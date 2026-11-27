using System.Text;

namespace Ngrep.Ignore;

/// <summary>
/// Git config locations used to resolve <c>core.excludesFile</c>.
/// Paths are explicit so tests can supply fixtures without the process environment.
/// </summary>
internal readonly record struct GitConfigFiles(
    string? SystemPath,
    string? XdgConfigPath,
    string? GlobalPath,
    string? LocalPath,
    string? DefaultExcludesPath,
    string? Home)
{
    public static GitConfigFiles FromEnvironment(string? gitCommonDir)
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("USERPROFILE");
        }

        string? system = null;
        var systemOverride = Environment.GetEnvironmentVariable("GIT_CONFIG_SYSTEM");
        if (!string.IsNullOrEmpty(systemOverride))
        {
            system = systemOverride;
        }
        else if (Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM") == null && !OperatingSystem.IsWindows())
        {
            system = "/etc/gitconfig";
        }

        string? xdgConfig = null;
        string? global = null;
        var globalOverride = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        if (!string.IsNullOrEmpty(globalOverride))
        {
            // Replaces both $XDG_CONFIG_HOME/git/config and ~/.gitconfig.
            global = globalOverride;
        }
        else
        {
            var xdg = XdgConfigHome(home);
            if (xdg != null)
            {
                xdgConfig = Path.Combine(xdg, "git", "config");
            }

            if (!string.IsNullOrEmpty(home))
            {
                global = Path.Combine(home, ".gitconfig");
            }
        }

        var local = string.IsNullOrEmpty(gitCommonDir) ? null : Path.Combine(gitCommonDir, "config");
        return new GitConfigFiles(system, xdgConfig, global, local, DefaultExcludes(home), home);
    }

    private static string? XdgConfigHome(string? home)
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return xdg;
        }

        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".config");
    }

    private static string? DefaultExcludes(string? home)
    {
        var xdg = XdgConfigHome(home);
        return xdg == null ? null : Path.Combine(xdg, "git", "ignore");
    }
}

/// <summary>
/// Reads <c>core.excludesFile</c> from git config files. Does not start git.
/// </summary>
internal static class GitConfig
{
    /// <summary>
    /// Absolute path of the global excludes file for <paramref name="gitCommonDir"/>, or the
    /// default <c>$XDG_CONFIG_HOME/git/ignore</c> when the key is unset. The file may not exist.
    /// </summary>
    public static string? FindExcludesPath(string? gitCommonDir) =>
        ResolveExcludesPath(GitConfigFiles.FromEnvironment(gitCommonDir));

