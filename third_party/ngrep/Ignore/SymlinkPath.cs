namespace Ngrep.Ignore;

/// <summary>
/// Canonical path of a directory and its ancestors. Used to stop symlink cycles
/// the way ripgrep does: a target already on the current chain is not entered.
/// Sibling links to the same directory are not a cycle and stay visitable.
/// </summary>
internal sealed class DirChain
{
    private readonly string _canonical;
    private readonly DirChain? _parent;

    public DirChain(string canonical, DirChain? parent = null)
    {
        _canonical = canonical;
        _parent = parent;
    }

    public string Canonical => _canonical;

    public bool Contains(string canonical)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        for (var node = this; node != null; node = node._parent)
        {
            if (string.Equals(node._canonical, canonical, comparison))
            {
                return true;
            }
        }

        return false;
    }

    public DirChain Descend(string canonical) => new(canonical, this);
}

/// <summary>
/// Resolves symlink components with managed <see cref="FileSystemInfo.LinkTarget"/> calls.
/// <c>..</c> is applied to the canonical parent, so a link inside a symlinked directory
/// does not pop the logical name and miss the cycle.
/// </summary>
internal static class SymlinkPath
{
    internal const int MaxLinks = 40;

    public static string Canonicalize(string path) => Walk(Path.GetFullPath(path), 0);

    /// <summary>
    /// Canonicalize a path that may still contain <c>..</c> and symlink components.
    /// <see cref="Path.GetFullPath(string)"/> is not used: it would resolve <c>..</c> lexically.
    /// </summary>
    public static string CanonicalizeRaw(string path) => Walk(path, 0);

    private static string Walk(string path, int depth)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return path;
        }

        var current = root;
        if (path.Length <= root.Length)
        {
            return root;
        }

        var rest = path[root.Length..];
        foreach (var segment in rest.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                current = Parent(current, root);
                continue;
            }

            current = Resolve(current, segment, depth);
        }

        return current;
    }

    private static string Parent(string current, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var trimmed = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0 || string.Equals(trimmed, rootTrimmed, comparison))
        {
            return root;
        }

        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent))
        {
            return root;
        }

        return parent;
    }

    private static string Resolve(string current, string name, int depth)
    {
        var next = Path.Join(current, name);
        if (depth >= MaxLinks)
        {
            return next;
        }

        string? raw;
        try
        {
            raw = new FileInfo(next).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return next;
        }

        if (string.IsNullOrEmpty(raw))
        {
            return next;
        }

        if (Path.IsPathRooted(raw))
        {
            return Walk(raw, depth + 1);
        }

        return Walk(Path.Join(current, raw), depth + 1);
    }
}
