using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Ngrep.Ignore;

/// <summary>
/// Gitignore matcher scoped the way git scopes nested files.
/// Patterns are evaluated against the path relative to the .gitignore that defined them.
/// The last matching pattern wins. If any parent directory is ignored, the child is ignored.
/// </summary>
internal sealed class IgnoreRuleSet
{
    private readonly IgnoreRule[] _rules;
    private readonly bool _hasNegation;

    private IgnoreRuleSet(IgnoreRule[] rules, bool hasNegation)
    {
        _rules = rules;
        _hasNegation = hasNegation;
    }

    public int Count => _rules.Length;

    public static IgnoreRuleSet Parse(string text)
    {
        var rules = new List<IgnoreRule>();
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..];
        }

        var hasNegation = false;
        foreach (var raw in normalized.Split('\n'))
        {
            if (IgnoreRule.TryParse(raw) is not { } rule)
            {
                continue;
            }

            if (rule.Negation)
            {
                hasNegation = true;
            }

            rules.Add(rule);
        }

        return new IgnoreRuleSet(rules.ToArray(), hasNegation);
    }

    public bool TryDecide(ReadOnlySpan<char> localPath, bool isDirectory, out bool ignored)
    {
        if (!_hasNegation)
        {
            foreach (var rule in _rules)
            {
                if (rule.DirectoryOnly && !isDirectory)
                {
                    continue;
                }

                if (!rule.Matches(localPath, isDirectory))
                {
                    continue;
                }

                ignored = true;
                return true;
            }

            ignored = false;
            return false;
        }

        var any = false;
        var state = false;
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }

            if (!rule.Matches(localPath, isDirectory))
            {
                continue;
            }

            any = true;
            state = !rule.Negation;
        }

        ignored = state;
        return any;
    }
}

internal sealed class IgnoreRule
{
    private readonly Glob? _glob;
    private readonly string _body;
    private readonly bool _anchored;
    private readonly bool _ignoreCase;
    private Regex? _regex;

    private IgnoreRule(Glob? glob, Regex? regex, string body, bool anchored, bool negation, bool directoryOnly, bool ignoreCase)
    {
        _glob = glob;
        _regex = regex;
        _body = body;
        _anchored = anchored;
        _ignoreCase = ignoreCase;
        Negation = negation;
        DirectoryOnly = directoryOnly;
    }

    public bool Negation { get; }

    public bool DirectoryOnly { get; }

    public static IgnoreRule? TryParse(string rawLine) => TryParse(rawLine, ignoreCase: false);

    /// <param name="ignoreCase">Ordinal, culture-invariant. Used by <c>--iglob</c>. Ignore files stay case-sensitive.</param>
    public static IgnoreRule? TryParse(string rawLine, bool ignoreCase)
    {
        var line = TrimTrailingWhitespace(rawLine);
        if (line.Length == 0 || line[0] == '#')
        {
            return null;
        }

        if (line.StartsWith("\\#", StringComparison.Ordinal))
        {
            line = line[1..];
        }

        var negation = false;
        if (line.StartsWith("\\!", StringComparison.Ordinal))
        {
            line = line[1..];
        }
        else if (line.StartsWith('!'))
        {
            negation = true;
            line = line[1..];
        }

        if (line.Length == 0)
        {
            return null;
        }

        var directoryOnly = line.EndsWith('/');
        if (directoryOnly)
        {
            line = line[..^1];
        }

        if (line.Length == 0)
        {
            return null;
        }

        var anchored = line.StartsWith('/') || line.Contains('/');
        if (line.StartsWith('/'))
        {
            line = line[1..];
        }

        if (line.Length == 0)
        {
            return null;
        }

        var glob = Glob.TryCreate(line, anchored, ignoreCase);
        Regex? regex = null;
        if (glob == null)
        {
            try
            {
                regex = CompileRegex(line, anchored, ignoreCase);
            }
            catch (RegexParseException)
            {
                return null;
            }
        }

        return new IgnoreRule(glob, regex, line, anchored, negation, directoryOnly, ignoreCase);
    }

    public bool Matches(string localPath, bool isDirectory) => Matches(localPath.AsSpan(), isDirectory);

    public bool Matches(ReadOnlySpan<char> localPath, bool isDirectory)
    {
        if (DirectoryOnly && !isDirectory)
        {
            return false;
        }

        if (_glob != null && _glob.TryMatch(localPath, out var matched))
        {
            return matched;
        }

        return PatternRegex().IsMatch(localPath);
    }

    /// <summary>Regex interpreter for the same pattern. Tests compare it with <see cref="Matches"/>.</summary>
    internal bool MatchesRegex(string localPath, bool isDirectory)
    {
        if (DirectoryOnly && !isDirectory)
        {
            return false;
        }

        return PatternRegex().IsMatch(localPath);
    }

    private Regex PatternRegex() => _regex ??= CompileRegex(_body, _anchored, _ignoreCase);

    private static Regex CompileRegex(string pattern, bool anchored, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant;
        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(Compile(pattern, anchored), options, TimeSpan.FromSeconds(2));
    }

