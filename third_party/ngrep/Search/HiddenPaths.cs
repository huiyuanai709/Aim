namespace Ngrep.Search;

/// <summary>
/// Hidden means a relative path component starts with <c>.</c>.
/// Hard skips are <c>.git</c> and <c>.ngrep</c> anywhere in the absolute path.
/// </summary>
internal static class HiddenPaths
{
    public static bool IsHidden(string relativePath)
    {
        if (relativePath.Length == 0 || relativePath == ".")
        {
            return false;
        }

        var span = relativePath.AsSpan();
        while (span.Length > 0)
        {
            var slash = span.IndexOf('/');
            var part = slash < 0 ? span : span[..slash];
            if (part.Length > 0 && part[0] == '.')
            {
                return true;
            }

            if (slash < 0)
            {
                return false;
            }

            span = span[(slash + 1)..];
        }

        return false;
    }

    /// <summary>
    /// A path the user named is not hidden. Components under that prefix still are.
    /// </summary>
    public static bool IsHiddenBelow(string relativePath, string prefix)
    {
        if (prefix.Length == 0 || prefix == ".")
        {
            return IsHidden(relativePath);
        }

        var normalized = prefix.Replace('\\', '/').TrimEnd('/');
        if (relativePath.Equals(normalized, StringComparison.Ordinal))
        {
            return false;
        }

        var head = normalized + "/";
        if (!relativePath.StartsWith(head, StringComparison.Ordinal))
        {
            return IsHidden(relativePath);
        }

        return IsHidden(relativePath[head.Length..]);
    }

    public static bool ContainsHardSkip(string fullPath)
    {
        var current = Path.GetFullPath(fullPath);
        while (true)
        {
            var trimmed = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (name is ".git" or ".ngrep")
            {
                return true;
            }

            var parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, trimmed, StringComparison.Ordinal))
            {
                return false;
            }

            current = parent;
        }
    }
}
