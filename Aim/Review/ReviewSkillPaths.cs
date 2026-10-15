using System.Text.Json;

namespace Aim;

internal sealed class ReviewSkillDiscovery
{
    public required string Root { get; init; }
    public required IReadOnlyList<string> Directories { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

internal static class ReviewSkillPaths
{
    public const int MaxConfigBytes = 65_536;
    public static readonly string[] DefaultRelativeDirectories = [".aim/skills", "skills"];

    public static ReviewSkillDiscovery Resolve(string workingDirectory, string? iniSkillsPaths)
    {
        var notes = new List<string>();
        var root = FindRoot(workingDirectory);
        var candidates = new List<(string Path, bool WarnIfMissing)>();
        foreach (var path in ReadProjectConfig(root, notes))
            candidates.Add((path, true));
        foreach (var path in SplitPaths(iniSkillsPaths))
            candidates.Add((path, true));
        foreach (var relative in DefaultRelativeDirectories)
            candidates.Add((relative, false));

        var directories = new List<string>();
        var labels = new List<string>();
        var seen = new HashSet<string>(PathComparer());
        foreach (var (path, warnIfMissing) in candidates)
        {
            if (!TryResolveDirectory(root, path, warnIfMissing, notes, out var full))
                continue;
            if (!seen.Add(full))
                continue;

            directories.Add(full);
            labels.Add(Label(root, full));
        }

        return new ReviewSkillDiscovery
        {
            Root = root,
            Directories = directories,
            Labels = labels,
            Notes = notes
        };
    }

    public static IEnumerable<string> SplitPaths(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0)
                yield return part;
        }
    }

    private static string FindRoot(string workingDirectory)
    {
        var (ok, output, _) = GitExecutor.RunGit(["rev-parse", "--show-toplevel"], workingDirectory);
        if (ok && !string.IsNullOrWhiteSpace(output))
            return output.Trim();

        return Path.GetFullPath(workingDirectory);
    }

    private static IEnumerable<string> ReadProjectConfig(string root, List<string> notes)
    {
        var file = Path.Combine(root, ".aim", "config.json");
        if (!File.Exists(file))
            yield break;

        string text;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > MaxConfigBytes)
            {
                notes.Add($"Warning: .aim/config.json exceeds {MaxConfigBytes} bytes and was ignored.");
                yield break;
            }

            text = File.ReadAllText(file);
        }
        catch (Exception ex)
        {
            notes.Add($"Warning: Could not read .aim/config.json: {ex.Message}");
            yield break;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            notes.Add($"Warning: Could not read .aim/config.json: {ex.Message}");
            yield break;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                notes.Add("Warning: .aim/config.json must be a JSON object.");
                yield break;
            }

            foreach (var path in ReadPathProperty(doc.RootElement, "skills", notes))
                yield return path;
            foreach (var path in ReadPathProperty(doc.RootElement, "skillspaths", notes))
                yield return path;
        }
    }

    private static IEnumerable<string> ReadPathProperty(JsonElement root, string name, List<string> notes)
    {
        if (!root.TryGetProperty(name, out var value))
            yield break;

        if (value.ValueKind == JsonValueKind.String)
        {
            foreach (var path in SplitPaths(value.GetString()))
                yield return path;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            notes.Add($"Warning: .aim/config.json property '{name}' must be a string or an array of strings.");
            yield break;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var path = item.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                    yield return path.Trim();
            }
            else
            {
                notes.Add($"Warning: .aim/config.json '{name}[{index}]' must be a string.");
            }

            index++;
        }
    }

    private static bool TryResolveDirectory(
        string root,
        string requested,
        bool warnIfMissing,
        List<string> notes,
        out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(requested) || requested.Contains('\0') || HasControl(requested))
        {
            if (warnIfMissing)
                notes.Add("Warning: Skipped an invalid skill path.");
            return false;
        }

        var combined = Path.IsPathRooted(requested)
            ? requested
            : Path.Combine(root, requested);
        full = Path.GetFullPath(combined);
        if (Directory.Exists(full))
            return true;

        if (warnIfMissing)
        {
            var reason = File.Exists(full) ? "is not a directory" : "was not found";
            notes.Add($"Warning: Skill path {full} {reason}.");
        }

        return false;
    }

    private static string Label(string root, string full)
    {
        if (!IsUnder(full, root))
            return full;

        var relative = Path.GetRelativePath(root, full);
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static bool IsUnder(string fullPath, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(fullPath);
        if (full.Equals(rootFull, comparison))
            return true;

        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison);
    }

    private static bool HasControl(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
                return true;
        }

        return false;
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
