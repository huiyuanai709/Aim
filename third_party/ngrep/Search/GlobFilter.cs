using Ngrep.Ignore;

namespace Ngrep.Search;

/// <summary>
/// Ripgrep-style path globs. A leading <c>!</c> excludes. When any include glob is present,
/// a path starts excluded and must be selected by a later include. The last matching glob wins.
/// Globs use the same syntax as .gitignore (<c>*</c>, <c>?</c>, <c>**</c>, character classes).
/// They do not re-include files the gitignore walker already skipped.
/// <see cref="GlobSpec.IgnoreCase"/> is <c>--iglob</c>. <c>-g</c> stays case-sensitive.
/// Mixed flags keep command-line order, and the last matching glob still wins.
/// </summary>
internal readonly record struct GlobSpec(string Pattern, bool IgnoreCase);

internal sealed class GlobFilter
{
    private readonly Entry[] _entries;
    private readonly bool _anyInclude;

    private GlobFilter(Entry[] entries, bool anyInclude)
    {
        _entries = entries;
        _anyInclude = anyInclude;
    }

    public static GlobFilter Empty { get; } = new([], false);

    public bool IsEmpty => _entries.Length == 0;

    public static GlobFilter Parse(IReadOnlyList<string> globs)
    {
        if (globs.Count == 0)
        {
            return Empty;
        }

        var specs = new GlobSpec[globs.Count];
        for (var i = 0; i < globs.Count; i++)
        {
            specs[i] = new GlobSpec(globs[i], false);
        }

        return Parse(specs);
    }

    public static GlobFilter Parse(IReadOnlyList<GlobSpec> globs)
    {
        if (globs.Count == 0)
        {
            return Empty;
        }

        var entries = new Entry[globs.Count];
        var anyInclude = false;
        for (var i = 0; i < globs.Count; i++)
        {
            var raw = globs[i].Pattern;
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException("Glob is empty.");
            }

            var exclude = raw[0] == '!';
            var body = exclude ? raw[1..] : raw;
            if (body.Length == 0)
            {
                throw new ArgumentException($"Glob '{raw}' has no pattern.");
            }

            var rule = IgnoreRule.TryParse(body, globs[i].IgnoreCase)
                ?? throw new ArgumentException($"Invalid glob '{raw}'.");
            if (!exclude)
            {
                anyInclude = true;
            }

            entries[i] = new Entry(rule, exclude);
        }

        return new GlobFilter(entries, anyInclude);
    }

    public bool Accepts(string relativePath)
    {
        if (_entries.Length == 0)
        {
            return true;
        }

        var included = !_anyInclude;
        foreach (var entry in _entries)
        {
            if (!entry.Rule.Matches(relativePath, false))
            {
                continue;
            }

            included = !entry.Exclude;
        }

        return included;
    }

    private readonly record struct Entry(IgnoreRule Rule, bool Exclude);
}
