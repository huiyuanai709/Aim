using Ngrep.Ignore;
using Ngrep.Indexing;

namespace Ngrep.Search;

/// <summary>
/// Paths a content scan would open, before the NUL and size checks. <c>--files</c> prints these.
/// The index is not consulted: a file added after <c>index</c> is still listed.
/// </summary>
internal static class FileList
{
    public static List<string> Collect(
        string path,
        IgnorePolicy policy,
        bool includeHidden,
        PathFilter filter,
        bool noMessages,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = Path.GetFullPath(path);
        if (!File.Exists(start) && !Directory.Exists(start))
        {
            throw new SearchPathException($"Path not found: {start}", noMessages);
        }

        if (HiddenPaths.ContainsHardSkip(start))
        {
            return [];
        }

        if (File.Exists(start))
        {
            var parent = Path.GetDirectoryName(start) ?? start;
            var relative = Path.GetFileName(start);
            // A path the user named is not skipped for being hidden. Ignore files still apply.
            if (filter.Accepts(relative) && (policy == IgnorePolicy.None || !TreeWalker.IsIgnoredFile(parent, start, policy)))
            {
                return [relative];
            }

            return [];
        }

        var results = new List<string>();
        var stats = new WalkStats();
        var root = TreeWalker.CreateRoot(start, policy);
        var options = TreeWalker.CreateOptions(root.Full, IndexPaths.DefaultFor(start), policy, includeHidden);
        var stack = new Stack<WalkDir>();
        stack.Push(root);
        var files = new List<WalkedFile>(32);
        var children = new List<WalkDir>(8);
        while (stack.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            TreeWalker.Collect(dir, options, stats, files, children, cancellation);
            foreach (var file in files)
            {
                if (filter.Accepts(file.RelativePath))
                {
                    results.Add(file.RelativePath);
                }
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }

        return results;
    }
}