    /// <summary>
    /// Last <c>core.excludesFile</c> across system, XDG, global, and local config, or the default path.
    /// <c>include.path</c> is read in place. <c>includeIf</c> is not.
    /// </summary>
    internal static string? ResolveExcludesPath(GitConfigFiles files)
    {
        string? raw = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in new[] { files.SystemPath, files.XdgConfigPath, files.GlobalPath, files.LocalPath })
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            ReadFile(path, files.Home, seen, 0, ref raw);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return files.DefaultExcludesPath;
        }

        return MakeAbsolute(ExpandTilde(raw.Trim(), files.Home));
    }

    /// <summary>
    /// The common git directory for <paramref name="workTree"/>, where <c>info/exclude</c> and
    /// <c>config</c> live. A <c>.git</c> file (<c>gitdir:</c>) uses <c>commondir</c> when that file exists.
    /// </summary>
    public static string? ResolveCommonGitDir(string workTree)
    {
        var gitPath = Path.Combine(workTree, ".git");
        if (Directory.Exists(gitPath))
        {
            return gitPath;
        }

        if (!File.Exists(gitPath))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(gitPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var line = FirstLine(text).Trim();
        const string prefix = "gitdir:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var spec = line[prefix.Length..].Trim();
        if (spec.Length == 0)
        {
            return null;
        }

        var gitDir = Path.IsPathRooted(spec) ? spec : Path.GetFullPath(Path.Combine(workTree, spec));
        var commonFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonFile))
        {
            return gitDir;
        }

        try
        {
            var common = FirstLine(File.ReadAllText(commonFile)).Trim();
            if (common.Length == 0)
            {
                return gitDir;
            }

            return Path.IsPathRooted(common) ? common : Path.GetFullPath(Path.Combine(gitDir, common));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return gitDir;
        }
    }

    private static void ReadFile(string path, string? home, HashSet<string> seen, int depth, ref string? excludes)
    {
        if (depth > 10)
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return;
        }

        if (!seen.Add(full) || !File.Exists(full))
        {
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        var dir = Path.GetDirectoryName(full);
        foreach (var entry in Parse(text))
        {
            if (entry.Include)
            {
                var included = ExpandTilde(entry.Value, home);
                if (included.Length == 0)
                {
                    continue;
                }

                if (!Path.IsPathRooted(included) && dir != null)
                {
                    included = Path.Combine(dir, included);
                }

                ReadFile(included, home, seen, depth + 1, ref excludes);
                continue;
            }

            excludes = entry.Value;
        }
    }

    private readonly record struct ConfigEvent(bool Include, string Value);

    private static List<ConfigEvent> Parse(string text)
    {
        var events = new List<ConfigEvent>();
        var section = "";
        var subsection = false;
        foreach (var logical in LogicalLines(text))
        {
            var line = logical.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                ParseSection(line, out section, out subsection);
                continue;
            }

            if (section.Length == 0 || subsection)
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = ValueOf(line[(eq + 1)..]);
            if (section.Equals("core", StringComparison.OrdinalIgnoreCase)
                && key.Equals("excludesFile", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(new ConfigEvent(false, value));
            }
            else if (section.Equals("include", StringComparison.OrdinalIgnoreCase)
                && key.Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(new ConfigEvent(true, value));
            }
        }

        return events;
    }

    private static void ParseSection(string line, out string section, out bool subsection)
    {
        section = "";
        subsection = false;
        var end = line.IndexOf(']');
        if (end < 1)
        {
            return;
        }

        var body = line[1..end].Trim();
        if (body.Length == 0)
        {
            return;
        }

        var split = body.IndexOfAny([' ', '\t', '"']);
        if (split < 0)
        {
            section = body;
            return;
        }

        section = body[..split].Trim();
        subsection = true;
    }

    private static string ValueOf(string afterEquals)
    {
        var trimmed = afterEquals.Trim();
        if (trimmed.Length > 0 && trimmed[0] == '"')
        {
            return Unquote(trimmed);
        }

        return trimmed;
    }

    private static string Unquote(string value)
    {
        var sb = new StringBuilder();
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
            {
                return sb.ToString();
            }

            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'b' => '\b',
                    _ => next,
                });
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static List<string> LogicalLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..];
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var piece in normalized.Split('\n'))
        {
            current.Append(piece);
            if (IsContinuation(current))
            {
                current.Length--;
                continue;
            }

            lines.Add(current.ToString());
            current.Clear();
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private static bool IsContinuation(StringBuilder line)
    {
        var slashes = 0;
        for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; i--)
        {
            slashes++;
        }

        return slashes % 2 == 1;
    }

    private static string ExpandTilde(string value, string? home)
    {
        if (value.Length == 0 || value[0] != '~' || string.IsNullOrEmpty(home))
        {
            return value;
        }

        if (value.Length == 1)
        {
            return home;
        }

        if (value[1] is not ('/' or '\\'))
        {
            return value;
        }

        var rest = value[2..];
        return rest.Length == 0 ? home : Path.Combine(home, rest);
    }

    private static string MakeAbsolute(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\n', '\r']);
        var line = end < 0 ? text : text[..end];
        if (line.Length > 0 && line[0] == '\uFEFF')
        {
            line = line[1..];
        }

        return line;
    }
}
