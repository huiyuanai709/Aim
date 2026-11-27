namespace Ngrep.Ignore;

internal readonly record struct WalkedFile(string FullPath, string RelativePath);

internal readonly struct WalkDir
{
    public string Full { get; init; }

    public string WalkRelative { get; init; }

    public string ScopeRelative { get; init; }

    public IgnoreStack Ignores { get; init; }

    /// <summary>Ripgrep depth. The start path is 0. Its immediate children are 1.</summary>
    public int Depth { get; init; }

    /// <summary>Set when <see cref="WalkOptions.FollowSymlinks"/> is on. Null otherwise.</summary>
    public DirChain? Chain { get; init; }
}

internal readonly struct WalkOptions
{
    public WalkOptions(IgnorePolicy policy, bool includeHidden, string? indexRelative, int? maxDepth = null, bool followSymlinks = false)
    {
        Policy = policy;
        IncludeHidden = includeHidden;
        IndexRelative = indexRelative;
        MaxDepth = maxDepth;
        FollowSymlinks = followSymlinks;
    }

    public IgnorePolicy Policy { get; }

    public bool IncludeHidden { get; }

    public string? IndexRelative { get; }

    /// <summary>
    /// Ripgrep <c>-d</c>. Null walks without a cap. 0 is the start path and does not list a directory.
    /// 1 lists that directory and stops. A directory is listed only when its depth is less than the cap.
    /// </summary>
    public int? MaxDepth { get; }

    /// <summary><c>-L</c> / <c>--follow</c>. Off unless the caller opts in.</summary>
    public bool FollowSymlinks { get; }
}

internal sealed class WalkStats
{
    private int _skippedIgnored;
    private int _skippedBinary;
    private int _skippedTooLarge;
    private int _skippedUnreadable;

    public int SkippedIgnored => Volatile.Read(ref _skippedIgnored);

    public int SkippedBinary => Volatile.Read(ref _skippedBinary);

    public int SkippedTooLarge => Volatile.Read(ref _skippedTooLarge);

    public int SkippedUnreadable => Volatile.Read(ref _skippedUnreadable);

    public void IncrementSkippedIgnored() => Interlocked.Increment(ref _skippedIgnored);

    public void IncrementSkippedBinary() => Interlocked.Increment(ref _skippedBinary);

    public void IncrementSkippedTooLarge() => Interlocked.Increment(ref _skippedTooLarge);

    public void IncrementSkippedUnreadable() => Interlocked.Increment(ref _skippedUnreadable);
}

internal static class TreeWalker
{
    /// <summary>Test hook. Invoked with the directory full path before its entries are listed.</summary>
    internal static Action<string>? BeforeCollect { get; set; }

    /// <summary>
    /// A dot name, or a Windows/macOS hidden attribute. <paramref name="includeHidden"/> (--hidden)
    /// is applied by the caller.
    /// </summary>
    internal static bool IsHiddenEntry(string name, bool hiddenAttribute)
    {
        if (name.Length > 0 && name[0] == '.')
        {
            return true;
        }

        return hiddenAttribute;
    }

    /// <summary>
    /// True when a content scan of <paramref name="walkRoot"/> would not yield <paramref name="fullPath"/>.
    /// Hidden names are included, so a path the user named is not rejected only for a leading dot.
    /// Symlinks are not yielded, so a named symlink is treated as not searched.
    /// Only the path's own components are examined: the tree is not listed, which keeps a
    /// search of one named file from costing a walk of its whole parent directory.
    /// </summary>
    public static bool IsIgnoredFile(string walkRoot, string fullPath, IgnorePolicy? policy = null)
    {
        var chosen = policy ?? IgnorePolicy.All;
        var root = CreateRoot(walkRoot, chosen);
        var target = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(root.Full, target).Replace('\\', '/');
        if (relative is "" or "." || Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }

        // The walk reads a listed directory's ignore files before it checks that
        // directory's entries, so the root's files come first and each level adds its own.
        var ignores = AddDirectoryIgnoreFiles(root.Ignores, root.Full, root.ScopeRelative, chosen);
        var scope = root.ScopeRelative;
        var dir = root.Full;
        var parts = relative.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var name = parts[i];
            var last = i == parts.Length - 1;
            var full = Path.Join(dir, name);
            var kind = KindOfPath(full);
            if (kind == DirentKind.Other)
            {
                return true;
            }

            // The walk never yields links or directories here: this check is the default walk.
            if (kind == DirentKind.Symlink || (last && kind == DirentKind.Directory))
            {
                return true;
            }

            if (kind == DirentKind.Directory && name is ".git" or ".ngrep")
            {
                return true;
            }

            scope = JoinRelative(scope, name);
            // Ignored directories are never entered, so no ancestor is re-tested.
            if (ignores.IsIgnored(scope, isDirectory: !last, checkParents: false))
            {
                return true;
            }

            if (last)
            {
                break;
            }

            dir = full;
            ignores = AddDirectoryIgnoreFiles(ignores, dir, scope, chosen);
        }

