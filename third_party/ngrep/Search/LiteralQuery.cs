using Ngrep.Indexing;
using Ngrep.Text;

namespace Ngrep.Search;

/// <summary>
/// Required literals for a pattern, in disjunctive normal form: any clause may match,
/// and every part inside a clause must occur. Extraction is conservative. Unsupported
/// syntax yields null, and the caller scans every candidate.
/// A clause can use the trigram index only when one of its parts is at least
/// <see cref="Trigrams.Length"/> characters. Shorter parts still reject files during
/// confirmation when they are ASCII, but they cannot drop index documents.
/// </summary>
internal sealed class LiteralQuery
{
    private const int MaxClauses = 64;

    private readonly Clause[] _clauses;
    private readonly ByteClause[] _byteClauses;
    private readonly SimdLiteralScanner? _anySensitive;
    private readonly SimdLiteralScanner? _anyInsensitive;
    private readonly SimdLiteralScanner[]? _eachSensitive;
    private readonly SimdLiteralScanner[]? _eachInsensitive;
    private readonly int[]? _orLengths;

    private LiteralQuery(Clause[] clauses)
    {
        _clauses = clauses;
        TrigramSafe = clauses.Length > 0 && clauses.All(static clause => clause.HasTrigram);
        HasLinePrefilter = clauses.Length > 0 && clauses.All(static clause => clause.HasSelectiveAscii);
        if (TryOrNeedles(clauses, out var orNeedles))
        {
            _byteClauses = [];
            _anySensitive = SimdLiteralScanner.Create(orNeedles, ignoreCase: false);
            _anyInsensitive = SimdLiteralScanner.Create(orNeedles, ignoreCase: true);
            _orLengths = new int[orNeedles.Count];
            for (var i = 0; i < orNeedles.Count; i++)
            {
                _orLengths[i] = orNeedles[i].Length;
            }

            if (orNeedles.Count > 1)
            {
                var sensitive = new SimdLiteralScanner[orNeedles.Count];
                var insensitive = new SimdLiteralScanner[orNeedles.Count];
                for (var i = 0; i < orNeedles.Count; i++)
                {
                    sensitive[i] = SimdLiteralScanner.Create([orNeedles[i]], ignoreCase: false);
                    insensitive[i] = SimdLiteralScanner.Create([orNeedles[i]], ignoreCase: true);
                }

                _eachSensitive = sensitive;
                _eachInsensitive = insensitive;
            }
            else
            {
                _eachSensitive = null;
                _eachInsensitive = null;
            }
        }
        else
        {
            _byteClauses = CompileClauses(clauses);
            _anySensitive = null;
            _anyInsensitive = null;
            _eachSensitive = null;
            _eachInsensitive = null;
            _orLengths = null;
        }
    }

    public bool TrigramSafe { get; }

    /// <summary>
    /// Every alternative has an ASCII literal of at least two bytes, so a line
    /// that lacks it cannot match. Length-1 literals are too common to use this way.
    /// </summary>
    public bool HasLinePrefilter { get; }

    /// <summary>
    /// OR of ASCII needles (a literal query, or <c>foo|bar</c>). <paramref name="each"/>
    /// is null when there is a single needle and <paramref name="any"/> is that scan.
    /// </summary>
    public bool TryAsciiOr(bool ignoreCase, out SimdLiteralScanner any, out SimdLiteralScanner[]? each, out int[] lengths)
    {
        if (_anySensitive == null || _orLengths == null || _orLengths.Length == 0)
        {
            any = null!;
            each = null;
            lengths = [];
            return false;
        }

        foreach (var length in _orLengths)
        {
            if (length == 0)
            {
                any = null!;
                each = null;
                lengths = [];
                return false;
            }
        }

        any = ignoreCase ? _anyInsensitive! : _anySensitive;
        each = ignoreCase ? _eachInsensitive : _eachSensitive;
        lengths = _orLengths;
        return true;
    }

