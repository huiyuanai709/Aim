using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Ngrep.Text;

namespace Ngrep.Search;

/// <summary>
/// Linear byte matcher for the ASCII subset of .NET regex.
/// A Thompson NFA is searched with a Pike VM (one pass, no backtracking).
/// Literal prefixes, literal alternations, and single-class runs skip the VM.
/// Spans follow <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/>
/// on ASCII text. Patterns this compiler does not recognize return false so the
/// caller can run that engine instead.
/// </summary>
internal static class ByteAutomaton
{
    public readonly record struct Options(bool IgnoreCase, bool DotAll);

    public static bool TryCompile(string pattern, Options options, out ByteProgram program)
    {
        program = null!;
        if (pattern.Length == 0 || pattern.Length > 4096)
        {
            return false;
        }

        foreach (var ch in pattern)
        {
            if (ch > 0x7F)
            {
                return false;
            }
        }

        var parser = new Parser(pattern, options.IgnoreCase, options.DotAll);
        var ast = parser.ParseAlt(stopAtGroup: false);
        if (ast == null || parser.Failed || parser.Index != pattern.Length)
        {
            return false;
        }

        ast = Simplify(ast);
        if (TryLiteral(ast, options.IgnoreCase, out program))
        {
            return true;
        }

        if (TryLiteralAlt(ast, options.IgnoreCase, out program))
        {
            return true;
        }

        if (TryClassRun(ast, out var run) && !run.IncludesNewline)
        {
            program = run;
            return true;
        }

        if (TrySequence(ast, options.IgnoreCase, out program))
        {
            return true;
        }

        if (!TryPike(ast, options.IgnoreCase, out program))
        {
            return false;
        }

        return true;
    }

    private static bool TryPike(Re ast, bool ignoreCase, out ByteProgram program)
    {
        var compiler = new PikeCompiler();
        if (!compiler.Build(ast, out var insts, out var anchored, out var unanchored, out var prefix))
        {
            program = null!;
            return false;
        }

        var pike = new PikeProgram(insts, anchored, unanchored, prefix, ignoreCase);
        program = DfaProgram.Wrap(pike, insts, unanchored, prefix) ?? pike;
        return true;
    }

    private static bool TryLiteral(Re ast, bool ignoreCase, out ByteProgram program)
    {
        if (ast is ReLit lit && lit.Bytes.Length > 0)
        {
            program = new LiteralProgram(lit.Bytes, ignoreCase);
            return true;
        }

        program = null!;
        return false;
    }

    private static bool TryLiteralAlt(Re ast, bool ignoreCase, out ByteProgram program)
    {
        if (ast is not ReAlt alt || alt.Parts.Length < 2)
        {
            program = null!;
            return false;
        }

        var needles = new byte[alt.Parts.Length][];
        for (var i = 0; i < alt.Parts.Length; i++)
        {
            if (alt.Parts[i] is not ReLit lit || lit.Bytes.Length == 0)
            {
                program = null!;
                return false;
            }

            needles[i] = lit.Bytes;
        }

        program = new LiteralAltProgram(needles, ignoreCase);
        return true;
    }

    private static bool TryClassRun(Re ast, out ClassRunProgram program)
    {
        if (TryClass(ast, out var m0, out var m1, out var m2, out var m3, out var includesNewline))
        {
            program = new ClassRunProgram(m0, m1, m2, m3, includesNewline, min: 1, max: 1, lazy: false);
            return true;
        }

        if (ast is ReRep rep && rep.Min >= 1 && TryClass(rep.Inner, out m0, out m1, out m2, out m3, out includesNewline))
        {
            program = new ClassRunProgram(m0, m1, m2, m3, includesNewline, rep.Min, rep.Max, rep.Lazy);
            return true;
        }

        program = null!;
        return false;
    }

    private static bool TrySequence(Re ast, bool ignoreCase, out ByteProgram program)
    {
        if (ast is not ReCat cat)
        {
            program = null!;
            return false;
        }

        var atoms = new List<SeqAtom>(cat.Parts.Length);
        foreach (var part in cat.Parts)
        {
            if (part is ReLit lit)
            {
                if (lit.Bytes.Length == 0)
                {
                    continue;
                }

                atoms.Add(SeqAtom.FromLiteral(lit.Bytes));
                continue;
            }

            if (!TryClassQuant(part, out var m0, out var m1, out var m2, out var m3, out var newline, out var min, out var max, out var lazy))
            {
                program = null!;
                return false;
            }

            atoms.Add(SeqAtom.Class(m0, m1, m2, m3, newline, min, max, lazy));
        }

        if (atoms.Count == 0 || atoms[0].Literal == null || atoms[0].Literal!.Length < 2)
        {
            program = null!;
            return false;
        }

        for (var i = 0; i < atoms.Count - 1; i++)
        {
            if (atoms[i].Literal != null)
            {
                continue;
            }

            var next = atoms[i + 1];
            if (next.Literal == null)
            {
                program = null!;
                return false;
            }

            var exact = atoms[i].Min == atoms[i].Max;
            if (exact)
            {
                continue;
            }

            if (atoms[i].Lazy)
            {
                program = null!;
                return false;
            }

            var first = next.Literal![0];
            if (MaskHas(atoms[i].M0, atoms[i].M1, atoms[i].M2, atoms[i].M3, first))
            {
                program = null!;
                return false;
            }

            if (ignoreCase && IsLetter(first))
            {
                var folded = (byte)(first ^ 0x20);
                if (MaskHas(atoms[i].M0, atoms[i].M1, atoms[i].M2, atoms[i].M3, folded))
                {
                    program = null!;
                    return false;
                }
            }
        }

        program = new SequenceProgram(atoms.ToArray(), ignoreCase);
        return true;
    }

    private static bool TryClassQuant(Re ast, out ulong m0, out ulong m1, out ulong m2, out ulong m3, out bool newline, out int min, out int max, out bool lazy)
    {
        if (ast is ReRep rep && TryClass(rep.Inner, out m0, out m1, out m2, out m3, out newline))
        {
            min = rep.Min;
            max = rep.Max;
            lazy = rep.Lazy;
            return true;
        }

        if (TryClass(ast, out m0, out m1, out m2, out m3, out newline))
        {
            min = 1;
            max = 1;
            lazy = false;
            return true;
        }

        min = max = 0;
        lazy = false;
        return false;
    }

    private static bool TryClass(Re ast, out ulong m0, out ulong m1, out ulong m2, out ulong m3, out bool newline)
    {
        if (ast is ReClass cls)
        {
            m0 = cls.M0;
            m1 = cls.M1;
            m2 = cls.M2;
            m3 = cls.M3;
            newline = cls.IncludesNewline;
            return true;
        }

        if (ast is ReLit lit && lit.Bytes.Length == 1)
        {
            m0 = m1 = m2 = m3 = 0;
            Set(ref m0, ref m1, ref m2, ref m3, lit.Bytes[0]);
            newline = lit.Bytes[0] == (byte)'\n';
            return true;
        }

        m0 = m1 = m2 = m3 = 0;
        newline = false;
        return false;
    }

    internal static bool MaskHas(ulong m0, ulong m1, ulong m2, ulong m3, byte value)
    {
        var bit = 1UL << (value & 63);
        return value switch
        {
            < 64 => (m0 & bit) != 0,
            < 128 => (m1 & bit) != 0,
            < 192 => (m2 & bit) != 0,
            _ => (m3 & bit) != 0,
        };
    }

    internal static void Set(ref ulong m0, ref ulong m1, ref ulong m2, ref ulong m3, int value)
    {
        if ((uint)value > 255)
        {
            return;
        }

        var bit = 1UL << (value & 63);
        switch (value)
        {
            case < 64:
                m0 |= bit;
                break;
            case < 128:
                m1 |= bit;
                break;
            case < 192:
                m2 |= bit;
                break;
            default:
                m3 |= bit;
                break;
        }
    }