        return false;
    }

    private static IgnoreStack AddDirectoryIgnoreFiles(IgnoreStack ignores, string directory, string scopeRelative, IgnorePolicy policy)
    {
        if (policy.Vcs)
        {
            ignores = TryAddIfExists(ignores, Path.Join(directory, ".gitignore"), IgnoreSource.Git, scopeRelative);
        }

        if (policy.Custom)
        {
            ignores = TryAddIfExists(ignores, Path.Join(directory, ".ignore"), IgnoreSource.DotIgnore, scopeRelative);
            ignores = TryAddIfExists(ignores, Path.Join(directory, ".rgignore"), IgnoreSource.RgIgnore, scopeRelative);
        }

        return ignores;
    }

    private static DirentKind KindOfPath(string full)
    {
        try
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return DirentKind.Symlink;
            }

            return (attributes & FileAttributes.Directory) != 0 ? DirentKind.Directory : DirentKind.File;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DirentKind.Other;
        }
    }

    /// <summary>
    /// Depth of <paramref name="walkRelative"/> from the start path. <c>""</c> is 0, <c>a</c> is 1, <c>a/b</c> is 2.
    /// </summary>
    internal static int DepthOf(string walkRelative)
    {
        if (walkRelative.Length == 0)
        {
            return 0;
        }

        var depth = 1;
        for (var i = 0; i < walkRelative.Length; i++)
        {
            if (walkRelative[i] == '/')
            {
                depth++;
            }
        }

        return depth;
    }

    public static IEnumerable<WalkedFile> Enumerate(
        string walkRoot,
        string? indexDirectory,
        WalkStats stats,
        IgnorePolicy? policy = null,
        bool includeHidden = false,
        int? maxDepth = null,
        bool followSymlinks = false)
    {
        var chosen = policy ?? IgnorePolicy.All;
        var root = CreateRoot(walkRoot, chosen);
        var options = new WalkOptions(chosen, includeHidden, IndexRelative(root.Full, indexDirectory), maxDepth, followSymlinks);
        root = Start(root, options);
        var stack = new Stack<WalkDir>();
        stack.Push(root);
        var files = new List<WalkedFile>(32);
        var children = new List<WalkDir>(8);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            Collect(dir, options, stats, files, children);
            foreach (var file in files)
            {
                yield return file;
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }
    }

    public static WalkOptions CreateOptions(string walkRootFull, string? indexDirectory, IgnorePolicy policy, bool includeHidden, int? maxDepth = null, bool followSymlinks = false) =>
        new(policy, includeHidden, IndexRelative(walkRootFull, indexDirectory), maxDepth, followSymlinks);

    /// <summary>
    /// Records the walk root's canonical path when symlinks are followed.
    /// Children copy that chain so a link back to an ancestor is not entered.
    /// </summary>
    public static WalkDir Start(WalkDir root, WalkOptions options)
    {
        if (!options.FollowSymlinks || root.Chain != null)
        {
            return root;
        }

        return new WalkDir
        {
            Full = root.Full,
            WalkRelative = root.WalkRelative,
            ScopeRelative = root.ScopeRelative,
            Ignores = root.Ignores,
            Depth = root.Depth,
            Chain = new DirChain(SymlinkPath.Canonicalize(root.Full)),
        };
    }

    public static WalkDir CreateRoot(string walkRoot, IgnorePolicy policy)
    {
        walkRoot = Path.GetFullPath(walkRoot);
        var scopeRoot = FindGitRoot(walkRoot) ?? walkRoot;
        var scopeRelative = Relative(scopeRoot, walkRoot);
        var ignores = policy.Vcs || policy.Custom
            ? LoadAncestors(scopeRoot, walkRoot, policy)
            : IgnoreStack.Empty;
        ignores = ignores.WithWorkingDirectory(scopeRoot, Directory.GetCurrentDirectory());
        ignores = AddExplicitFiles(ignores, policy);
        return new WalkDir
        {
            Full = walkRoot,
            WalkRelative = "",
            ScopeRelative = scopeRelative,
            Ignores = ignores,
            Depth = 0,
        };
    }

    /// <summary>
    /// Every ignore frame a walk of <paramref name="walkRoot"/> would apply, parents before children.
    /// Sibling rules are on the same stack and do not match each other's paths.
    /// Used to drop index hits that <c>--ignore-file</c> excludes without reading file contents.
    /// </summary>
    public static IgnoreStack LoadStack(string walkRoot, IgnorePolicy policy, bool includeHidden)
    {
        var root = CreateRoot(walkRoot, policy);
        var options = CreateOptions(root.Full, indexDirectory: null, policy, includeHidden);
        var merged = root.Ignores;
        var pending = new Stack<WalkDir>();
        pending.Push(root);
        var stats = new WalkStats();
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            var sink = new ListSink();
            if (!DirectoryEntries.TryList(dir.Full, ref sink, default))
            {
                continue;
            }

            var entries = sink.Entries;
            if (entries == null || entries.Count == 0)
            {
                continue;
            }

            entries.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
            if (policy.Vcs || policy.Custom)
            {
                merged = AddListedIgnoreFiles(merged, dir, entries, policy, stats);
            }

            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (entry.Kind != DirentKind.Directory)
                {
                    continue;
                }

                var name = entry.Name;
                var childWalk = JoinRelative(dir.WalkRelative, name);
                if (name is ".git" or ".ngrep" || IsIndexPath(childWalk, options.IndexRelative))
                {
                    continue;
                }

                if (!includeHidden && IsHiddenEntry(name, entry.Hidden))
                {
                    continue;
                }

                var scopeRelative = JoinRelative(dir.ScopeRelative, name);
                if (merged.IsIgnored(scopeRelative, true, checkParents: false))
                {
                    continue;
                }

                pending.Push(new WalkDir
                {
                    Full = Path.Join(dir.Full, name),
                    WalkRelative = childWalk,
                    ScopeRelative = scopeRelative,
                    Ignores = merged,
                });
            }
        }

        return merged;
    }

    /// <summary>Path of <paramref name="walkRelative"/> relative to the git root that owns <paramref name="walkRoot"/>.</summary>
    public static string ScopePath(string walkRoot, string walkRelative)
    {
        walkRoot = Path.GetFullPath(walkRoot);
        var scope = FindGitRoot(walkRoot) ?? walkRoot;
        return JoinRelative(Relative(scope, walkRoot), walkRelative);
    }

    private static IgnoreStack AddExplicitFiles(IgnoreStack stack, IgnorePolicy policy)
    {
        foreach (var file in policy.Files)
        {
            var full = Path.GetFullPath(file);
            if (!File.Exists(full))
            {
                throw new ArgumentException($"Ignore file not found: {full}");
            }

            try
            {
                stack = stack.Add(IgnoreSource.IgnoreFile, "", File.ReadAllText(full));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                throw new ArgumentException($"Ignore file not readable: {full}");
            }
        }

        return stack;
    }

    public static void Collect(WalkDir dir, WalkOptions options, WalkStats stats, List<WalkedFile> files, List<WalkDir> children, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        files.Clear();
        children.Clear();
        // A directory at the cap is not listed. Its children would be deeper than the cap.
        // Depth 0 therefore does not read the start directory. Ignore rules of a directory
        // we do not list are left unread; ancestors were already loaded on the root.
        if (options.MaxDepth is int limit && dir.Depth >= limit)
        {
            return;
        }

        BeforeCollect?.Invoke(dir.Full);
        cancellation.ThrowIfCancellationRequested();
        var sink = new ListSink();
        if (!DirectoryEntries.TryList(dir.Full, ref sink, cancellation))
        {
            stats.IncrementSkippedUnreadable();
            return;
        }

        var entries = sink.Entries;
        if (entries == null)
        {
            return;
        }

        entries.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        var ignores = dir.Ignores;
        if (options.Policy.Vcs || options.Policy.Custom)
        {
            ignores = AddListedIgnoreFiles(ignores, dir, entries, options.Policy, stats);
        }

        var chain = dir.Chain;
        if (options.FollowSymlinks && chain == null)
        {
            chain = new DirChain(SymlinkPath.Canonicalize(dir.Full));
        }

        foreach (var entry in entries)
        {
            var name = entry.Name;
            var kind = entry.Kind;
            if (kind == DirentKind.Symlink && !options.FollowSymlinks)
            {
                continue;
            }

            if (kind == DirentKind.Other)
            {
                continue;
            }

            var childWalk = JoinRelative(dir.WalkRelative, name);
            // .git, .ngrep, and the index directory stay skipped even when their names are links.
            if ((kind is DirentKind.Directory or DirentKind.Symlink)
                && (name is ".git" or ".ngrep" || IsIndexPath(childWalk, options.IndexRelative)))
            {
                continue;
            }

            // Dot names and the Windows/macOS hidden attribute are skipped unless the
            // caller asked for them. Ignore files were read above; the files themselves
            // are not hits unless hidden names are included.
            if (!options.IncludeHidden && IsHiddenEntry(name, entry.Hidden))
            {
                continue;
            }

            string? followedCanonical = null;
            if (kind == DirentKind.Symlink)
            {
                if (!TryFollow(Path.Join(dir.Full, name), chain!, out kind, out followedCanonical))
                {
                    stats.IncrementSkippedUnreadable();
                    continue;
                }
            }

            var isDir = kind == DirentKind.Directory;
            var scopeRelative = JoinRelative(dir.ScopeRelative, name);
            if (ignores.IsIgnored(scopeRelative, isDir, checkParents: false))
            {
                if (!isDir)
                {
                    stats.IncrementSkippedIgnored();
                }

                continue;
            }

            var full = Path.Join(dir.Full, name);
            if (isDir)
            {
                var childDepth = DepthOf(childWalk);
                if (options.MaxDepth is int childLimit && childDepth >= childLimit)
                {
                    continue;
                }

                DirChain? next = null;
                if (options.FollowSymlinks)
                {
                    var canonical = followedCanonical ?? Path.Join(chain!.Canonical, name);
                    if (chain!.Contains(canonical))
                    {
                        continue;
                    }

                    next = chain.Descend(canonical);
                }

                children.Add(new WalkDir
                {
                    Full = full,
                    WalkRelative = childWalk,
                    ScopeRelative = scopeRelative,
                    Ignores = ignores,
                    Depth = childDepth,
                    Chain = next,
                });
                continue;
            }

            files.Add(new WalkedFile(full, childWalk));
        }
    }

    /// <summary>
    /// Classify a symlink the caller asked to follow. The returned canonical path is the
    /// real directory target. File links report no canonical path; the walk still opens
    /// the link path, which is the path printed to the user.
    /// </summary>
    private static bool TryFollow(string linkPath, DirChain parent, out DirentKind kind, out string? canonical)
    {
        kind = DirentKind.Other;
        canonical = null;
        try
        {
            var raw = new FileInfo(linkPath).LinkTarget;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            var target = Path.IsPathRooted(raw)
                ? SymlinkPath.CanonicalizeRaw(raw)
                : SymlinkPath.CanonicalizeRaw(Path.Join(parent.Canonical, raw));
            var attributes = File.GetAttributes(target);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                kind = DirentKind.Directory;
                canonical = target;
                return true;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            kind = DirentKind.File;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string? FindGitRoot(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir != null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string JoinRelative(string parent, string name)
    {
        if (parent.Length == 0)
        {
            return name;
        }

        return string.Concat(parent, "/", name);
    }

    private static IgnoreStack AddListedIgnoreFiles(
        IgnoreStack ignores,
        WalkDir dir,
        List<(string Name, DirentKind Kind, bool Hidden)> entries,
        IgnorePolicy policy,
        WalkStats stats)
    {
        var git = false;
        var dot = false;
        var rg = false;
        for (var i = 0; i < entries.Count; i++)
        {
            switch (entries[i].Name)
            {
                case ".gitignore":
                    git = true;
                    break;
                case ".ignore":
                    dot = true;
                    break;
                case ".rgignore":
                    rg = true;
                    break;
            }
        }

        if (policy.Vcs && git)
        {
            ignores = TryAdd(ignores, Path.Join(dir.Full, ".gitignore"), IgnoreSource.Git, dir.ScopeRelative, stats);
        }

        if (policy.Custom && dot)
        {
            ignores = TryAdd(ignores, Path.Join(dir.Full, ".ignore"), IgnoreSource.DotIgnore, dir.ScopeRelative, stats);
        }

        if (policy.Custom && rg)
        {
            ignores = TryAdd(ignores, Path.Join(dir.Full, ".rgignore"), IgnoreSource.RgIgnore, dir.ScopeRelative, stats);
        }

        return ignores;
    }

    private static IgnoreStack LoadAncestors(string scopeRoot, string walkRoot, IgnorePolicy policy)
    {
        var stack = IgnoreStack.Empty;
        if (policy.Vcs)
        {
            stack = LoadVcsRoot(stack, scopeRoot);
        }

        var walkFull = Path.GetFullPath(walkRoot);
        foreach (var dir in Chain(scopeRoot, walkRoot))
        {
            if (string.Equals(dir, walkFull, StringComparison.Ordinal))
            {
                continue;
            }

            var rel = Relative(scopeRoot, dir);
            if (policy.Vcs)
            {
                stack = TryAddIfExists(stack, Path.Combine(dir, ".gitignore"), IgnoreSource.Git, rel);
            }

            if (policy.Custom)
            {
                stack = TryAddIfExists(stack, Path.Combine(dir, ".ignore"), IgnoreSource.DotIgnore, rel);
                stack = TryAddIfExists(stack, Path.Combine(dir, ".rgignore"), IgnoreSource.RgIgnore, rel);
            }
        }

        return stack;
    }

    /// <summary>
    /// Global excludes and <c>info/exclude</c> apply only when <paramref name="scopeRoot"/> has a
    /// <c>.git</c> file or directory. Patterns are relative to that git root.
    /// </summary>
    private static IgnoreStack LoadVcsRoot(IgnoreStack stack, string scopeRoot)
    {
        var common = GitConfig.ResolveCommonGitDir(scopeRoot);
        if (common == null)
        {
            return stack;
        }

        var excludes = GitConfig.FindExcludesPath(common);
        if (!string.IsNullOrEmpty(excludes))
        {
            stack = TryAddIfExists(stack, excludes, IgnoreSource.Global, "");
        }

        return TryAddIfExists(stack, Path.Combine(common, "info", "exclude"), IgnoreSource.Exclude, "");
    }

    private static IgnoreStack TryAddIfExists(IgnoreStack stack, string path, IgnoreSource source, string baseRelative)
    {
        if (!File.Exists(path))
        {
            return stack;
        }

        return TryAdd(stack, path, source, baseRelative, stats: null);
    }

    private static IgnoreStack TryAdd(IgnoreStack stack, string path, IgnoreSource source, string baseRelative, WalkStats? stats)
    {
        try
        {
            return stack.Add(source, baseRelative, File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stats?.IncrementSkippedUnreadable();
            return stack;
        }
    }

    private static IEnumerable<string> Chain(string scopeRoot, string walkRoot)
    {
        var chain = new List<string>();
        var current = Path.GetFullPath(walkRoot);
        var top = Path.GetFullPath(scopeRoot);
        while (true)
        {
            chain.Add(current);
            if (string.Equals(current, top, StringComparison.Ordinal))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        chain.Reverse();
        return chain;
    }

    private static string Relative(string scopeRoot, string walkRoot)
    {
        var rel = Path.GetRelativePath(scopeRoot, walkRoot).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    private static string? IndexRelative(string walkRoot, string? indexDirectory)
    {
        if (string.IsNullOrEmpty(indexDirectory))
        {
            return ".ngrep/index";
        }

        var full = Path.GetFullPath(indexDirectory);
        var rel = Path.GetRelativePath(walkRoot, full).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            return null;
        }

        return rel == "." ? "" : rel;
    }

    private static bool IsIndexPath(string walkRelative, string? indexRelative)
    {
        if (string.IsNullOrEmpty(indexRelative))
        {
            return false;
        }

        return walkRelative.Equals(indexRelative, StringComparison.Ordinal)
            || walkRelative.StartsWith(indexRelative + "/", StringComparison.Ordinal);
    }

    private struct ListSink : IDirentSink
    {
        public List<(string Name, DirentKind Kind, bool Hidden)> Entries;

        public void OnEntry(string name, DirentKind kind, bool hiddenAttribute)
        {
            Entries ??= new List<(string, DirentKind, bool)>(32);
            Entries.Add((name, kind, hiddenAttribute));
        }
    }
}