    /// <summary>Marks lines that contain every selective literal of some clause.</summary>
    public void MarkLines(string text, int[] starts, int lineCount, bool ignoreCase, bool[] lines)
    {
        Array.Clear(lines, 0, lineCount);
        if (lineCount == 0)
        {
            return;
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var single = true;
        foreach (var clause in _clauses)
        {
            if (SelectiveParts(clause) != 1)
            {
                single = false;
                break;
            }
        }

        if (single)
        {
            foreach (var clause in _clauses)
            {
                foreach (var part in clause.Parts)
                {
                    if (part.Length >= 2 && IsAsciiText(part))
                    {
                        OrLines(text, starts, lineCount, part, comparison, lines);
                    }
                }
            }

            return;
        }

        foreach (var clause in _clauses)
        {
            bool[]? acc = null;
            foreach (var part in clause.Parts)
            {
                if (part.Length < 2 || !IsAsciiText(part))
                {
                    continue;
                }

                var present = new bool[lineCount];
                OrLines(text, starts, lineCount, part, comparison, present);
                if (acc == null)
                {
                    acc = present;
                    continue;
                }

                for (var i = 0; i < lineCount; i++)
                {
                    acc[i] &= present[i];
                }
            }

            if (acc == null)
            {
                Array.Fill(lines, true, 0, lineCount);
                return;
            }

            for (var i = 0; i < lineCount; i++)
            {
                lines[i] |= acc[i];
            }
        }
    }

    /// <summary>
    /// Start offsets of lines that contain the required ASCII literals. Sorted and unique.
    /// False when a clause cannot be decided from bytes alone.
    /// </summary>
    public bool CandidateLineStarts(ReadOnlySpan<byte> bytes, bool ignoreCase, List<int> starts)
    {
        starts.Clear();
        if (_orLengths != null)
        {
            var scanners = ignoreCase ? _eachInsensitive : _eachSensitive;
            var any = ignoreCase ? _anyInsensitive : _anySensitive;
            if (scanners == null)
            {
                AddLineStarts(bytes, any!, _orLengths[0], starts);
                return true;
            }

            List<int>? orUnion = null;
            for (var i = 0; i < scanners.Length; i++)
            {
                var found = new List<int>();
                AddLineStarts(bytes, scanners[i], _orLengths[i], found);
                orUnion = orUnion == null ? found : UnionSorted(orUnion, found);
            }

            if (orUnion != null)
            {
                starts.AddRange(orUnion);
            }

            return true;
        }

        List<int>? union = null;
        foreach (var clause in _byteClauses)
        {
            if (clause.Always)
            {
                return false;
            }

            List<int>? acc = null;
            var selective = 0;
            foreach (var part in clause.Parts)
            {
                if (part.Kind != ByteKind.Ascii || part.Length < 2 || part.Sensitive == null || part.Insensitive == null)
                {
                    continue;
                }

                selective++;
                var found = new List<int>();
                AddLineStarts(bytes, ignoreCase ? part.Insensitive : part.Sensitive, part.Length, found);
                acc = acc == null ? found : IntersectSorted(acc, found);
                if (acc.Count == 0)
                {
                    // A line must contain every required part, so an empty intersection
                    // stays empty. Skip the remaining scans for this clause.
                    break;
                }
            }

            if (selective == 0)
            {
                return false;
            }

            if (acc == null || acc.Count == 0)
            {
                continue;
            }

            union = union == null ? acc : UnionSorted(union, acc);
        }

        if (union != null)
        {
            starts.AddRange(union);
        }

        return true;
    }

    private static void AddLineStarts(ReadOnlySpan<byte> bytes, SimdLiteralScanner scanner, int length, List<int> starts)
    {
        var from = 0;
        var last = -1;
        while (from <= bytes.Length - length)
        {
            var rel = scanner.IndexOfAny(bytes[from..]);
            if (rel < 0)
            {
                return;
            }

            var index = from + rel;
            var line = index;
            while (line > 0 && bytes[line - 1] != (byte)'\n')
            {
                line--;
            }

            if (line != last)
            {
                starts.Add(line);
                last = line;
            }

            var newline = bytes[index..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                return;
            }

            from = index + newline + 1;
        }
    }

    private static List<int> IntersectSorted(List<int> left, List<int> right)
    {
        var acc = new List<int>(Math.Min(left.Count, right.Count));
        var i = 0;
        var j = 0;
        while (i < left.Count && j < right.Count)
        {
            var compare = left[i].CompareTo(right[j]);
            if (compare == 0)
            {
                acc.Add(left[i]);
                i++;
                j++;
            }
            else if (compare < 0)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return acc;
    }

    private static List<int> UnionSorted(List<int> left, List<int> right)
    {
        var acc = new List<int>(left.Count + right.Count);
        var i = 0;
        var j = 0;
        while (i < left.Count && j < right.Count)
        {
            if (left[i] == right[j])
            {
                acc.Add(left[i]);
                i++;
                j++;
            }
            else if (left[i] < right[j])
            {
                acc.Add(left[i]);
                i++;
            }
            else
            {
                acc.Add(right[j]);
                j++;
            }
        }

        for (; i < left.Count; i++)
        {
            acc.Add(left[i]);
        }

        for (; j < right.Count; j++)
        {
            acc.Add(right[j]);
        }

        return acc;
    }

    private static int SelectiveParts(Clause clause)
    {
        var count = 0;
        foreach (var part in clause.Parts)
        {
            if (part.Length >= 2 && IsAsciiText(part))
            {
                count++;
            }
        }

        return count;
    }

    private static void OrLines(string text, int[] starts, int lineCount, string part, StringComparison comparison, bool[] lines)
    {
        var from = 0;
        while (from < text.Length)
        {
            var idx = text.IndexOf(part, from, comparison);
            if (idx < 0)
            {
                return;
            }

            var line = LineMap.Map(starts, idx).Line;
            if ((uint)(line - 1) < (uint)lineCount)
            {
                lines[line - 1] = true;
            }

            var newline = text.IndexOf('\n', idx);
            from = newline < 0 ? text.Length : newline + 1;
        }
    }

    private static bool IsAsciiText(string text)
    {
        foreach (var ch in text)
        {
            if (ch > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    public string[][] Parts => _clauses.Select(static clause => clause.Parts).ToArray();

    public static LiteralQuery? FromPatterns(IReadOnlyList<string> patterns, bool regex)
    {
        if (!regex)
        {
            var clauses = new Clause[patterns.Count];
            for (var i = 0; i < patterns.Count; i++)
            {
                clauses[i] = Clause.FromExact(patterns[i]);
            }

            return new LiteralQuery(clauses);
        }

        var all = new List<Clause>();
        foreach (var pattern in patterns)
        {
            var extracted = Extract(pattern);
            if (extracted == null)
            {
                return null;
            }

            all.AddRange(extracted);
            if (all.Count > MaxClauses)
            {
                return null;
            }
        }

        return all.Count == 0 ? null : new LiteralQuery(all.ToArray());
    }

    public bool TryCollectGrams(HashSet<string> grams)
    {
        if (!TrigramSafe)
        {
            return false;
        }

        foreach (var clause in _clauses)
        {
            foreach (var part in clause.Parts)
            {
                if (part.Length < Trigrams.Length)
                {
                    continue;
                }

                foreach (var gram in Trigrams.ForQuery(part))
                {
                    grams.Add(gram);
                }
            }
        }

        return grams.Count > 0;
    }

    public bool TrySelect(Dictionary<string, List<int>> postings, out HashSet<int> ids)
    {
        ids = [];
        if (!TrigramSafe)
        {
            return false;
        }

        foreach (var clause in _clauses)
        {
            List<int>? acc = null;
            foreach (var part in clause.Parts)
            {
                if (part.Length < Trigrams.Length)
                {
                    continue;
                }

                var hit = Intersect(postings, Trigrams.ForQuery(part));
                if (acc == null)
                {
                    acc = hit;
                }
                else
                {
                    acc = IntersectSorted(acc, hit);
                }

                if (acc.Count == 0)
                {
                    break;
                }
            }

            if (acc == null)
            {
                ids = [];
                return false;
            }

            foreach (var id in acc)
            {
                ids.Add(id);
            }
        }

        return true;
    }

    /// <summary>
    /// True when the bytes cannot satisfy any clause. Non-ASCII parts are not rejected here.
    /// ASCII parts are a SIMD candidate scan; a hit still means the file must be decoded.
    /// </summary>
    public bool Misses(ReadOnlySpan<byte> utf8, bool ignoreCase)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 = utf8[3..];
        }

        if (_anySensitive != null)
        {
            var scanner = ignoreCase ? _anyInsensitive! : _anySensitive;
            return scanner.IndexOfAny(utf8) < 0;
        }

        foreach (var clause in _byteClauses)
        {
            if (ClauseHits(utf8, clause, ignoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ClauseHits(ReadOnlySpan<byte> utf8, ByteClause clause, bool ignoreCase)
    {
        if (clause.Always)
        {
            return true;
        }

        foreach (var part in clause.Parts)
        {
            if (part.Kind == ByteKind.Empty)
            {
                continue;
            }

            if (part.Kind == ByteKind.NonAscii)
            {
                return true;
            }

            var scanner = ignoreCase ? part.Insensitive : part.Sensitive;
            if (scanner!.IndexOfAny(utf8) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryOrNeedles(Clause[] clauses, out List<byte[]> needles)
    {
        needles = [];
        if (clauses.Length == 0)
        {
            return false;
        }

        foreach (var clause in clauses)
        {
            if (clause.Parts.Length != 1 || !TryAscii(clause.Parts[0], out var bytes))
            {
                return false;
            }

            needles.Add(bytes);
        }

        return true;
    }

    private static ByteClause[] CompileClauses(Clause[] clauses)
    {
        var compiled = new ByteClause[clauses.Length];
        for (var c = 0; c < clauses.Length; c++)
        {
            var parts = clauses[c].Parts;
            if (parts.Length == 0)
            {
                compiled[c] = ByteClause.AlwaysHit;
                continue;
            }

            var allAscii = true;
            var byteParts = new List<BytePart>(parts.Length);
            foreach (var part in parts)
            {
                if (part.Length == 0)
                {
                    continue;
                }

                if (!TryAscii(part, out var bytes))
                {
                    allAscii = false;
                    byteParts.Add(BytePart.NonAsciiPart);
                    continue;
                }

                byteParts.Add(BytePart.Ascii(
                    SimdLiteralScanner.Create([bytes], ignoreCase: false),
                    SimdLiteralScanner.Create([bytes], ignoreCase: true),
                    bytes.Length));
            }

            if (allAscii && byteParts.Count > 1)
            {
                byteParts.Sort(static (left, right) => right.Length.CompareTo(left.Length));
            }

            compiled[c] = new ByteClause(false, byteParts.ToArray());
        }

        return compiled;
    }

    private static bool TryAscii(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0)
        {
            return false;
        }

        var raw = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7F)
            {
                return false;
            }

            raw[i] = (byte)text[i];
        }

        bytes = raw;
        return true;
    }

    private enum ByteKind : byte
    {
        Empty,
        NonAscii,
        Ascii,
    }

    private readonly struct BytePart
    {
        private BytePart(ByteKind kind, int length, SimdLiteralScanner? sensitive, SimdLiteralScanner? insensitive)
        {
            Kind = kind;
            Length = length;
            Sensitive = sensitive;
            Insensitive = insensitive;
        }

        public ByteKind Kind { get; }

        public int Length { get; }

        public SimdLiteralScanner? Sensitive { get; }

        public SimdLiteralScanner? Insensitive { get; }

        public static BytePart NonAsciiPart { get; } = new(ByteKind.NonAscii, 0, null, null);

        public static BytePart Ascii(SimdLiteralScanner sensitive, SimdLiteralScanner insensitive, int length) =>
            new(ByteKind.Ascii, length, sensitive, insensitive);
    }

    private readonly struct ByteClause
    {
        public ByteClause(bool always, BytePart[] parts)
        {
            Always = always;
            Parts = parts;
        }

        public bool Always { get; }

        public BytePart[] Parts { get; }

        public static ByteClause AlwaysHit { get; } = new(true, []);
    }

    private static List<int> Intersect(Dictionary<string, List<int>> postings, List<string> grams)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<List<int>>(grams.Count);
        foreach (var gram in grams)
        {
            if (!seen.Add(gram))
            {
                continue;
            }

            if (!postings.TryGetValue(gram, out var list) || list.Count == 0)
            {
                return [];
            }

            ordered.Add(list);
        }

        // Rarest list first so a selective gram does not hash every document id.
        ordered.Sort(static (left, right) => left.Count.CompareTo(right.Count));
        List<int>? acc = null;
        foreach (var list in ordered)
        {
            acc = acc == null ? list : IntersectSorted(acc, list);
            if (acc.Count == 0)
            {
                return acc;
            }
        }

        return acc ?? [];
    }

    private static Clause[]? Extract(string pattern)
    {
        var parser = new Parser(pattern);
        var node = parser.ParseAlt(stopAtGroup: false);
        if (node == null || parser.Failed || parser.Index != pattern.Length)
        {
            return null;
        }

        var analysis = Analyze(node);
        if (analysis == null || analysis.Choices.Length == 0 || analysis.Choices.Length > MaxClauses)
        {
            return null;
        }

        return analysis.Choices;
    }

    private static Analysis? Analyze(Node node)
    {
        switch (node)
        {
            case Lit lit:
                return Analysis.Exact(lit.Text);
            case Any:
                return Analysis.AnyChar;
            case Empty:
                return Analysis.EmptyMatch;
            case Cat cat:
                Analysis? folded = Analysis.EmptyMatch;
                foreach (var part in cat.Parts)
                {
                    var next = Analyze(part);
                    if (next == null || folded == null)
                    {
                        return null;
                    }

                    folded = Analysis.Concat(folded, next);
                    if (folded == null)
                    {
                        return null;
                    }
                }

                return folded;
            case Alt alt:
                Analysis? union = null;
                foreach (var part in alt.Parts)
                {
                    var next = Analyze(part);
                    if (next == null)
                    {
                        return null;
                    }

                    union = union == null ? next : Analysis.Or(union, next);
                    if (union == null)
                    {
                        return null;
                    }
                }

                return union ?? Analysis.EmptyMatch;
            case Rep rep:
                var inner = Analyze(rep.Inner);
                if (inner == null)
                {
                    return null;
                }

                return Analysis.Repeat(inner, rep.Min);
            default:
                return null;
        }
    }

    private sealed class Clause
    {
        private Clause(string[] parts, string? exact, string? suffix)
        {
            Parts = parts;
            Exact = exact;
            Suffix = suffix;
        }

        public string[] Parts { get; }

        public string? Exact { get; }

        /// <summary>Exact text the match ends with, when the next literal can be glued on. Null when a gap or any-char sits at the end.</summary>
        public string? Suffix { get; }

        public bool HasTrigram
        {
            get
            {
                foreach (var part in Parts)
                {
                    if (part.Length >= Trigrams.Length)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>An ASCII piece of at least two characters can reject a line on its own.</summary>
        public bool HasSelectiveAscii
        {
            get
            {
                foreach (var part in Parts)
                {
                    if (part.Length >= 2 && IsAsciiText(part))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public static Clause FromExact(string text)
        {
            return text.Length == 0 ? new Clause([], "", "") : new Clause([text], text, text);
        }

        public static Clause Gap() => new([], null, null);

        public static Clause Concat(Clause left, Clause right)
        {
            if (left.Exact != null && right.Exact != null)
            {
                return FromExact(left.Exact + right.Exact);
            }

            if (right.Exact != null && left.Suffix != null)
            {
                if (left.Parts.Length == 0)
                {
                    return FromExact(left.Suffix + right.Exact);
                }

                var extended = (string[])left.Parts.Clone();
                extended[^1] += right.Exact;
                return new Clause(extended, null, left.Suffix + right.Exact);
            }

            if (right.Exact != null)
            {
                var added = new string[left.Parts.Length + (right.Exact.Length == 0 ? 0 : 1)];
                left.Parts.CopyTo(added, 0);
                if (right.Exact.Length > 0)
                {
                    added[^1] = right.Exact;
                }

                return new Clause(added, null, right.Exact);
            }

            var count = left.Parts.Length + right.Parts.Length;
            if (count == 0)
            {
                return new Clause([], null, right.Suffix);
            }

            var parts = new string[count];
            left.Parts.CopyTo(parts, 0);
            right.Parts.CopyTo(parts, left.Parts.Length);
            return new Clause(parts, null, right.Suffix);
        }
    }

    private sealed class Analysis
    {
        private Analysis(bool nullable, Clause[] choices)
        {
            Nullable = nullable;
            Choices = choices;
        }

        public bool Nullable { get; }

        public Clause[] Choices { get; }

        public static Analysis EmptyMatch { get; } = new(true, [Clause.FromExact("")]);

        public static Analysis AnyChar { get; } = new(false, [Clause.Gap()]);

        public static Analysis NullableGap { get; } = new(true, [Clause.Gap()]);

        public static Analysis Exact(string text)
        {
            var clause = Clause.FromExact(text);
            return new Analysis(text.Length == 0, [clause]);
        }

        public static Analysis? Concat(Analysis left, Analysis right)
        {
            var count = left.Choices.Length * right.Choices.Length;
            if (count > MaxClauses)
            {
                return null;
            }

            var choices = new Clause[count];
            var index = 0;
            foreach (var first in left.Choices)
            {
                foreach (var second in right.Choices)
                {
                    choices[index++] = Clause.Concat(first, second);
                }
            }

            return new Analysis(left.Nullable && right.Nullable, choices);
        }

        public static Analysis? Or(Analysis left, Analysis right)
        {
            var count = left.Choices.Length + right.Choices.Length;
            if (count > MaxClauses)
            {
                return null;
            }

            var choices = new Clause[count];
            left.Choices.CopyTo(choices, 0);
            right.Choices.CopyTo(choices, left.Choices.Length);
            return new Analysis(left.Nullable || right.Nullable, choices);
        }

        public static Analysis Repeat(Analysis inner, int min)
        {
            if (min <= 0 || inner.Nullable)
            {
                // A repeat that can match empty may also consume, so neighbors must not be glued together.
                return NullableGap;
            }

            if (inner.Choices.Length == 1 && inner.Choices[0].Exact != null)
            {
                var piece = inner.Choices[0].Exact!;
                if (piece.Length == 0)
                {
                    return EmptyMatch;
                }

                if ((long)piece.Length * min <= 256)
                {
                    return Exact(string.Concat(Enumerable.Repeat(piece, min)));
                }
            }

            return new Analysis(false, inner.Choices);
        }
    }

    private abstract record Node;

    private sealed record Lit(string Text) : Node;

    private sealed record Any : Node;

    private sealed record Empty : Node;

    private sealed record Cat(Node[] Parts) : Node;

    private sealed record Alt(Node[] Parts) : Node;

    private sealed record Rep(Node Inner, int Min) : Node;

    private struct Parser
    {
        private readonly string _pattern;

        public Parser(string pattern) => _pattern = pattern;

        public int Index { get; private set; }

        public bool Failed { get; private set; }

        public Node? ParseAlt(bool stopAtGroup)
        {
            var parts = new List<Node>();
            var first = ParseConcat(stopAtGroup);
            if (first == null)
            {
                return null;
            }

            parts.Add(first);
            while (!Failed && Index < _pattern.Length && _pattern[Index] == '|')
            {
                Index++;
                var next = ParseConcat(stopAtGroup);
                if (next == null)
                {
                    return null;
                }

                parts.Add(next);
            }

            return parts.Count == 1 ? parts[0] : new Alt(parts.ToArray());
        }

        private Node? ParseConcat(bool stopAtGroup)
        {
            var parts = new List<Node>();
            while (!Failed && Index < _pattern.Length)
            {
                var ch = _pattern[Index];
                if (ch == '|' || (stopAtGroup && ch == ')'))
                {
                    break;
                }

                var quant = ParseQuant();
                if (quant == null)
                {
                    return null;
                }

                parts.Add(quant);
            }

            if (parts.Count == 0)
            {
                return new Empty();
            }

            return parts.Count == 1 ? parts[0] : new Cat(parts.ToArray());
        }

        private Node? ParseQuant()
        {
            var atom = ParseAtom();
            if (atom == null)
            {
                return null;
            }

            if (Index >= _pattern.Length)
            {
                return atom;
            }

            var ch = _pattern[Index];
            int min;
            if (ch == '*')
            {
                min = 0;
                Index++;
            }
            else if (ch == '+')
            {
                min = 1;
                Index++;
            }
            else if (ch == '?')
            {
                min = 0;
                Index++;
            }
            else if (ch == '{' && TryParseBrace(out min))
            {
                // consumed
            }
            else
            {
                return atom;
            }

            if (Index < _pattern.Length && _pattern[Index] == '?')
            {
                Index++;
            }

            return new Rep(atom, min);
        }

        private bool TryParseBrace(out int min)
        {
            min = 0;
            if (Index >= _pattern.Length || _pattern[Index] != '{')
            {
                return false;
            }

            var cursor = Index + 1;
            if (!TryDigits(ref cursor, out var first))
            {
                return false;
            }

            int? max = first;
            var lower = first;
            if (cursor < _pattern.Length && _pattern[cursor] == ',')
            {
                cursor++;
                if (cursor < _pattern.Length && char.IsDigit(_pattern[cursor]))
                {
                    if (!TryDigits(ref cursor, out var upper))
                    {
                        return false;
                    }

                    max = upper;
                }
                else
                {
                    max = null;
                }
            }

            if (cursor >= _pattern.Length || _pattern[cursor] != '}' || (max != null && max < lower))
            {
                return false;
            }

            Index = cursor + 1;
            min = lower;
            return true;
        }

        private bool TryDigits(ref int cursor, out int value)
        {
            value = 0;
            if (cursor >= _pattern.Length || !char.IsDigit(_pattern[cursor]))
            {
                return false;
            }

            var any = false;
            while (cursor < _pattern.Length && char.IsDigit(_pattern[cursor]))
            {
                any = true;
                value = (value * 10) + (_pattern[cursor] - '0');
                cursor++;
                if (value > 10_000)
                {
                    return false;
                }
            }

            return any;
        }

        private Node? ParseAtom()
        {
            if (Failed || Index >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            var ch = _pattern[Index++];
            switch (ch)
            {
                case '.':
                    return new Any();
                case '^':
                case '$':
                    return new Empty();
                case '(':
                    return ParseGroup();
                case '[':
                    return ParseClass();
                case '\\':
                    return ParseEscape();
                case ')' or '*' or '+' or '?' or '|':
                    Failed = true;
                    return null;
                default:
                    return new Lit(ch.ToString());
            }
        }

        private Node? ParseGroup()
        {
            if (Index < _pattern.Length && _pattern[Index] == '?')
            {
                if (Index + 1 < _pattern.Length && _pattern[Index + 1] == ':')
                {
                    Index += 2;
                }
                else
                {
                    Failed = true;
                    return null;
                }
            }

            var body = ParseAlt(stopAtGroup: true);
            if (body == null || Failed || Index >= _pattern.Length || _pattern[Index] != ')')
            {
                Failed = true;
                return null;
            }

            Index++;
            return body;
        }

        private Node? ParseClass()
        {
            if (Index >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            var negated = false;
            if (_pattern[Index] == '^')
            {
                negated = true;
                Index++;
            }

            var chars = new List<char>();
            var ranged = false;
            var first = true;
            var closed = false;
            while (Index < _pattern.Length)
            {
                var ch = _pattern[Index++];
                if (ch == ']' && !first)
                {
                    closed = true;
                    break;
                }

                first = false;
                if (ch == '\\')
                {
                    var escaped = ParseClassEscape();
                    if (escaped == null)
                    {
                        SkipClass();
                        return Failed ? null : new Any();
                    }

                    chars.Add(escaped.Value);
                    continue;
                }

                if (ch == '-' && chars.Count > 0 && Index < _pattern.Length && _pattern[Index] != ']')
                {
                    ranged = true;
                    continue;
                }

                chars.Add(ch);
            }

            if (!closed)
            {
                Failed = true;
                return null;
            }

            if (!negated && !ranged && chars.Count == 1)
            {
                return new Lit(chars[0].ToString());
            }

            return new Any();
        }

        private void SkipClass()
        {
            while (Index < _pattern.Length)
            {
                var ch = _pattern[Index++];
                if (ch == '\\' && Index < _pattern.Length)
                {
                    Index++;
                    continue;
                }

                if (ch == ']')
                {
                    return;
                }
            }

            Failed = true;
        }

        private char? ParseClassEscape()
        {
            if (Index >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            var ch = _pattern[Index++];
            if (ch is 'd' or 'D' or 'w' or 'W' or 's' or 'S' or 'p' or 'P')
            {
                if (ch is 'p' or 'P')
                {
                    SkipProperty();
                }

                return null;
            }

            return DecodeEscapeChar(ch);
        }

        private Node? ParseEscape()
        {
            if (Index >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            var ch = _pattern[Index++];
            switch (ch)
            {
                case 'd' or 'D' or 's' or 'S' or 'w' or 'W':
                    return new Any();
                case 'b' or 'B' or 'A' or 'z' or 'Z' or 'G':
                    return new Empty();
                case 'p' or 'P':
                    if (!SkipProperty())
                    {
                        Failed = true;
                        return null;
                    }

                    return new Any();
                case >= '0' and <= '9':
                    Failed = true;
                    return null;
                default:
                    var decoded = DecodeEscapeChar(ch);
                    if (decoded == null)
                    {
                        Failed = true;
                        return null;
                    }

                    return new Lit(decoded.Value.ToString());
            }
        }

        private bool SkipProperty()
        {
            if (Index >= _pattern.Length || _pattern[Index] != '{')
            {
                return false;
            }

            var end = _pattern.IndexOf('}', Index + 1);
            if (end < 0)
            {
                return false;
            }

            Index = end + 1;
            return true;
        }

        private char? DecodeEscapeChar(char ch)
        {
            switch (ch)
            {
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                case 'a':
                    return '\a';
                case 'f':
                    return '\f';
                case 'v':
                    return '\v';
                case 'e':
                    return '\x1B';
                case 'x':
                    return ParseHex(2);
                case 'u':
                    return ParseHex(4);
                case 'c':
                    if (Index >= _pattern.Length)
                    {
                        return null;
                    }

                    return (char)(_pattern[Index++] & 0x1F);
                default:
                    return ch;
            }
        }

        private char? ParseHex(int digits)
        {
            if (Index + digits > _pattern.Length)
            {
                return null;
            }

            var value = 0;
            for (var i = 0; i < digits; i++)
            {
                var hex = _pattern[Index++];
                value <<= 4;
                if (hex is >= '0' and <= '9')
                {
                    value += hex - '0';
                }
                else if (hex is >= 'a' and <= 'f')
                {
                    value += hex - 'a' + 10;
                }
                else if (hex is >= 'A' and <= 'F')
                {
                    value += hex - 'A' + 10;
                }
                else
                {
                    return null;
                }
            }

            return (char)value;
        }
    }
}