    private static string Compile(string pattern, bool anchored)
    {
        var sb = new StringBuilder();
        sb.Append('^');
        if (!anchored)
        {
            sb.Append("(?:.*/)?");
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(Regex.Escape(pattern[i + 1].ToString()));
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
                        if (beforeSlash)
                        {
                            sb.Append("(?:.*/)?");
                            i += starCount;
                            continue;
                        }

                        sb.Append(".*");
                        i += starCount - 1;
                        continue;
                    }
                }

                sb.Append("[^/]*");
                i += starCount - 1;
                continue;
            }

            if (c == '?')
            {
                sb.Append("[^/]");
                continue;
            }

            if (c == '[')
            {
                var end = pattern.IndexOf(']', i + 1);
                if (end < 0)
                {
                    sb.Append("\\[");
                    continue;
                }

                var cls = pattern.Substring(i, end - i + 1);
                if (cls.Length >= 2 && cls[1] == '!')
                {
                    cls = "[^" + cls[2..];
                }

                sb.Append(cls);
                i = end;
                continue;
            }

            sb.Append(Regex.Escape(c.ToString()));
        }

        sb.Append('$');
        return sb.ToString();
    }

    private static string TrimTrailingWhitespace(string raw)
    {
        var end = raw.Length;
        while (end > 0 && raw[end - 1] is ' ' or '\t')
        {
            var slashes = 0;
            var i = end - 2;
            while (i >= 0 && raw[i] == '\\')
            {
                slashes++;
                i--;
            }

            if (slashes % 2 == 1)
            {
                break;
            }

            end--;
        }

        return raw[..end];
    }
}

/// <summary>
/// One ignore file family. Higher values win over lower ones even when the lower file is more nested.
/// </summary>
internal enum IgnoreSource : byte
{
    /// <summary>
    /// A file passed with <c>--ignore-file</c>. Lowest precedence.
    /// Earlier files lose to later ones. Patterns are relative to the process working directory.
    /// </summary>
    IgnoreFile = 0,

    /// <summary><c>core.excludesFile</c>, or the default global excludes file.</summary>
    Global = 1,

    /// <summary><c>$GIT_DIR/info/exclude</c>.</summary>
    Exclude = 2,

    /// <summary>A <c>.gitignore</c> file.</summary>
    Git = 3,

    /// <summary>A <c>.ignore</c> file.</summary>
    DotIgnore = 4,

    /// <summary>A <c>.rgignore</c> file. Highest precedence.</summary>
    RgIgnore = 5,
}

/// <summary>
/// Which ignore sources a walk reads.
/// <see cref="All"/> is the default. <see cref="CustomOnly"/> is <c>--no-ignore-vcs</c>
/// (<c>.ignore</c> and <c>.rgignore</c> stay on). <see cref="None"/> is <c>--no-ignore</c>.
/// <see cref="ExtraFiles"/> are <c>--ignore-file</c> paths. They stay on for every policy,
/// including <see cref="None"/>, because the user named them.
/// </summary>
internal readonly record struct IgnorePolicy(bool Vcs, bool Custom, IReadOnlyList<string>? ExtraFiles = null)
{
    public static IgnorePolicy All { get; } = new(true, true);

    public static IgnorePolicy None { get; } = new(false, false);

    public static IgnorePolicy CustomOnly { get; } = new(false, true);

    public IReadOnlyList<string> Files => ExtraFiles ?? [];

    public bool HasExtra => ExtraFiles is { Count: > 0 };

    public IgnorePolicy WithExtra(IReadOnlyList<string> files) =>
        files.Count == 0 ? this : this with { ExtraFiles = files };
}

/// <summary>
/// Ignore rules in ripgrep's source order.
/// Within one source, parent files are added before child files and the last match wins.
/// The first source that matches at all decides, from <see cref="IgnoreSource.RgIgnore"/>
/// down to <see cref="IgnoreSource.IgnoreFile"/>. A whitelist in <c>.gitignore</c> does not
/// override a match in <c>.ignore</c>. <c>--ignore-file</c> loses to every other source.
/// If a parent directory is ignored, the child is ignored.
/// </summary>
internal sealed class IgnoreStack
{
    private readonly record struct Frame(IgnoreSource Source, string BaseRelative, IgnoreRuleSet Rules);

    private readonly Frame[] _frames;
    private readonly string? _scopeRoot;
    private readonly string? _cwd;
    private readonly bool _hasIgnoreFile;

    private IgnoreStack(Frame[] frames, string? scopeRoot, string? cwd)
    {
        _frames = frames;
        _scopeRoot = scopeRoot;
        _cwd = cwd;
        foreach (var frame in frames)
        {
            if (frame.Source == IgnoreSource.IgnoreFile)
            {
                _hasIgnoreFile = true;
                break;
            }
        }
    }

    public static IgnoreStack Empty { get; } = new([], null, null);

