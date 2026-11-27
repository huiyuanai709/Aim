namespace Ngrep.Ignore;

/// <summary>
/// Gitignore glob compiled to the same language as the regex fallback.
/// <see cref="TryMatch"/> reports whether the fast matcher finished; a step limit
/// asks the caller to use the regex interpreter instead.
/// </summary>
internal sealed class Glob
{
    private const int StepLimit = 10000;

    private readonly Atom[] _atoms;
    private readonly Shape _shape;
    private readonly string _literal;
    private readonly StringComparison _comparison;
    private readonly bool _ignoreCase;

    private Glob(Atom[] atoms, Shape shape, string literal, bool ignoreCase)
    {
        _atoms = atoms;
        _shape = shape;
        _literal = literal;
        _ignoreCase = ignoreCase;
        _comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public static Glob? TryCreate(string pattern, bool anchored) => TryCreate(pattern, anchored, ignoreCase: false);

    public static Glob? TryCreate(string pattern, bool anchored, bool ignoreCase)
    {
        if (!TryCompile(pattern, anchored, out var atoms))
        {
            return null;
        }

        var (shape, literal) = Classify(atoms, anchored);
        return new Glob(atoms, shape, literal, ignoreCase);
    }

    public bool TryMatch(ReadOnlySpan<char> text, out bool matched)
    {
        switch (_shape)
        {
            case Shape.All:
                matched = true;
                return true;
            case Shape.ExactPath:
                matched = text.Equals(_literal, _comparison);
                return true;
            case Shape.ExactSegment:
                matched = LastSegmentEquals(text, _literal, _comparison);
                return true;
            case Shape.SuffixSegment:
                matched = LastSegmentEndsWith(text, _literal, _comparison);
                return true;
            case Shape.PrefixSegment:
                matched = LastSegmentStartsWith(text, _literal, _comparison);
                return true;
            case Shape.ContainsSegment:
                matched = LastSegmentContains(text, _literal, _comparison);
                return true;
        }

        var steps = 0;
        matched = Match(text, 0, 0, ref steps);
        return steps <= StepLimit;
    }

    private static bool LastSegmentEquals(ReadOnlySpan<char> text, string literal, StringComparison comparison)
    {
        var slash = text.LastIndexOf('/');
        var segment = slash < 0 ? text : text[(slash + 1)..];
        return segment.Equals(literal, comparison);
    }

    private static bool LastSegmentEndsWith(ReadOnlySpan<char> text, string literal, StringComparison comparison)
    {
        var slash = text.LastIndexOf('/');
        var segment = slash < 0 ? text : text[(slash + 1)..];
        return segment.EndsWith(literal, comparison);
    }

    private static bool LastSegmentStartsWith(ReadOnlySpan<char> text, string literal, StringComparison comparison)
    {
        var slash = text.LastIndexOf('/');
        var segment = slash < 0 ? text : text[(slash + 1)..];
        return segment.StartsWith(literal, comparison);
    }

    private static bool LastSegmentContains(ReadOnlySpan<char> text, string literal, StringComparison comparison)
    {
        var slash = text.LastIndexOf('/');
        var segment = slash < 0 ? text : text[(slash + 1)..];
        return segment.IndexOf(literal, comparison) >= 0;
    }

    private bool Match(ReadOnlySpan<char> text, int ti, int ai, ref int steps)
    {
        if (++steps > StepLimit)
        {
            return false;
        }

        while (ai < _atoms.Length)
        {
            var atom = _atoms[ai];
            switch (atom.Kind)
            {
                case AtomKind.Literal:
                    if (!text[ti..].StartsWith(atom.Text.AsSpan(), _comparison))
                    {
                        return false;
                    }

                    ti += atom.Text.Length;
                    ai++;
                    break;
                case AtomKind.Any:
                    if ((uint)ti >= (uint)text.Length || text[ti] == '/')
                    {
                        return false;
                    }

                    ti++;
                    ai++;
                    break;
                case AtomKind.Class:
                    if ((uint)ti >= (uint)text.Length || !atom.Class!.Contains(text[ti], _ignoreCase))
                    {
                        return false;
                    }

                    ti++;
                    ai++;
                    break;
                case AtomKind.Rest:
                    return true;
                case AtomKind.Star:
                    var max = ti;
                    while (max < text.Length && text[max] != '/')
                    {
                        max++;
                    }

                    for (var k = max; k >= ti; k--)
                    {
                        if (Match(text, k, ai + 1, ref steps))
                        {
                            return true;
                        }

                        if (steps > StepLimit)
                        {
                            return false;
                        }
                    }

                    return false;
                case AtomKind.AnyDirs:
                    if (Match(text, ti, ai + 1, ref steps))
                    {
                        return true;
                    }

                    for (var i = ti; i < text.Length; i++)
                    {
                        if (text[i] == '/' && Match(text, i + 1, ai + 1, ref steps))
                        {
                            return true;
                        }

                        if (steps > StepLimit)
                        {
                            return false;
                        }
                    }

                    return false;
                default:
                    return false;
            }
        }

        return ti == text.Length;
    }

    private static (Shape Shape, string Literal) Classify(Atom[] atoms, bool anchored)
    {
        if (atoms.Length == 1 && atoms[0].Kind == AtomKind.Rest)
        {
            return (Shape.All, "");
        }

        if (atoms.Length == 2 && atoms[0].Kind == AtomKind.AnyDirs && atoms[1].Kind == AtomKind.Rest)
        {
            return (Shape.All, "");
        }

        if (!anchored && atoms.Length == 2 && atoms[0].Kind == AtomKind.AnyDirs && atoms[1].Kind == AtomKind.Literal)
        {
            return (Shape.ExactSegment, atoms[1].Text);
        }

        if (anchored && atoms.Length == 1 && atoms[0].Kind == AtomKind.Literal)
        {
            return (Shape.ExactPath, atoms[0].Text);
        }

        if (!anchored && atoms.Length == 3 && atoms[0].Kind == AtomKind.AnyDirs && atoms[1].Kind == AtomKind.Star && atoms[2].Kind == AtomKind.Literal)
        {
            return (Shape.SuffixSegment, atoms[2].Text);
        }

        if (!anchored && atoms.Length == 3 && atoms[0].Kind == AtomKind.AnyDirs && atoms[1].Kind == AtomKind.Literal && atoms[2].Kind == AtomKind.Star)
        {
            return (Shape.PrefixSegment, atoms[1].Text);
        }

        if (!anchored
            && atoms.Length == 4
            && atoms[0].Kind == AtomKind.AnyDirs
            && atoms[1].Kind == AtomKind.Star
            && atoms[2].Kind == AtomKind.Literal
            && atoms[3].Kind == AtomKind.Star)
        {
            return (Shape.ContainsSegment, atoms[2].Text);
        }

        if (!anchored && atoms.Length == 2 && atoms[0].Kind == AtomKind.AnyDirs && atoms[1].Kind == AtomKind.Star)
        {
            return (Shape.All, "");
        }

        return (Shape.General, "");
    }

    private static bool TryCompile(string pattern, bool anchored, out Atom[] atoms)
    {
        var list = new List<Atom>(8);
        if (!anchored)
        {
            list.Add(Atom.AnyDirs());
        }

        var literal = new System.Text.StringBuilder();
        void Flush()
        {
            if (literal.Length == 0)
            {
                return;
            }

            list.Add(Atom.Literal(literal.ToString()));
            literal.Clear();
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                literal.Append(pattern[i + 1]);
                i++;
                continue;
            }

            if (c == '*')
            {
                var starCount = 1;
                while (i + starCount < pattern.Length && pattern[i + starCount] == '*')
                {
                    starCount++;
                }

                if (starCount >= 2)
                {
                    var atStart = i == 0;
                    var afterSlash = i > 0 && pattern[i - 1] == '/';
                    var atEnd = i + starCount == pattern.Length;
                    var beforeSlash = i + starCount < pattern.Length && pattern[i + starCount] == '/';
                    if ((atStart || afterSlash) && (atEnd || beforeSlash))
                    {
                        Flush();
                        if (beforeSlash)
                        {
                            list.Add(Atom.AnyDirs());
                            i += starCount;
                            continue;
                        }

                        list.Add(Atom.Rest());
                        i += starCount - 1;
                        continue;
                    }
                }

                Flush();
                list.Add(Atom.Star());
                i += starCount - 1;
                continue;
            }

            if (c == '?')
            {
                Flush();
                list.Add(Atom.Any());
                continue;
            }

            if (c == '[')
            {
                var end = pattern.IndexOf(']', i + 1);
                if (end < 0)
                {
                    literal.Append('[');
                    continue;
                }

                var inner = pattern.AsSpan(i + 1, end - i - 1);
                var negated = false;
                if (inner.Length > 0 && inner[0] == '!')
                {
                    negated = true;
                    inner = inner[1..];
                }

                if (!CharClass.TryParse(inner, negated, out var cls))
                {
                    atoms = [];
                    return false;
                }

                Flush();
                list.Add(Atom.OfClass(cls));
                i = end;
                continue;
            }

            literal.Append(c);
        }

        Flush();
        atoms = list.ToArray();
        return true;
    }

