using Ngrep.Indexing;
using Ngrep.Output;

namespace Ngrep.Watch;

internal static class WatchCommand
{
    public static int Run(string root, string? indexDirectory, OutputFormat format, CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Directory not found: {root}");
        }

        var index = string.IsNullOrWhiteSpace(indexDirectory)
            ? IndexPaths.DefaultFor(root)
            : Path.GetFullPath(indexDirectory);

        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = false,
        };

        var pending = 0;
        void Mark(string? path)
        {
            if (!IsSelfInflicted(root, index, path))
            {
                Interlocked.Exchange(ref pending, 1);
            }
        }

        watcher.Created += (_, e) => Mark(e.FullPath);
        watcher.Changed += (_, e) => Mark(e.FullPath);
        watcher.Deleted += (_, e) => Mark(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            if (!IsSelfInflicted(root, index, e.FullPath) || !IsSelfInflicted(root, index, e.OldFullPath))
            {
                Interlocked.Exchange(ref pending, 1);
            }
        };
        watcher.Error += (_, e) => Console.Error.WriteLine(e.GetException().Message);

        var last = Indexer.Build(root, index);
        watcher.EnableRaisingEvents = true;
        MachineOutput.WriteWatch(new WatchEvent("ready", last), format);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (cancellationToken.WaitHandle.WaitOne(300))
            {
                break;
            }

            if (Interlocked.Exchange(ref pending, 0) != 1)
            {
                continue;
            }

            try
            {
                last = Indexer.Build(root, index);
                MachineOutput.WriteWatch(new WatchEvent("reindexed", last), format);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine(ex.Message);
            }
        }

        MachineOutput.WriteWatch(new WatchEvent("stopped", last), format);
        return Cli.ExitCodes.Success;
    }

    internal static bool IsSelfInflicted(string root, string index, string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return true;
        }

        string full;
        try
        {
            full = Path.GetFullPath(fullPath);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return true;
        }

        var indexFull = Path.GetFullPath(index);
        if (IsUnder(full, indexFull) || IsUnder(full, indexFull + ".tmp") || IsUnder(full, indexFull + ".bak"))
        {
            return true;
        }

        if (IsUnder(full, Path.Combine(Path.GetFullPath(root), ".ngrep")))
        {
            return true;
        }

        if (IsUnder(full, Path.Combine(Path.GetFullPath(root), ".git")))
        {
            return true;
        }

        return false;
    }

    private static bool IsUnder(string full, string dir) =>
        full.Equals(dir, StringComparison.Ordinal)
        || full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