    /// <summary>
    /// <c>--ignore-file</c> patterns match the path relative to <paramref name="cwd"/>,
    /// not the git root. Other sources stay relative to <paramref name="scopeRoot"/>.
    /// </summary>
    public IgnoreStack WithWorkingDirectory(string scopeRoot, string cwd) =>
        new(_frames, Path.GetFullPath(scopeRoot), Path.GetFullPath(cwd));

    public IgnoreStack Add(string baseRelative, string contents) =>
        Add(IgnoreSource.Git, baseRelative, contents);

    public IgnoreStack Add(IgnoreSource source, string baseRelative, string contents)
    {
        var rules = IgnoreRuleSet.Parse(contents);
        if (rules.Count == 0)
        {
            return this;
        }

        var next = new Frame[_frames.Length + 1];
        _frames.CopyTo(next, 0);
        next[^1] = new Frame(source, Normalize(baseRelative), rules);
        return new IgnoreStack(next, _scopeRoot, _cwd);
    }

    public bool IsIgnored(string relativePath, bool isDirectory) =>
        IsIgnored(relativePath.AsSpan(), isDirectory, checkParents: true);

    /// <summary>
    /// <paramref name="checkParents"/> is false while walking: ignored directories are not entered,
    /// so a child does not re-test every ancestor.
    /// </summary>
    public bool IsIgnored(ReadOnlySpan<char> relativePath, bool isDirectory, bool checkParents)
    {
        if (!NeedsNormalize(relativePath))
        {
            return Decide(relativePath, isDirectory, checkParents);
        }

        var rented = ArrayPool<char>.Shared.Rent(relativePath.Length);
        try
        {
            var written = NormalizeInto(relativePath, rented);
            return Decide(rented.AsSpan(0, written), isDirectory, checkParents);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private bool Decide(ReadOnlySpan<char> relativePath, bool isDirectory, bool checkParents)
    {
        if (checkParents)
        {
            var slash = relativePath.LastIndexOf('/');
            if (slash > 0 && Decide(relativePath[..slash], true, true))
            {
                return true;
            }
        }

        for (var source = (int)IgnoreSource.RgIgnore; source >= (int)IgnoreSource.IgnoreFile; source--)
        {
            var path = relativePath;
            string? mapped = null;
            if (source == (int)IgnoreSource.IgnoreFile)
            {
                if (!_hasIgnoreFile)
                {
                    continue;
                }

                if (_cwd != null)
                {
                    mapped = ToCwdRelative(relativePath);
                    if (mapped == null)
                    {
                        continue;
                    }

                    path = mapped;
                }
            }

            var matched = false;
            var ignored = false;
            foreach (var frame in _frames)
            {
                if ((int)frame.Source != source)
                {
                    continue;
                }

                if (!TryLocal(frame.BaseRelative, path, out var local))
                {
                    continue;
                }

                if (frame.Rules.TryDecide(local, isDirectory, out var decision))
                {
                    matched = true;
                    ignored = decision;
                }
            }

            if (matched)
            {
                return ignored;
            }
        }

        return false;
    }

    /// <summary>
    /// Scope-relative path to a working-directory-relative path.
    /// Null when the path is outside the working directory, so <c>--ignore-file</c> does not apply.
    /// </summary>
    private string? ToCwdRelative(ReadOnlySpan<char> scopeRelative)
    {
        var full = scopeRelative.Length == 0
            ? _scopeRoot!
            : Path.Join(_scopeRoot, scopeRelative.ToString().Replace('/', Path.DirectorySeparatorChar));
        var rel = Path.GetRelativePath(_cwd!, full);
        if (rel == ".")
        {
            return "";
        }

        rel = rel.Replace('\\', '/');
        if (rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            return null;
        }

        return rel;
    }

    private static bool TryLocal(string baseRelative, ReadOnlySpan<char> relativePath, out ReadOnlySpan<char> local)
    {
        if (baseRelative.Length == 0)
        {
            local = relativePath;
            return true;
        }

        if (relativePath.Length == baseRelative.Length && relativePath.Equals(baseRelative, StringComparison.Ordinal))
        {
            local = default;
            return false;
        }

        if (relativePath.Length > baseRelative.Length
            && relativePath.StartsWith(baseRelative, StringComparison.Ordinal)
            && relativePath[baseRelative.Length] == '/')
        {
            local = relativePath[(baseRelative.Length + 1)..];
            return local.Length > 0;
        }

        local = default;
        return false;
    }

    private static bool NeedsNormalize(ReadOnlySpan<char> path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        if (path[0] == '/' || path[^1] == '/' || path.Contains('\\'))
        {
            return true;
        }

        return false;
    }

    private static int NormalizeInto(ReadOnlySpan<char> path, Span<char> dest)
    {
        var start = 0;
        var end = path.Length;
        while (start < end && path[start] is '/' or '\\')
        {
            start++;
        }

        while (end > start && path[end - 1] is '/' or '\\')
        {
            end--;
        }

        var written = 0;
        for (var i = start; i < end; i++)
        {
            var c = path[i] == '\\' ? '/' : path[i];
            dest[written++] = c;
        }

        return written;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