    internal static bool IsLetter(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    internal static bool Equal(ReadOnlySpan<byte> text, ReadOnlySpan<byte> needle, bool ignoreCase)
    {
        if (needle.Length > text.Length)
        {
            return false;
        }

        for (var i = 0; i < needle.Length; i++)
        {
            var left = text[i];
            var right = needle[i];
            if (left == right)
            {
                continue;
            }

            if (!ignoreCase || !IsLetter(left) || !IsLetter(right) || (left ^ right) != 0x20)
            {
                return false;
            }
        }

        return true;
    }

    private static Re Simplify(Re ast)
    {
        // Alternation is associative, so a nested alt can be flattened into its parent.
        // Doing it here lets `foo|(bar|baz)` reach TryLiteralAlt as a literal alternation
        // instead of falling through to the (much slower) Pike VM.
        if (ast is ReAlt alt)
        {
            var alternatives = new List<Re>(alt.Parts.Length);
            var changed = false;
            foreach (var part in alt.Parts)
            {
                var simplified = Simplify(part);
                if (simplified is ReAlt nested)
                {
                    alternatives.AddRange(nested.Parts);
                    changed = true;
                    continue;
                }

                changed |= !ReferenceEquals(simplified, part);
                alternatives.Add(simplified);
            }

            return changed ? new ReAlt(alternatives.ToArray()) : ast;
        }

        if (ast is not ReCat cat)
        {
            return ast;
        }

        var parts = new List<Re>(cat.Parts.Length);
        foreach (var part in cat.Parts)
        {
            var simplified = Simplify(part);
            if (simplified is ReLit lit && parts.Count > 0 && parts[^1] is ReLit prev)
            {
                var merged = new byte[prev.Bytes.Length + lit.Bytes.Length];
                prev.Bytes.CopyTo(merged, 0);
                lit.Bytes.CopyTo(merged, prev.Bytes.Length);
                parts[^1] = new ReLit(merged);
                continue;
            }

            parts.Add(simplified);
        }

        return parts.Count == 1 ? parts[0] : new ReCat(parts.ToArray());
    }

    private sealed class Parser
    {
        private readonly string _pattern;
        private readonly bool _fold;
        private readonly bool _dotAll;

        public Parser(string pattern, bool fold, bool dotAll)
        {
            _pattern = pattern;
            _fold = fold;
            _dotAll = dotAll;
        }

        public int Index { get; private set; }

        public bool Failed { get; private set; }

        public Re? ParseAlt(bool stopAtGroup)
        {
            var parts = new List<Re>();
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
                    Failed = true;
                    return null;
                }

                parts.Add(next);
            }

            return parts.Count == 1 ? parts[0] : new ReAlt(parts.ToArray());
        }

        private Re? ParseConcat(bool stopAtGroup)
        {
            var parts = new List<Re>();
            while (Index < _pattern.Length)
            {
                var ch = _pattern[Index];
                if (ch == '|' || (stopAtGroup && ch == ')'))
                {
                    break;
                }

                var atom = ParseRepeat();
                if (atom == null)
                {
                    Failed = true;
                    return null;
                }

                parts.Add(atom);
            }

            if (parts.Count == 0)
            {
                return new ReEmpty();
            }

            return parts.Count == 1 ? parts[0] : new ReCat(parts.ToArray());
        }

        private Re? ParseRepeat()
        {
            var atom = ParseAtom();
            if (atom == null || Index >= _pattern.Length)
            {
                return atom;
            }

            var ch = _pattern[Index];
            if (ch is not ('*' or '+' or '?' or '{'))
            {
                return atom;
            }

            if (atom is ReAnchor or ReEmpty)
            {
                Failed = true;
                return null;
            }

            int min;
            int max;
            if (ch == '*')
            {
                min = 0;
                max = -1;
                Index++;
            }
            else if (ch == '+')
            {
                min = 1;
                max = -1;
                Index++;
            }
            else if (ch == '?')
            {
                min = 0;
                max = 1;
                Index++;
            }
            else if (!TryQuantifier(out min, out max))
            {
                // A '{' that is not a quantifier is a literal in .NET. It belongs to the next atom.
                return atom;
            }

            var lazy = false;
            if (Index < _pattern.Length && _pattern[Index] == '?')
            {
                lazy = true;
                Index++;
            }

            if (min > 48 || (max >= 0 && (max < min || max > 96 || max - min > 48)))
            {
                Failed = true;
                return null;
            }

            return new ReRep(atom, min, max, lazy);
        }

        private bool TryQuantifier(out int min, out int max)
        {
            min = 0;
            max = 0;
            var start = Index;
            if (Index >= _pattern.Length || _pattern[Index] != '{')
            {
                return false;
            }

            var i = Index + 1;
            if (!TryNumber(ref i, out min))
            {
                return false;
            }

            if (i < _pattern.Length && _pattern[i] == ',')
            {
                i++;
                if (i < _pattern.Length && _pattern[i] == '}')
                {
                    max = -1;
                    i++;
                }
                else if (!TryNumber(ref i, out max) || i >= _pattern.Length || _pattern[i] != '}')
                {
                    return false;
                }
                else
                {
                    i++;
                }
            }
            else if (i < _pattern.Length && _pattern[i] == '}')
            {
                max = min;
                i++;
            }
            else
            {
                return false;
            }

            Index = i;
            _ = start;
            return true;
        }

        private bool TryNumber(ref int i, out int value)
        {
            value = 0;
            if (i >= _pattern.Length || !char.IsDigit(_pattern[i]))
            {
                return false;
            }

            var any = false;
            while (i < _pattern.Length && char.IsDigit(_pattern[i]))
            {
                any = true;
                value = (value * 10) + (_pattern[i] - '0');
                if (value > 1000)
                {
                    return false;
                }

                i++;
            }

            return any;
        }