    private enum Shape : byte
    {
        General,
        All,
        ExactPath,
        ExactSegment,
        SuffixSegment,
        PrefixSegment,
        ContainsSegment,
    }

    private enum AtomKind : byte
    {
        Literal,
        Star,
        Any,
        AnyDirs,
        Rest,
        Class,
    }

    private readonly struct Atom
    {
        private Atom(AtomKind kind, string text, CharClass? cls)
        {
            Kind = kind;
            Text = text;
            Class = cls;
        }

        public AtomKind Kind { get; }

        public string Text { get; }

        public CharClass? Class { get; }

        public static Atom Literal(string text) => new(AtomKind.Literal, text, null);

        public static Atom Star() => new(AtomKind.Star, "", null);

        public static Atom Any() => new(AtomKind.Any, "", null);

        public static Atom AnyDirs() => new(AtomKind.AnyDirs, "", null);

        public static Atom Rest() => new(AtomKind.Rest, "", null);

        public static Atom OfClass(CharClass cls) => new(AtomKind.Class, "", cls);
    }

    private sealed class CharClass
    {
        private readonly bool _negated;
        private readonly (char From, char To)[] _ranges;

        private CharClass(bool negated, (char From, char To)[] ranges)
        {
            _negated = negated;
            _ranges = ranges;
        }

        public static bool TryParse(ReadOnlySpan<char> body, bool negated, out CharClass cls)
        {
            cls = null!;
            if (body.Length == 0)
            {
                return false;
            }

            var ranges = new List<(char, char)>(body.Length);
            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c is '\\' or ':' or '&')
                {
                    return false;
                }

                if (i + 2 < body.Length && body[i + 1] == '-')
                {
                    var to = body[i + 2];
                    if (to is '\\' or ':' or '&')
                    {
                        return false;
                    }

                    ranges.Add((c, to));
                    i += 2;
                    continue;
                }

                ranges.Add((c, c));
            }

            cls = new CharClass(negated, ranges.ToArray());
            return true;
        }

        public bool Contains(char value, bool ignoreCase)
        {
            var hit = InRange(value);
            if (!hit && ignoreCase)
            {
                var lower = char.ToLowerInvariant(value);
                var upper = char.ToUpperInvariant(value);
                hit = (lower != value && InRange(lower)) || (upper != value && InRange(upper));
            }

            return _negated ? !hit : hit;
        }

        private bool InRange(char value)
        {
            foreach (var (from, to) in _ranges)
            {
                if (value >= from && value <= to)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
