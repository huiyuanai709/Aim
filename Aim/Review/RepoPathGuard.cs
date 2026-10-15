namespace Aim;

internal static class RepoPathGuard
{
    public static bool TryNormalize(string? requested, out string relative, out string error)
    {
        relative = "";
        if (string.IsNullOrWhiteSpace(requested))
        {
            error = "Path is required.";
            return false;
        }

        var path = requested.Replace('\\', '/').Trim();
        if (path.Contains('\0') || path.StartsWith('/') || path.Contains(':'))
        {
            error = "Path must be a repo-relative path.";
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                error = "Path must stay inside the repository.";
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            error = "Path is required.";
            return false;
        }

        relative = string.Join('/', segments);
        if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
        {
            error = "Path must stay inside the repository.";
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryResolve(string repoRoot, string? requested, out string relative, out string fullPath, out string error)
    {
        fullPath = "";
        if (!TryNormalize(requested, out relative, out error))
            return false;

        var root = Path.GetFullPath(repoRoot);
        fullPath = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnder(fullPath, root))
        {
            error = "Path must stay inside the repository.";
            return false;
        }

        if (EscapesViaLink(root, fullPath))
        {
            error = "Path must stay inside the repository.";
            return false;
        }

        return true;
    }

    public static bool IsUnder(string fullPath, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(fullPath);
        if (full.Equals(rootFull, comparison))
            return true;

        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison);
    }

    private static bool EscapesViaLink(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or ".")
                continue;

            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                return false;

            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is null)
                continue;

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is null)
                continue;

            var resolvedFull = Path.GetFullPath(resolved.FullName);
            if (!IsUnder(resolvedFull, root))
                return true;

            current = resolvedFull;
        }

        return false;
    }
}