        private Re? ParseAtom()
        {
            if (Index >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            var ch = _pattern[Index];
            switch (ch)
            {
                case '.':
                    Index++;
                    return new ReClass(DotMask(_dotAll), !_dotAll ? false : true);
                case '^':
                    Index++;
                    return new ReAnchor(AnchorKind.Bol);
                case '$':
                    Index++;
                    return new ReAnchor(AnchorKind.Eol);
                case '(':
                    return ParseGroup();
                case '[':
                    return ParseClass();
                case '\\':
                    return ParseEscape(inClass: false);
                case ')' or '|' or '*' or '+' or '?' or ']':
                    Failed = true;
                    return null;
                default:
                    Index++;
                    return Lit((byte)ch);
            }
        }

        private Re? ParseGroup()
        {
            Index++;
            if (Index >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            if (_pattern[Index] == '?')
            {
                Index++;
                if (Index < _pattern.Length && _pattern[Index] == ':')
                {
                    Index++;
                }
                else
                {
                    Failed = true;
                    return null;
                }
            }

            var inner = ParseAlt(stopAtGroup: true);
            if (inner == null || Index >= _pattern.Length || _pattern[Index] != ')')
            {
                Failed = true;
                return null;
            }

            Index++;
            return inner;
        }

        private Re? ParseClass()
        {
            Index++;
            var negate = false;
            if (Index < _pattern.Length && _pattern[Index] == '^')
            {
                negate = true;
                Index++;
            }

            ulong m0 = 0, m1 = 0, m2 = 0, m3 = 0;
            var any = false;
            var first = true;
            while (Index < _pattern.Length)
            {
                if (_pattern[Index] == ']' && !first)
                {
                    break;
                }

                first = false;
                int lo;
                if (_pattern[Index] == '\\')
                {
                    var cls = ParseEscape(inClass: true);
                    if (cls is ReClass many)
                    {
                        m0 |= many.M0;
                        m1 |= many.M1;
                        m2 |= many.M2;
                        m3 |= many.M3;
                        any = true;
                        continue;
                    }

                    if (cls is not ReLit lit || lit.Bytes.Length != 1)
                    {
                        Failed = true;
                        return null;
                    }

                    lo = lit.Bytes[0];
                }
                else
                {
                    lo = (byte)_pattern[Index];
                    Index++;
                }

                if (Index + 1 < _pattern.Length && _pattern[Index] == '-' && _pattern[Index + 1] != ']')
                {
                    Index++;
                    int hi;
                    if (_pattern[Index] == '\\')
                    {
                        var cls = ParseEscape(inClass: true);
                        if (cls is not ReLit lit || lit.Bytes.Length != 1)
                        {
                            Failed = true;
                            return null;
                        }

                        hi = lit.Bytes[0];
                    }
                    else
                    {
                        hi = (byte)_pattern[Index];
                        Index++;
                    }

                    if (hi < lo)
                    {
                        Failed = true;
                        return null;
                    }

                    for (var b = lo; b <= hi; b++)
                    {
                        AddByte(ref m0, ref m1, ref m2, ref m3, (byte)b);
                    }
                }
                else
                {
                    AddByte(ref m0, ref m1, ref m2, ref m3, (byte)lo);
                }

                any = true;
            }

            if (!any || Index >= _pattern.Length || _pattern[Index] != ']')
            {
                Failed = true;
                return null;
            }

            Index++;
            if (negate)
            {
                m0 = ~m0;
                m1 = ~m1;
                m2 = ~m2;
                m3 = ~m3;
            }

            return new ReClass((m0, m1, m2, m3), MaskHas(m0, m1, m2, m3, (byte)'\n'));
        }

        private Re? ParseEscape(bool inClass)
        {
            if (Index >= _pattern.Length || _pattern[Index] != '\\' || Index + 1 >= _pattern.Length)
            {
                Failed = true;
                return null;
            }

            Index++;
            var ch = _pattern[Index++];
            switch (ch)
            {
                case 'd':
                    return new ReClass(RangeMask((byte)'0', (byte)'9'), false);
                case 'D':
                    return Negate(RangeMask((byte)'0', (byte)'9'));
                case 'w':
                    return new ReClass(WordMask(), false);
                case 'W':
                    return Negate(WordMask());
                case 's':
                    return new ReClass(SpaceMask(), true);
                case 'S':
                    return Negate(SpaceMask());
                case 'n':
                    return Lit((byte)'\n');
                case 'r':
                    return Lit((byte)'\r');
                case 't':
                    return Lit((byte)'\t');
                case 'f':
                    return Lit((byte)'\f');
                case 'v':
                    return Lit((byte)'\v');
                case 'a':
                    return Lit(0x07);
                case 'e':
                    return Lit(0x1B);
                case 'A' when !inClass:
                    return new ReAnchor(AnchorKind.Bos);
                case 'z' when !inClass:
                    return new ReAnchor(AnchorKind.Eos);
                case 'Z' when !inClass:
                    return new ReAnchor(AnchorKind.Eow);
                case 'b' when !inClass:
                    return new ReAnchor(AnchorKind.Word);
                case 'B' when !inClass:
                    return new ReAnchor(AnchorKind.NotWord);
                case 'x':
                    if (Index + 1 >= _pattern.Length || !Hex(_pattern[Index], out var hi) || !Hex(_pattern[Index + 1], out var lo))
                    {
                        Failed = true;
                        return null;
                    }

                    Index += 2;
                    return Lit((byte)((hi << 4) | lo));
                case 'u':
                    if (!TryHex4(out var code) || code > 0x7F)
                    {
                        Failed = true;
                        return null;
                    }

                    return Lit((byte)code);
                case >= '1' and <= '9':
                    Failed = true;
                    return null;
                default:
                    if (ch > 0x7F)
                    {
                        Failed = true;
                        return null;
                    }

                    return Lit((byte)ch);
            }
        }

        private bool TryHex4(out int code)
        {
            code = 0;
            if (Index + 3 >= _pattern.Length)
            {
                return false;
            }

            for (var n = 0; n < 4; n++)
            {
                if (!Hex(_pattern[Index + n], out var digit))
                {
                    return false;
                }

                code = (code << 4) | digit;
            }

            Index += 4;
            return true;
        }

        private Re Lit(byte value)
        {
            if (_fold && IsLetter(value))
            {
                ulong m0 = 0, m1 = 0, m2 = 0, m3 = 0;
                AddByte(ref m0, ref m1, ref m2, ref m3, value);
                AddByte(ref m0, ref m1, ref m2, ref m3, (byte)(value ^ 0x20));
                return new ReClass((m0, m1, m2, m3), false);
            }

            return new ReLit([value]);
        }

        private void AddByte(ref ulong m0, ref ulong m1, ref ulong m2, ref ulong m3, byte value)
        {
            Set(ref m0, ref m1, ref m2, ref m3, value);
            if (_fold && IsLetter(value))
            {
                Set(ref m0, ref m1, ref m2, ref m3, value ^ 0x20);
            }
        }

        private static ReClass Negate((ulong M0, ulong M1, ulong M2, ulong M3) mask) =>
            new((~mask.M0, ~mask.M1, ~mask.M2, ~mask.M3), !MaskHas(mask.M0, mask.M1, mask.M2, mask.M3, (byte)'\n'));
    }

    private static (ulong M0, ulong M1, ulong M2, ulong M3) DotMask(bool dotAll)
    {
        var mask = (~0UL, ~0UL, ~0UL, ~0UL);
        if (!dotAll)
        {
            Clear(ref mask, (byte)'\n');
        }

        return mask;
    }

    private static (ulong, ulong, ulong, ulong) RangeMask(byte lo, byte hi)
    {
        ulong m0 = 0, m1 = 0, m2 = 0, m3 = 0;
        for (var b = lo; b <= hi; b++)
        {
            Set(ref m0, ref m1, ref m2, ref m3, b);
        }

        return (m0, m1, m2, m3);
    }

    private static (ulong, ulong, ulong, ulong) WordMask()
    {
        var mask = RangeMask((byte)'0', (byte)'9');
        Or(ref mask, RangeMask((byte)'A', (byte)'Z'));
        Or(ref mask, RangeMask((byte)'a', (byte)'z'));
        Set(ref mask.Item1, ref mask.Item2, ref mask.Item3, ref mask.Item4, (byte)'_');
        return mask;
    }

    private static (ulong, ulong, ulong, ulong) SpaceMask()
    {
        ulong m0 = 0, m1 = 0, m2 = 0, m3 = 0;
        foreach (var b in new byte[] { (byte)' ', (byte)'\t', (byte)'\n', (byte)'\r', (byte)'\f', (byte)'\v' })
        {
            Set(ref m0, ref m1, ref m2, ref m3, b);
        }

        return (m0, m1, m2, m3);
    }

    private static void Or(ref (ulong, ulong, ulong, ulong) mask, (ulong, ulong, ulong, ulong) other)
    {
        mask.Item1 |= other.Item1;
        mask.Item2 |= other.Item2;
        mask.Item3 |= other.Item3;
        mask.Item4 |= other.Item4;
    }

    private static void Clear(ref (ulong, ulong, ulong, ulong) mask, byte value)
    {
        var bit = 1UL << (value & 63);
        switch (value)
        {
            case < 64:
                mask.Item1 &= ~bit;
                break;
            case < 128:
                mask.Item2 &= ~bit;
                break;
            case < 192:
                mask.Item3 &= ~bit;
                break;
            default:
                mask.Item4 &= ~bit;
                break;
        }
    }

    private static bool Hex(char ch, out int value)
    {
        if (ch is >= '0' and <= '9')
        {
            value = ch - '0';
            return true;
        }

        if (ch is >= 'a' and <= 'f')
        {
            value = ch - 'a' + 10;
            return true;
        }

        if (ch is >= 'A' and <= 'F')
        {
            value = ch - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }

    private abstract class Re;

    private sealed class ReEmpty : Re;

    private sealed class ReLit(byte[] bytes) : Re
    {
        public byte[] Bytes { get; } = bytes;
    }

    private sealed class ReClass((ulong M0, ulong M1, ulong M2, ulong M3) mask, bool includesNewline) : Re
    {
        public ulong M0 { get; } = mask.M0;

        public ulong M1 { get; } = mask.M1;

        public ulong M2 { get; } = mask.M2;

        public ulong M3 { get; } = mask.M3;

        public bool IncludesNewline { get; } = includesNewline;
    }

    private sealed class ReCat(Re[] parts) : Re
    {
        public Re[] Parts { get; } = parts;
    }

    private sealed class ReAlt(Re[] parts) : Re
    {
        public Re[] Parts { get; } = parts;
    }

    private sealed class ReRep(Re inner, int min, int max, bool lazy) : Re
    {
        public Re Inner { get; } = inner;

        public int Min { get; } = min;

        public int Max { get; } = max;

        public bool Lazy { get; } = lazy;
    }

    private enum AnchorKind : byte
    {
        Bol,
        Eol,
        Bos,
        Eos,
        Eow,
        Word,
        NotWord,
    }

    private sealed class ReAnchor(AnchorKind kind) : Re
    {
        public AnchorKind Kind { get; } = kind;
    }

    internal readonly record struct SeqAtom(
        byte[]? Literal,
        ulong M0,
        ulong M1,
        ulong M2,
        ulong M3,
        bool IncludesNewline,
        int Min,
        int Max,
        bool Lazy)
    {
        public static SeqAtom FromLiteral(byte[] bytes) => new(bytes, 0, 0, 0, 0, false, bytes.Length, bytes.Length, false);

        public static SeqAtom Class(ulong m0, ulong m1, ulong m2, ulong m3, bool newline, int min, int max, bool lazy) =>
            new(null, m0, m1, m2, m3, newline, min, max, lazy);
    }

    private sealed class PikeCompiler
    {
        private readonly List<PikeProgram.Inst> _insts = [];

        public bool Build(Re ast, out PikeProgram.Inst[] insts, out int anchored, out int unanchored, out byte[]? prefix)
        {
            insts = [];
            anchored = 0;
            unanchored = 0;
            prefix = PrefixOf(ast);
            var expr = Compile(ast);
            if (expr == null || _insts.Count > 512)
            {
                return false;
            }

            var match = Emit(PikeProgram.Op.Match);
            Patch(expr.Outs, match);
            var save = Emit(PikeProgram.Op.Save);
            SetX(save, expr.Start);
            var split = Emit(PikeProgram.Op.Split);
            var any = EmitClass(~0UL, ~0UL, ~0UL, ~0UL);
            var jump = Emit(PikeProgram.Op.Jump);
            SetX(split, save);
            SetY(split, any);
            SetX(any, jump);
            SetX(jump, split);
            anchored = save;
            unanchored = split;
            insts = _insts.ToArray();
            return true;
        }

        private static byte[]? PrefixOf(Re ast)
        {
            if (ast is ReCat cat && cat.Parts.Length > 0 && cat.Parts[0] is ReLit lit && lit.Bytes.Length >= 2)
            {
                return lit.Bytes;
            }

            return null;
        }

        private Frag? Compile(Re node)
        {
            switch (node)
            {
                case ReEmpty:
                    return JumpFrag();
                case ReLit lit:
                    Frag? acc = null;
                    foreach (var value in lit.Bytes)
                    {
                        ulong m0 = 0, m1 = 0, m2 = 0, m3 = 0;
                        Set(ref m0, ref m1, ref m2, ref m3, value);
                        var piece = ClassFrag(m0, m1, m2, m3);
                        acc = acc == null ? piece : Cat(acc, piece);
                    }

                    return acc ?? JumpFrag();
                case ReClass cls:
                    return ClassFrag(cls.M0, cls.M1, cls.M2, cls.M3);
                case ReAnchor anchor:
                    return AnchorFrag(anchor.Kind);
                case ReAlt alt:
                    if (alt.Parts.Length == 0)
                    {
                        return null;
                    }

                    var either = Compile(alt.Parts[0]);
                    if (either == null)
                    {
                        return null;
                    }

                    for (var i = 1; i < alt.Parts.Length; i++)
                    {
                        var right = Compile(alt.Parts[i]);
                        if (right == null)
                        {
                            return null;
                        }

                        var split = Emit(PikeProgram.Op.Split);
                        SetX(split, either.Start);
                        SetY(split, right.Start);
                        var outs = new List<Hole>(either.Outs);
                        outs.AddRange(right.Outs);
                        either = new Frag(split, outs);
                    }

                    return either;
                case ReCat cat:
                    Frag? joined = null;
                    foreach (var part in cat.Parts)
                    {
                        var next = Compile(part);
                        if (next == null)
                        {
                            return null;
                        }

                        joined = joined == null ? next : Cat(joined, next);
                    }

                    return joined ?? JumpFrag();
                case ReRep rep:
                    return CompileRep(rep);
                default:
                    return null;
            }
        }

        private Frag? CompileRep(ReRep rep)
        {
            Frag? acc = null;
            for (var i = 0; i < rep.Min; i++)
            {
                var piece = Compile(rep.Inner);
                if (piece == null)
                {
                    return null;
                }

                acc = acc == null ? piece : Cat(acc, piece);
            }

            if (rep.Max < 0)
            {
                var star = Star(rep.Inner, rep.Lazy);
                if (star == null)
                {
                    return null;
                }

                return acc == null ? star : Cat(acc, star);
            }

            for (var i = rep.Min; i < rep.Max; i++)
            {
                var optional = Optional(rep.Inner, rep.Lazy);
                if (optional == null)
                {
                    return null;
                }

                acc = acc == null ? optional : Cat(acc, optional);
            }

            return acc ?? JumpFrag();
        }

        private Frag? Star(Re inner, bool lazy)
        {
            var split = Emit(PikeProgram.Op.Split);
            var body = Compile(inner);
            if (body == null)
            {
                return null;
            }

            Patch(body.Outs, split);
            var outs = new List<Hole>();
            if (!lazy)
            {
                SetX(split, body.Start);
                outs.Add(new Hole(split, 1));
            }
            else
            {
                SetY(split, body.Start);
                outs.Add(new Hole(split, 0));
            }

            return new Frag(split, outs);
        }

        private Frag? Optional(Re inner, bool lazy)
        {
            var split = Emit(PikeProgram.Op.Split);
            var body = Compile(inner);
            if (body == null)
            {
                return null;
            }

            var outs = new List<Hole>(body.Outs);
            if (!lazy)
            {
                SetX(split, body.Start);
                outs.Add(new Hole(split, 1));
            }
            else
            {
                SetY(split, body.Start);
                outs.Add(new Hole(split, 0));
            }

            return new Frag(split, outs);
        }

        private Frag ClassFrag(ulong m0, ulong m1, ulong m2, ulong m3)
        {
            var id = EmitClass(m0, m1, m2, m3);
            return new Frag(id, [new Hole(id, 0)]);
        }

        private Frag AnchorFrag(AnchorKind kind)
        {
            var op = kind switch
            {
                AnchorKind.Bol => PikeProgram.Op.Bol,
                AnchorKind.Eol => PikeProgram.Op.Eol,
                AnchorKind.Bos => PikeProgram.Op.Bos,
                AnchorKind.Eos => PikeProgram.Op.Eos,
                AnchorKind.Eow => PikeProgram.Op.Eow,
                AnchorKind.Word => PikeProgram.Op.Word,
                _ => PikeProgram.Op.NotWord,
            };
            var id = Emit(op);
            return new Frag(id, [new Hole(id, 0)]);
        }

        private Frag JumpFrag()
        {
            var id = Emit(PikeProgram.Op.Jump);
            return new Frag(id, [new Hole(id, 0)]);
        }

        private Frag Cat(Frag left, Frag right)
        {
            Patch(left.Outs, right.Start);
            return new Frag(left.Start, right.Outs);
        }

        private int Emit(PikeProgram.Op op)
        {
            _insts.Add(new PikeProgram.Inst { Op = op, X = -1, Y = -1 });
            return _insts.Count - 1;
        }

        private int EmitClass(ulong m0, ulong m1, ulong m2, ulong m3)
        {
            _insts.Add(new PikeProgram.Inst { Op = PikeProgram.Op.Class, X = -1, Y = -1, M0 = m0, M1 = m1, M2 = m2, M3 = m3 });
            return _insts.Count - 1;
        }

        private void SetX(int index, int target)
        {
            var inst = _insts[index];
            inst.X = target;
            _insts[index] = inst;
        }

        private void SetY(int index, int target)
        {
            var inst = _insts[index];
            inst.Y = target;
            _insts[index] = inst;
        }

        private void Patch(List<Hole> holes, int target)
        {
            foreach (var hole in holes)
            {
                if (hole.Slot == 0)
                {
                    SetX(hole.Inst, target);
                }
                else
                {
                    SetY(hole.Inst, target);
                }
            }
        }

        private readonly struct Hole(int inst, int slot)
        {
            public int Inst { get; } = inst;

            public int Slot { get; } = slot;
        }

        private sealed class Frag(int start, List<Hole> outs)
        {
            public int Start { get; } = start;

            public List<Hole> Outs { get; } = outs;
        }
    }
}

internal abstract class ByteProgram
{
    /// <summary>
    /// True when running this program over whole bytes is expected to lose to narrowing
    /// to candidate lines first. The Pike VM touches every byte with a thread list, so a
    /// required ASCII literal is a far cheaper filter than the VM itself.
    /// </summary>
    public virtual bool PrefersLiteralPrefilter => false;

    /// <summary>
    /// True when decoding the file and confirming with the .NET engine beats running this
    /// program over the bytes. The VM is linear but about fifty times slower per byte than
    /// the engine on a decoded string, so it is only worth running when there is no decode
    /// to pay for and the VM can stop early.
    /// </summary>
    public virtual bool PrefersDecoded => false;

    public abstract void Collect(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits);

    protected static bool ContainsNewline(ReadOnlySpan<byte> text, int index, int length)
    {
        var end = index + length;
        if (end > text.Length)
        {
            end = text.Length;
        }

        for (var i = index; i < end; i++)
        {
            if (text[i] == (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }

    protected static void AddHit(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits, int index, int length)
    {
        if (length < 0 || index < 0 || index > text.Length || index + length > text.Length)
        {
            return;
        }

        if (!allowNewline && ContainsNewline(text, index, length))
        {
            return;
        }

        hits.Add(new ByteMatch(index, length));
        _ = stopAtFirst;
    }
}

internal readonly record struct ByteMatch(int Index, int Length);

internal sealed class LiteralProgram : ByteProgram
{
    private readonly byte[] _needle;
    private readonly SimdLiteralScanner _scanner;

    public LiteralProgram(byte[] needle, bool ignoreCase)
    {
        _needle = needle;
        _scanner = SimdLiteralScanner.Create([needle], ignoreCase);
    }

    public override void Collect(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        var from = 0;
        var length = _needle.Length;
        while (from <= text.Length - length)
        {
            var rel = _scanner.IndexOfAny(text[from..]);
            if (rel < 0)
            {
                return;
            }

            var index = from + rel;
            AddHit(text, allowNewline, stopAtFirst, hits, index, length);
            if (stopAtFirst && hits.Count > 0)
            {
                return;
            }

            from = index + Math.Max(1, length);
        }
    }
}

internal sealed class LiteralAltProgram : ByteProgram
{
    private readonly byte[][] _needles;
    private readonly SimdLiteralScanner _any;
    private readonly bool _ignoreCase;

    public LiteralAltProgram(byte[][] needles, bool ignoreCase)
    {
        _needles = needles;
        _ignoreCase = ignoreCase;
        _any = SimdLiteralScanner.Create(needles, ignoreCase);
    }

    public override void Collect(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        var from = 0;
        while (from < text.Length)
        {
            var rel = _any.IndexOfAny(text[from..]);
            if (rel < 0)
            {
                return;
            }

            var index = from + rel;
            var matched = false;
            var length = 0;
            foreach (var needle in _needles)
            {
                if (!ByteAutomaton.Equal(text[index..], needle, _ignoreCase))
                {
                    continue;
                }

                matched = true;
                length = needle.Length;
                break;
            }

            if (!matched)
            {
                from = index + 1;
                continue;
            }

            var before = hits.Count;
            AddHit(text, allowNewline, stopAtFirst, hits, index, length);
            if (stopAtFirst && hits.Count > before)
            {
                return;
            }

            from = index + Math.Max(1, length);
        }
    }
}

internal sealed class ClassRunProgram : ByteProgram
{
    private readonly ulong _m0;
    private readonly ulong _m1;
    private readonly ulong _m2;
    private readonly ulong _m3;
    private readonly int _min;
    private readonly int _max;
    private readonly bool _lazy;
    private readonly ClassFinder _finder;

    public ClassRunProgram(ulong m0, ulong m1, ulong m2, ulong m3, bool includesNewline, int min, int max, bool lazy)
    {
        _m0 = m0;
        _m1 = m1;
        _m2 = m2;
        _m3 = m3;
        IncludesNewline = includesNewline;
        _min = min;
        _max = max;
        _lazy = lazy;
        _finder = ClassFinder.Create(m0, m1, m2, m3);
    }

    public ClassRunProgram((ulong M0, ulong M1, ulong M2, ulong M3) mask, bool includesNewline, int min, int max, bool lazy)
        : this(mask.M0, mask.M1, mask.M2, mask.M3, includesNewline, min, max, lazy)
    {
    }

    public bool IncludesNewline { get; }

    public override void Collect(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        if (text.Length >= 64 && Avx512BW.IsSupported && _finder.RangeCount > 0)
        {
            CollectRanges512(text, allowNewline, stopAtFirst, hits);
            return;
        }

        if (text.Length >= 32 && Avx2.IsSupported && _finder.RangeCount > 0)
        {
            CollectRanges256(text, allowNewline, stopAtFirst, hits);
            return;
        }

        if (text.Length >= 16 && Sse2.IsSupported && _finder.RangeCount > 0)
        {
            CollectRanges128(text, allowNewline, stopAtFirst, hits);
            return;
        }

        CollectScalar(text, allowNewline, stopAtFirst, hits);
    }

    private void CollectScalar(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!In(text[i]))
            {
                i = _finder.Next(text, i);
                continue;
            }

            var j = i + 1;
            while (j < text.Length && In(text[j]))
            {
                j++;
            }

            if (EmitRun(text, allowNewline, stopAtFirst, hits, i, j))
            {
                return;
            }

            i = j;
        }
    }

    private unsafe void CollectRanges512(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        if (_min == 8)
        {
            CollectMin8Ranges512(text, allowNewline, stopAtFirst, hits);
            return;
        }

        if (!Avx512BW.IsSupported || !_finder.TryRanges(out var ranges, out var lo0, out var hi0, out var lo1, out var hi1, out var lo2, out var hi2))
        {
            CollectScalar(text, allowNewline, stopAtFirst, hits);
            return;
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        var i = 0;
        fixed (byte* basePtr = &origin)
        {
            var bias = Vector512.Create((byte)0x80);
            var last = text.Length - 64;
            while (i <= last)
            {
                var biased = Avx512F.Xor(Avx512F.LoadVector512(basePtr + i), bias);
                var mask = ClassFinder.Range512(biased, lo0, hi0);
                if (ranges > 1)
                {
                    mask = Avx512F.Or(mask, ClassFinder.Range512(biased, lo1, hi1));
                }

                if (ranges > 2)
                {
                    mask = Avx512F.Or(mask, ClassFinder.Range512(biased, lo2, hi2));
                }

                var bits = mask.ExtractMostSignificantBits();
                if (bits == 0)
                {
                    i += 64;
                    continue;
                }

                if (TrySkipShortRuns(text, allowNewline, stopAtFirst, hits, bits, i, 64, ref i, out var stop))
                {
                    if (stop)
                    {
                        return;
                    }

                    continue;
                }

                if (!WalkChunk(text, allowNewline, stopAtFirst, hits, bits, i, 64, ref i))
                {
                    return;
                }
            }
        }

        if (i < text.Length)
        {
            CollectScalarFrom(text, allowNewline, stopAtFirst, hits, i);
        }
    }

    private unsafe void CollectRanges256(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        if (_min == 8)
        {
            CollectMin8Ranges256(text, allowNewline, stopAtFirst, hits);
            return;
        }

        if (!Avx2.IsSupported || !_finder.TryRanges(out var ranges, out var lo0, out var hi0, out var lo1, out var hi1, out var lo2, out var hi2))
        {
            CollectScalar(text, allowNewline, stopAtFirst, hits);
            return;
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        var i = 0;
        fixed (byte* basePtr = &origin)
        {
            var bias = Vector256.Create((byte)0x80);
            var last = text.Length - 32;
            while (i <= last)
            {
                var biased = Avx2.Xor(Avx.LoadVector256(basePtr + i), bias);
                var mask = ClassFinder.Range256(biased, lo0, hi0);
                if (ranges > 1)
                {
                    mask = Avx2.Or(mask, ClassFinder.Range256(biased, lo1, hi1));
                }

                if (ranges > 2)
                {
                    mask = Avx2.Or(mask, ClassFinder.Range256(biased, lo2, hi2));
                }

                var bits = (ulong)mask.ExtractMostSignificantBits();
                if (bits == 0)
                {
                    i += 32;
                    continue;
                }

                if (TrySkipShortRuns(text, allowNewline, stopAtFirst, hits, bits, i, 32, ref i, out var stop))
                {
                    if (stop)
                    {
                        return;
                    }

                    continue;
                }

                if (!WalkChunk(text, allowNewline, stopAtFirst, hits, bits, i, 32, ref i))
                {
                    return;
                }
            }
        }

        if (i < text.Length)
        {
            CollectScalarFrom(text, allowNewline, stopAtFirst, hits, i);
        }
    }

    private unsafe void CollectRanges128(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        if (_min == 8)
        {
            CollectMin8Ranges128(text, allowNewline, stopAtFirst, hits);
            return;
        }

        if (!Sse2.IsSupported || !_finder.TryRanges(out var ranges, out var lo0, out var hi0, out var lo1, out var hi1, out var lo2, out var hi2))
        {
            CollectScalar(text, allowNewline, stopAtFirst, hits);
            return;
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        var i = 0;
        fixed (byte* basePtr = &origin)
        {
            var bias = Vector128.Create((byte)0x80);
            var last = text.Length - 16;
            while (i <= last)
            {
                var biased = Sse2.Xor(Sse2.LoadVector128(basePtr + i), bias);
                var mask = ClassFinder.Range128(biased, lo0, hi0);
                if (ranges > 1)
                {
                    mask = Sse2.Or(mask, ClassFinder.Range128(biased, lo1, hi1));
                }

                if (ranges > 2)
                {
                    mask = Sse2.Or(mask, ClassFinder.Range128(biased, lo2, hi2));
                }

                var bits = (ulong)mask.ExtractMostSignificantBits();
                if (bits == 0)
                {
                    i += 16;
                    continue;
                }

                if (TrySkipShortRuns(text, allowNewline, stopAtFirst, hits, bits, i, 16, ref i, out var stop))
                {
                    if (stop)
                    {
                        return;
                    }

                    continue;
                }

                if (!WalkChunk(text, allowNewline, stopAtFirst, hits, bits, i, 16, ref i))
                {
                    return;
                }
            }
        }

        if (i < text.Length)
        {
            CollectScalarFrom(text, allowNewline, stopAtFirst, hits, i);
        }
    }

    /// <summary>
    /// AVX-512 scan for a class repeated 8 times. The scanner itself never calls out,
    /// so a miss stays in one register-resident loop. A real run returns to the walker.
    /// </summary>
    private unsafe void CollectMin8Ranges512(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        if (!Avx512BW.IsSupported || !_finder.TryRanges(out var ranges, out var lo0, out var hi0, out var lo1, out var hi1, out var lo2, out var hi2))
        {
            CollectScalar(text, allowNewline, stopAtFirst, hits);
            return;
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        var i = 0;
        fixed (byte* basePtr = &origin)
        {
            var last = text.Length - 64;
            while (true)
            {
                i = ScanMin8Avx512(basePtr, text.Length, i, ranges, lo0, hi0, lo1, hi1, lo2, hi2);
                if ((uint)i > (uint)last)
                {
                    break;
                }

                var bits = ClassBits512(basePtr + i, ranges, lo0, hi0, lo1, hi1, lo2, hi2);
                if (!HasRun8(bits))
                {
                    var end = i + 64;
                    var need = 8 - EdgeSuffix(bits, 64);
                    if (need > text.Length - end || !RunContinues(text, end, need))
                    {
                        i = end;
                        continue;
                    }
                }

                if (!WalkDense(text, allowNewline, stopAtFirst, hits, bits, i, 64, ref i))
                {
                    return;
                }
            }
        }

        if (i < text.Length)
        {
            CollectScalarFrom(text, allowNewline, stopAtFirst, hits, i);
        }
    }

    private unsafe void CollectMin8Ranges256(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        if (!Avx2.IsSupported || !_finder.TryRanges(out var ranges, out var lo0, out var hi0, out var lo1, out var hi1, out var lo2, out var hi2))
        {
            CollectScalar(text, allowNewline, stopAtFirst, hits);
            return;
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        var i = 0;
        fixed (byte* basePtr = &origin)
        {
            var last = text.Length - 32;
            while (true)
            {
                i = ScanMin8Avx2(basePtr, text.Length, i, ranges, lo0, hi0, lo1, hi1, lo2, hi2);
                if ((uint)i > (uint)last)
                {
                    break;
                }

                var bits = ClassBits256(basePtr + i, ranges, lo0, hi0, lo1, hi1, lo2, hi2);
                if (!HasRun8(bits))
                {
                    var end = i + 32;
                    var need = 8 - EdgeSuffix(bits, 32);
                    if (need > text.Length - end || !RunContinues(text, end, need))
                    {
                        i = end;
                        continue;
                    }
                }

                if (!WalkDense(text, allowNewline, stopAtFirst, hits, bits, i, 32, ref i))
                {
                    return;
                }
            }
        }

        if (i < text.Length)
        {
            CollectScalarFrom(text, allowNewline, stopAtFirst, hits, i);
        }
    }

    private unsafe void CollectMin8Ranges128(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        if (!Sse2.IsSupported || !_finder.TryRanges(out var ranges, out var lo0, out var hi0, out var lo1, out var hi1, out var lo2, out var hi2))
        {
            CollectScalar(text, allowNewline, stopAtFirst, hits);
            return;
        }

        ref var origin = ref MemoryMarshal.GetReference(text);
        var i = 0;
        fixed (byte* basePtr = &origin)
        {
            var last = text.Length - 16;
            while (true)
            {
                i = ScanMin8Sse2(basePtr, text.Length, i, ranges, lo0, hi0, lo1, hi1, lo2, hi2);
                if ((uint)i > (uint)last)
                {
                    break;
                }

                var bits = ClassBits128(basePtr + i, ranges, lo0, hi0, lo1, hi1, lo2, hi2);
                if (!HasRun8(bits))
                {
                    var end = i + 16;
                    var need = 8 - EdgeSuffix(bits, 16);
                    if (need > text.Length - end || !RunContinues(text, end, need))
                    {
                        i = end;
                        continue;
                    }
                }

                if (!WalkDense(text, allowNewline, stopAtFirst, hits, bits, i, 16, ref i))
                {
                    return;
                }
            }
        }

        if (i < text.Length)
        {
            CollectScalarFrom(text, allowNewline, stopAtFirst, hits, i);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int ScanMin8Avx512(
        byte* basePtr,
        int length,
        int i,
        int ranges,
        byte lo0,
        byte hi0,
        byte lo1,
        byte hi1,
        byte lo2,
        byte hi2)
    {
        var last = length - 64;
        if (ranges == 1)
        {
            var bias = Vector512.Create((byte)0x80);
            var loV = Vector512.Create((byte)(lo0 ^ 0x80));
            var hiV = Vector512.Create((byte)(hi0 ^ 0x80));
            while (i <= last)
            {
                var biased = Avx512F.Xor(Avx512F.LoadVector512(basePtr + i), bias);
                var ge = Avx512F.Or(Avx512BW.CompareEqual(biased, loV), Avx512BW.CompareGreaterThan(biased.AsSByte(), loV.AsSByte()).AsByte());
                var le = Avx512F.Or(Avx512BW.CompareEqual(biased, hiV), Avx512BW.CompareGreaterThan(hiV.AsSByte(), biased.AsSByte()).AsByte());
                var bits = Avx512F.And(ge, le).ExtractMostSignificantBits();
                if (bits != 0 && MayHoldRun8(bits, 64, basePtr, i, length, lo0, hi0))
                {
                    return i;
                }

                i += 64;
            }

            return i;
        }

        while (i <= last)
        {
            var bits = ClassBits512(basePtr + i, ranges, lo0, hi0, lo1, hi1, lo2, hi2);
            if (bits != 0 && (BitOperations.PopCount(bits) >= 8 || (bits & 0x8000_0000_0000_0000UL) != 0))
            {
                return i;
            }

            i += 64;
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int ScanMin8Avx2(
        byte* basePtr,
        int length,
        int i,
        int ranges,
        byte lo0,
        byte hi0,
        byte lo1,
        byte hi1,
        byte lo2,
        byte hi2)
    {
        var last = length - 32;
        if (ranges == 1)
        {
            var bias = Vector256.Create((byte)0x80);
            var loV = Vector256.Create((byte)(lo0 ^ 0x80));
            var hiV = Vector256.Create((byte)(hi0 ^ 0x80));
            while (i <= last)
            {
                var biased = Avx2.Xor(Avx.LoadVector256(basePtr + i), bias);
                var ge = Avx2.Or(Avx2.CompareEqual(biased, loV), Avx2.CompareGreaterThan(biased.AsSByte(), loV.AsSByte()).AsByte());
                var le = Avx2.Or(Avx2.CompareEqual(biased, hiV), Avx2.CompareGreaterThan(hiV.AsSByte(), biased.AsSByte()).AsByte());
                var bits = (ulong)Avx2.And(ge, le).ExtractMostSignificantBits();
                if (bits != 0 && MayHoldRun8(bits, 32, basePtr, i, length, lo0, hi0))
                {
                    return i;
                }

                i += 32;
            }

            return i;
        }

        while (i <= last)
        {
            var bits = ClassBits256(basePtr + i, ranges, lo0, hi0, lo1, hi1, lo2, hi2);
            if (bits != 0 && (BitOperations.PopCount(bits) >= 8 || (bits & 0x8000_0000UL) != 0))
            {
                return i;
            }

            i += 32;
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe int ScanMin8Sse2(
        byte* basePtr,
        int length,
        int i,
        int ranges,
        byte lo0,
        byte hi0,
        byte lo1,
        byte hi1,
        byte lo2,
        byte hi2)
    {
        var last = length - 16;
        if (ranges == 1)
        {
            var bias = Vector128.Create((byte)0x80);
            var loV = Vector128.Create((byte)(lo0 ^ 0x80));
            var hiV = Vector128.Create((byte)(hi0 ^ 0x80));
            while (i <= last)
            {
                var biased = Sse2.Xor(Sse2.LoadVector128(basePtr + i), bias);
                var ge = Sse2.Or(Sse2.CompareEqual(biased, loV), Sse2.CompareGreaterThan(biased.AsSByte(), loV.AsSByte()).AsByte());
                var le = Sse2.Or(Sse2.CompareEqual(biased, hiV), Sse2.CompareGreaterThan(hiV.AsSByte(), biased.AsSByte()).AsByte());
                var bits = (ulong)Sse2.And(ge, le).ExtractMostSignificantBits();
                if (bits != 0 && MayHoldRun8(bits, 16, basePtr, i, length, lo0, hi0))
                {
                    return i;
                }

                i += 16;
            }

            return i;
        }

        while (i <= last)
        {
            var bits = ClassBits128(basePtr + i, ranges, lo0, hi0, lo1, hi1, lo2, hi2);
            if (bits != 0 && (BitOperations.PopCount(bits) >= 8 || (bits & 0x8000UL) != 0))
            {
                return i;
            }

            i += 16;
        }

        return i;
    }

    /// <summary>
    /// True when this window might hold eight members. Fewer than eight hits cannot,
    /// unless the run touches the last byte and the following byte stays in range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool MayHoldRun8(ulong bits, int width, byte* basePtr, int index, int length, byte lo, byte hi)
    {
        if (BitOperations.PopCount(bits) >= 8)
        {
            return true;
        }

        if ((bits & (1UL << (width - 1))) == 0)
        {
            return false;
        }

        var next = index + width;
        if ((uint)next >= (uint)length)
        {
            return false;
        }

        var n = basePtr[next];
        return (uint)(n - lo) <= (uint)(hi - lo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasRun8(ulong bits)
    {
        var run = bits;
        run &= run >> 1;
        run &= run >> 2;
        run &= run >> 4;
        return run != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EdgeSuffix(ulong bits, int width)
    {
        var aligned = width == 64 ? bits : bits << (64 - width);
        return BitOperations.LeadingZeroCount(~aligned);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong ClassBits512(byte* p, int ranges, byte lo0, byte hi0, byte lo1, byte hi1, byte lo2, byte hi2)
    {
        var bias = Vector512.Create((byte)0x80);
        var biased = Avx512F.Xor(Avx512F.LoadVector512(p), bias);
        var mask = ClassFinder.Range512(biased, lo0, hi0);
        if (ranges > 1)
        {
            mask = Avx512F.Or(mask, ClassFinder.Range512(biased, lo1, hi1));
        }

        if (ranges > 2)
        {
            mask = Avx512F.Or(mask, ClassFinder.Range512(biased, lo2, hi2));
        }

        return mask.ExtractMostSignificantBits();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong ClassBits256(byte* p, int ranges, byte lo0, byte hi0, byte lo1, byte hi1, byte lo2, byte hi2)
    {
        var bias = Vector256.Create((byte)0x80);
        var biased = Avx2.Xor(Avx.LoadVector256(p), bias);
        var mask = ClassFinder.Range256(biased, lo0, hi0);
        if (ranges > 1)
        {
            mask = Avx2.Or(mask, ClassFinder.Range256(biased, lo1, hi1));
        }

        if (ranges > 2)
        {
            mask = Avx2.Or(mask, ClassFinder.Range256(biased, lo2, hi2));
        }

        return (ulong)mask.ExtractMostSignificantBits();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ulong ClassBits128(byte* p, int ranges, byte lo0, byte hi0, byte lo1, byte hi1, byte lo2, byte hi2)
    {
        var bias = Vector128.Create((byte)0x80);
        var biased = Sse2.Xor(Sse2.LoadVector128(p), bias);
        var mask = ClassFinder.Range128(biased, lo0, hi0);
        if (ranges > 1)
        {
            mask = Sse2.Or(mask, ClassFinder.Range128(biased, lo1, hi1));
        }

        if (ranges > 2)
        {
            mask = Sse2.Or(mask, ClassFinder.Range128(biased, lo2, hi2));
        }

        return (ulong)mask.ExtractMostSignificantBits();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool RunContinues(ReadOnlySpan<byte> text, int end, int need)
    {
        for (var k = 0; k < need; k++)
        {
            if (!In(text[end + k]))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool WalkDense(
        ReadOnlySpan<byte> text,
        bool allowNewline,
        bool stopAtFirst,
        List<ByteMatch> hits,
        ulong bits,
        int chunk,
        int width,
        ref int i)
    {
        if (TrySkipShortRuns(text, allowNewline, stopAtFirst, hits, bits, chunk, width, ref i, out var stop))
        {
            return !stop;
        }

        return WalkChunk(text, allowNewline, stopAtFirst, hits, bits, chunk, width, ref i);
    }

    /// <summary>
    /// Skips a chunk whose longest in-window run is shorter than the minimum.
    /// A run that touches the right edge is finished in scalar so it can cross chunks.
    /// Returns false when the caller should walk every member bit.
    /// </summary>
    private bool TrySkipShortRuns(
        ReadOnlySpan<byte> text,
        bool allowNewline,
        bool stopAtFirst,
        List<ByteMatch> hits,
        ulong bits,
        int chunk,
        int width,
        ref int i,
        out bool stop)
    {
        stop = false;
        if (_min <= 1 || _min > 64)
        {
            return false;
        }

        var acc = bits;
        var shifted = bits;
        for (var s = 1; s < _min; s++)
        {
            shifted >>= 1;
            acc &= shifted;
        }

        if (acc != 0)
        {
            return false;
        }

        var suffix = 0;
        for (var b = width - 1; b >= 0; b--)
        {
            if ((bits & (1UL << b)) == 0)
            {
                break;
            }

            suffix++;
        }

        if (suffix == 0)
        {
            i = chunk + width;
            return true;
        }

        var start = chunk + width - suffix;
        var end = chunk + width;
        if (end < text.Length && In(text[end]))
        {
            while (end < text.Length && In(text[end]))
            {
                end++;
            }
        }

        stop = EmitRun(text, allowNewline, stopAtFirst, hits, start, end);
        i = end;
        return true;
    }

    /// <summary>
    /// Walks member bits inside one SIMD chunk. Returns false when the caller should stop.
    /// A run that ends inside the chunk stays in the bitset. A run that touches the
    /// chunk edge is extended in scalar and <paramref name="i"/> jumps to its end.
    /// </summary>
    private bool WalkChunk(
        ReadOnlySpan<byte> text,
        bool allowNewline,
        bool stopAtFirst,
        List<ByteMatch> hits,
        ulong bits,
        int chunk,
        int width,
        ref int i)
    {
        while (bits != 0)
        {
            var rel = BitOperations.TrailingZeroCount(bits);
            var runLen = ConsecutiveOnes(bits, rel, width);
            var start = chunk + rel;
            var end = start + runLen;
            if (rel + runLen == width && end < text.Length && In(text[end]))
            {
                while (end < text.Length && In(text[end]))
                {
                    end++;
                }

                if (EmitRun(text, allowNewline, stopAtFirst, hits, start, end))
                {
                    return false;
                }

                i = end;
                return true;
            }

            if (EmitRun(text, allowNewline, stopAtFirst, hits, start, end))
            {
                return false;
            }

            var consumed = rel + runLen;
            if (consumed >= width)
            {
                break;
            }

            bits &= ~((1UL << consumed) - 1);
        }

        i = chunk + width;
        return true;
    }

    private static int ConsecutiveOnes(ulong bits, int rel, int width)
    {
        var shifted = bits >> rel;
        var within = width - rel;
        if (within < 64)
        {
            shifted &= (1UL << within) - 1;
        }

        var ones = BitOperations.TrailingZeroCount(~shifted);
        return ones == 0 ? 1 : ones;
    }

    private void CollectScalarFrom(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits, int start)
    {
        var i = start;
        while (i < text.Length)
        {
            if (!In(text[i]))
            {
                i++;
                continue;
            }

            var j = i + 1;
            while (j < text.Length && In(text[j]))
            {
                j++;
            }

            if (EmitRun(text, allowNewline, stopAtFirst, hits, i, j))
            {
                return;
            }

            i = j;
        }
    }

    /// <summary>Emits the matches inside <c>[start, end)</c>. True when the search should stop.</summary>
    private bool EmitRun(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits, int start, int end)
    {
        var run = end - start;
        if (run < _min)
        {
            return false;
        }

        if (!_lazy && _max < 0)
        {
            AddHit(text, allowNewline, stopAtFirst, hits, start, run);
            return stopAtFirst && hits.Count > 0;
        }

        if (_lazy)
        {
            var step = Math.Max(1, _min);
            for (var p = start; p + _min <= end; p += step)
            {
                AddHit(text, allowNewline, stopAtFirst, hits, p, _min);
                if (stopAtFirst && hits.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        var cursor = start;
        while (cursor + _min <= end)
        {
            var take = Math.Min(_max, end - cursor);
            if (take < _min)
            {
                break;
            }

            AddHit(text, allowNewline, stopAtFirst, hits, cursor, take);
            if (stopAtFirst && hits.Count > 0)
            {
                return true;
            }

            cursor += take;
        }

        return false;
    }

    private bool In(byte value) => ByteAutomaton.MaskHas(_m0, _m1, _m2, _m3, value);
}

internal sealed class SequenceProgram : ByteProgram
{
    private readonly ByteAutomaton.SeqAtom[] _atoms;
    private readonly SimdLiteralScanner _prefix;
    private readonly bool _ignoreCase;

    public SequenceProgram(ByteAutomaton.SeqAtom[] atoms, bool ignoreCase)
    {
        _atoms = atoms;
        _ignoreCase = ignoreCase;
        _prefix = SimdLiteralScanner.Create([atoms[0].Literal!], ignoreCase);
    }

    public override void Collect(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        var prefix = _atoms[0].Literal!;
        var from = 0;
        while (from <= text.Length - prefix.Length)
        {
            var rel = _prefix.IndexOfAny(text[from..]);
            if (rel < 0)
            {
                return;
            }

            var index = from + rel;
            if (MatchAt(text, index, out var end))
            {
                var length = end - index;
                AddHit(text, allowNewline, stopAtFirst, hits, index, length);
                if (stopAtFirst && hits.Count > 0)
                {
                    return;
                }

                from = index + Math.Max(1, length);
            }
            else
            {
                from = index + 1;
            }
        }
    }

    private bool MatchAt(ReadOnlySpan<byte> text, int pos, out int end)
    {
        var i = pos;
        for (var a = 0; a < _atoms.Length; a++)
        {
            var atom = _atoms[a];
            if (atom.Literal != null)
            {
                if (!ByteAutomaton.Equal(text[i..], atom.Literal, _ignoreCase))
                {
                    end = 0;
                    return false;
                }

                i += atom.Literal.Length;
                continue;
            }

            var start = i;
            var limit = atom.Max < 0 ? text.Length : Math.Min(text.Length, start + atom.Max);
            while (i < limit && ByteAutomaton.MaskHas(atom.M0, atom.M1, atom.M2, atom.M3, text[i]))
            {
                i++;
            }

            var got = i - start;
            if (got < atom.Min)
            {
                end = 0;
                return false;
            }

            if (atom.Lazy)
            {
                i = start + atom.Min;
            }
        }

        end = i;
        return true;
    }
}

/// <summary>
/// Thompson NFA executed as a Pike VM. Threads are ordered by priority, so the
/// first alternative and greedy or lazy quantifiers follow the backtracking
/// engines without retrying earlier input.
/// </summary>
internal sealed class PikeProgram : ByteProgram
{
    internal enum Op : byte
    {
        Class,
        Split,
        Jump,
        Match,
        Save,
        Bol,
        Eol,
        Bos,
        Eos,
        Eow,
        Word,
        NotWord,
    }

    internal struct Inst
    {
        public Op Op;
        public int X;
        public int Y;
        public ulong M0;
        public ulong M1;
        public ulong M2;
        public ulong M3;
    }

    private readonly Inst[] _insts;
    private readonly int _anchored;
    private readonly int _unanchored;
    private readonly byte[]? _prefix;
    private readonly SimdLiteralScanner? _prefixScan;

    public PikeProgram(Inst[] insts, int anchored, int unanchored, byte[]? prefix, bool ignoreCase)
    {
        _insts = insts;
        _anchored = anchored;
        _unanchored = unanchored;
        if (prefix is { Length: >= 2 })
        {
            _prefix = prefix;
            _prefixScan = SimdLiteralScanner.Create([prefix], ignoreCase);
        }
    }

    public override bool PrefersLiteralPrefilter => true;

    /// <summary>
    /// Without a literal prefix the VM scans every byte, so the decoded engine wins on the
    /// files that reach it. With a prefix the SIMD scan already rejects a miss cheaply, so
    /// the VM stays.
    /// </summary>
    public override bool PrefersDecoded => _prefixScan == null;

    public override void Collect(ReadOnlySpan<byte> text, bool allowNewline, bool stopAtFirst, List<ByteMatch> hits)
    {
        var count = _insts.Length;
        if (count <= 256)
        {
            Span<int> seen = stackalloc int[count];
            Span<Thread> left = stackalloc Thread[count];
            Span<Thread> right = stackalloc Thread[count];
            seen.Clear();
            Collect(text, allowNewline, stopAtFirst, hits, seen, left, right);
            return;
        }

        var seenRent = System.Buffers.ArrayPool<int>.Shared.Rent(count);
        var leftRent = System.Buffers.ArrayPool<Thread>.Shared.Rent(count);
        var rightRent = System.Buffers.ArrayPool<Thread>.Shared.Rent(count);
        try
        {
            Array.Clear(seenRent, 0, count);
            Collect(text, allowNewline, stopAtFirst, hits, seenRent.AsSpan(0, count), leftRent.AsSpan(0, count), rightRent.AsSpan(0, count));
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(seenRent);
            System.Buffers.ArrayPool<Thread>.Shared.Return(leftRent);
            System.Buffers.ArrayPool<Thread>.Shared.Return(rightRent);
        }
    }

    private void Collect(
        ReadOnlySpan<byte> text,
        bool allowNewline,
        bool stopAtFirst,
        List<ByteMatch> hits,
        Span<int> seen,
        Span<Thread> left,
        Span<Thread> right)
    {
        if (_prefixScan != null && _prefix != null)
        {
            var from = 0;
            while (from <= text.Length - _prefix.Length)
            {
                var rel = _prefixScan.IndexOfAny(text[from..]);
                if (rel < 0)
                {
                    return;
                }

                var index = from + rel;
                if (Next(text, index, _anchored, seen, left, right, out var start, out var length) && start == index)
                {
                    var before = hits.Count;
                    AddHit(text, allowNewline, stopAtFirst, hits, start, length);
                    if (stopAtFirst && hits.Count > before)
                    {
                        return;
                    }

                    from = index + Math.Max(1, length);
                }
                else
                {
                    from = index + 1;
                }
            }

            return;
        }

        var pos = 0;
        while (pos <= text.Length)
        {
            if (!Next(text, pos, _unanchored, seen, left, right, out var start, out var length))
            {
                return;
            }

            var before = hits.Count;
            AddHit(text, allowNewline, stopAtFirst, hits, start, length);
            if (stopAtFirst && hits.Count > before)
            {
                return;
            }

            var next = length == 0 ? start + 1 : start + length;
            pos = next <= pos ? pos + 1 : next;
        }
    }

    private bool Next(
        ReadOnlySpan<byte> text,
        int from,
        int startPc,
        Span<int> seen,
        Span<Thread> current,
        Span<Thread> next,
        out int start,
        out int length)
    {
        start = 0;
        length = 0;
        seen.Clear();
        var gen = 1;
        var currentCount = 0;
        Add(text, from, ref gen, seen, current, ref currentCount, startPc, -1);
        var bestStart = -1;
        var bestEnd = -1;
        for (var pos = from; ;)
        {
            gen = Bump(seen, gen);
            var nextCount = 0;
            for (var i = 0; i < currentCount; i++)
            {
                var thread = current[i];
                ref var inst = ref _insts[thread.Pc];
                if (inst.Op == Op.Match)
                {
                    bestStart = thread.Start;
                    bestEnd = pos;
                    break;
                }

                if (pos < text.Length && inst.Op == Op.Class && ByteAutomaton.MaskHas(inst.M0, inst.M1, inst.M2, inst.M3, text[pos]))
                {
                    Add(text, pos + 1, ref gen, seen, next, ref nextCount, inst.X, thread.Start);
                }
            }

            if (bestStart >= 0 && nextCount == 0)
            {
                start = bestStart;
                length = bestEnd - bestStart;
                return start >= 0 && length >= 0;
            }

            if (pos >= text.Length)
            {
                break;
            }

            var swap = current;
            current = next;
            next = swap;
            currentCount = nextCount;
            pos++;
        }

        if (bestStart < 0)
        {
            return false;
        }

        start = bestStart;
        length = bestEnd - bestStart;
        return length >= 0;
    }

    private void Add(ReadOnlySpan<byte> text, int pos, ref int gen, Span<int> seen, Span<Thread> list, ref int count, int pc, int start)
    {
        while (true)
        {
            if ((uint)pc >= (uint)_insts.Length || seen[pc] == gen)
            {
                return;
            }

            seen[pc] = gen;
            ref var inst = ref _insts[pc];
            switch (inst.Op)
            {
                case Op.Split:
                    Add(text, pos, ref gen, seen, list, ref count, inst.X, start);
                    pc = inst.Y;
                    continue;
                case Op.Jump:
                    pc = inst.X;
                    continue;
                case Op.Save:
                    start = pos;
                    pc = inst.X;
                    continue;
                case Op.Bol:
                    if (pos == 0 || (pos > 0 && pos <= text.Length && text[pos - 1] == (byte)'\n'))
                    {
                        pc = inst.X;
                        continue;
                    }

                    return;
                case Op.Eol:
                    if (pos == text.Length || (pos < text.Length && text[pos] == (byte)'\n'))
                    {
                        pc = inst.X;
                        continue;
                    }

                    return;
                case Op.Bos:
                    if (pos == 0)
                    {
                        pc = inst.X;
                        continue;
                    }

                    return;
                case Op.Eos:
                    if (pos == text.Length)
                    {
                        pc = inst.X;
                        continue;
                    }

                    return;
                case Op.Eow:
                    if (pos == text.Length || (pos == text.Length - 1 && text[pos] == (byte)'\n'))
                    {
                        pc = inst.X;
                        continue;
                    }

                    return;
                case Op.Word:
                    if (IsBoundary(text, pos))
                    {
                        pc = inst.X;
                        continue;
                    }

                    return;
                case Op.NotWord:
                    if (!IsBoundary(text, pos))
                    {
                        pc = inst.X;
                        continue;
                    }

                    return;
                default:
                    if (count < list.Length)
                    {
                        list[count++] = new Thread(pc, start);
                    }

                    return;
            }
        }
    }

    private static int Bump(Span<int> seen, int gen)
    {
        if (gen == int.MaxValue)
        {
            seen.Clear();
            return 1;
        }

        return gen + 1;
    }

    private static bool IsBoundary(ReadOnlySpan<byte> text, int index)
    {
        var left = index > 0 && index <= text.Length && IsWord(text[index - 1]);
        var right = index < text.Length && IsWord(text[index]);
        return left != right;
    }

    private static bool IsWord(byte value) =>
        value == (byte)'_'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'a' and <= (byte)'z';

    private readonly record struct Thread(int Pc, int Start);
}
