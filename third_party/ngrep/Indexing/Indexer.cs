using System.Collections.Concurrent;
using System.Globalization;
using Ngrep.Ignore;
using Ngrep.Text;

namespace Ngrep.Indexing;

internal static class Indexer
{
    /// <summary>Added, updated, or removed paths applied between snapshot commits.</summary>
    internal const int DefaultFlushFiles = 4096;

    /// <summary>Raw file bytes of those paths. The read-ahead window is separate.</summary>
    internal const long DefaultFlushBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Builds or updates an index.
    /// <paramref name="maxDegreeOfParallelism"/> 0 uses <c>NGREP_THREADS</c>, then <c>NETGREP_THREADS</c>,
    /// otherwise <see cref="Environment.ProcessorCount"/>. File bytes are read with
    /// <see cref="TextFiles.ReadAsync"/> on that many workers. Document ids, postings, and
    /// progress stay on the caller, in path order.
    /// When <paramref name="onPersisted"/> is null, dirty documents are committed in chunks
    /// (every <paramref name="flushFileLimit"/> paths or <paramref name="flushByteLimit"/>
    /// raw bytes, default <see cref="DefaultFlushFiles"/> / <see cref="DefaultFlushBytes"/>),
    /// not once per file and not only at the end. A callback still commits each added,
    /// updated, or removed path before the callback runs.
    /// </summary>
    public static IndexResult Build(
        string root,
        string? indexDirectory = null,
        bool showProgress = false,
        Action<string, string>? onPersisted = null,
        int maxDegreeOfParallelism = 0,
        int flushFileLimit = 0,
        long flushByteLimit = 0)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Directory not found: {root}");
        }

        var workers = ResolveWorkers(maxDegreeOfParallelism);
        var fileLimit = flushFileLimit == 0 ? DefaultFlushFiles : flushFileLimit;
        var byteLimit = flushByteLimit == 0 ? DefaultFlushBytes : flushByteLimit;
        if (fileLimit < 1 || byteLimit < 1)
        {
            throw new ArgumentException("Flush limits must be positive.");
        }
        var indexPath = string.IsNullOrWhiteSpace(indexDirectory)
            ? IndexPaths.DefaultFor(root)
            : Path.GetFullPath(indexDirectory);

        var existing = IndexStore.TryLoad(indexPath);
        var documents = existing?.Documents.ToDictionary(d => d.Path, StringComparer.Ordinal)
            ?? new Dictionary<string, DocumentRecord>(StringComparer.Ordinal);
        var postings = existing?.Postings ?? new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var createdUtc = existing?.Manifest.CreatedUtc;
        var nextId = documents.Count == 0 ? 1 : documents.Values.Max(d => d.Id) + 1;

        var stats = new WalkStats();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        var removed = 0;
        var examined = 0;
        var persistEach = onPersisted != null;
        var pending = new List<(string Kind, string Path)>();
        var dirtyFiles = 0;
        long dirtyBytes = 0;

        // Hidden files stay in the index so --hidden can filter at search time.
        // Symlinks stay skipped. Search -L scans, because this snapshot does not follow them.
        var files = TreeWalker.Enumerate(root, indexPath, stats, includeHidden: true).ToList();
        files.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        var previous = new DocumentRecord?[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            if (documents.TryGetValue(files[i].RelativePath, out var doc))
            {
                previous[i] = doc;
            }
        }

        var intern = new ConcurrentDictionary<ulong, string>();
        Feed(files, previous, intern, workers, (index, prep) =>
        {
            try
            {
                if (prep.Kind == PrepKind.Fault)
                {
                    throw prep.Error ?? new IOException("Failed to read a file while indexing.");
                }

                Apply(files[index], prep);
            }
            finally
            {
                examined++;
                if (showProgress && examined % 100 == 0 && examined != files.Count)
                {
                    WriteSummary();
                }
            }
        });

        foreach (var path in documents.Keys.Where(path => !seen.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray())
        {
            RemoveDoc(postings, documents[path].Id);
            documents.Remove(path);
            removed++;
            Note("removed", path, 0);
        }

        if (persistEach)
        {
            Commit(null, null);
        }
        else
        {
            FlushChunk();
        }

        if (showProgress)
        {
            WriteSummary();
        }

        return new IndexResult
        {
            Root = root,
            IndexPath = indexPath,
            Documents = documents.Count,
            Added = added,
            Updated = updated,
            Unchanged = unchanged,
            Removed = removed,
            SkippedIgnored = stats.SkippedIgnored,
            SkippedBinary = stats.SkippedBinary,
            SkippedTooLarge = stats.SkippedTooLarge,
            SkippedUnreadable = stats.SkippedUnreadable,
        };

        void Apply(WalkedFile file, Prep prep)
        {
            switch (prep.Kind)
            {
                case PrepKind.Unreadable:
                    stats.IncrementSkippedUnreadable();
                    return;
                case PrepKind.TooLarge:
                    stats.IncrementSkippedTooLarge();
                    return;
                case PrepKind.Binary:
                    stats.IncrementSkippedBinary();
                    return;
                case PrepKind.Unchanged:
                    seen.Add(file.RelativePath);
                    unchanged++;
                    return;
                case PrepKind.HashMatch:
                    var matched = documents[file.RelativePath];
                    matched.MtimeUnixMs = prep.Mtime;
                    matched.Size = prep.Size;
                    matched.CachedLine = null;
                    seen.Add(file.RelativePath);
                    unchanged++;
                    return;
                case PrepKind.Index:
                    break;
                default:
                    throw prep.Error ?? new InvalidOperationException("Failed to read a file while indexing.");
            }

            documents.TryGetValue(file.RelativePath, out var existing);
            var id = existing?.Id ?? nextId++;
            var kind = existing == null ? "added" : "updated";
            if (existing != null)
            {
                RemoveDoc(postings, existing.Id);
                updated++;
            }
            else
            {
                added++;
            }

            var grams = prep.Grams;
            if (grams != null)
            {
                foreach (var gram in grams)
                {
                    AddDoc(postings, gram, id);
                }
            }

            documents[file.RelativePath] = new DocumentRecord
            {
                Id = id,
                Path = file.RelativePath,
                Size = prep.Size,
                MtimeUnixMs = prep.Mtime,
                Sha256 = prep.Hash ?? "",
            };
            seen.Add(file.RelativePath);
            Note(kind, file.RelativePath, prep.Size);
        }

        void Note(string kind, string path, long bytes)
        {
            if (persistEach)
            {
                Commit(kind, path);
                return;
            }

            if (showProgress)
            {
                pending.Add((kind, path));
            }

            dirtyFiles++;
            dirtyBytes += bytes;
            if (dirtyFiles >= fileLimit || dirtyBytes >= byteLimit)
            {
                FlushChunk();
            }
        }

        void FlushChunk()
        {
            Commit(null, null);
            if (showProgress)
            {
                foreach (var (kind, path) in pending)
                {
                    WriteProgressLine(kind + ": " + path);
                }
            }

            pending.Clear();
            dirtyFiles = 0;
            dirtyBytes = 0;
            foreach (var document in documents.Values)
            {
                document.CachedLine = null;
            }
        }

        void Commit(string? kind, string? relativePath)
        {
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(createdUtc))
            {
                createdUtc = now;
            }

            var kept = new List<DocumentRecord>(documents.Count);
            foreach (var document in documents.Values)
            {
                kept.Add(document);
            }

            var manifest = new IndexManifest
            {
                FormatVersion = IndexPaths.FormatVersion,
                Tool = IndexPaths.ToolName,
                Root = root,
                CreatedUtc = createdUtc,
                UpdatedUtc = now,
                DocumentCount = kept.Count,
                TrigramCount = postings.Count,
                CaseFolding = "invariant-lowercase",
                ColumnUnit = "utf16-code-unit",
            };
            IndexStore.Write(indexPath, manifest, kept, postings);
            if (kind == null || relativePath == null)
            {
                return;
            }

            WriteOutcome(kind, relativePath);
            onPersisted?.Invoke(kind, relativePath);
        }

        void WriteOutcome(string verb, string relativePath)
        {
            if (showProgress)
            {
                WriteProgressLine(verb + ": " + relativePath);
            }
        }

        void WriteSummary()
        {
            var skipped = stats.SkippedBinary + stats.SkippedTooLarge + stats.SkippedUnreadable;
            WriteProgressLine(
                "indexed " + examined.ToString(CultureInfo.InvariantCulture)
                + "/" + files.Count.ToString(CultureInfo.InvariantCulture)
                + ", added " + added.ToString(CultureInfo.InvariantCulture)
                + ", updated " + updated.ToString(CultureInfo.InvariantCulture)
                + ", unchanged " + unchanged.ToString(CultureInfo.InvariantCulture)
                + ", removed " + removed.ToString(CultureInfo.InvariantCulture)
                + ", skipped " + skipped.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static int ResolveWorkers(int requested)
    {
        if (requested < 0)
        {
            throw new ArgumentException("Worker count must be a positive integer.");
        }

        if (requested > 0)
        {
            return requested;
        }

        var env = Environment.GetEnvironmentVariable("NGREP_THREADS");
        var name = "NGREP_THREADS";
        if (string.IsNullOrWhiteSpace(env))
        {
            env = Environment.GetEnvironmentVariable("NETGREP_THREADS");
            name = "NETGREP_THREADS";
        }

        if (!string.IsNullOrWhiteSpace(env))
        {
            if (!int.TryParse(env, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                throw new ArgumentException($"{name} must be a positive integer.");
            }

            return parsed;
        }

        return Math.Max(1, Environment.ProcessorCount);
    }

    private static void Feed(
        List<WalkedFile> files,
        DocumentRecord?[] previous,
        ConcurrentDictionary<ulong, string> intern,
        int workers,
        Action<int, Prep> apply)
    {
        if (files.Count == 0)
        {
            return;
        }

        workers = Math.Min(Math.Max(1, workers), files.Count);
        if (workers == 1)
        {
            var seen = new HashSet<ulong>();
            var buffer = new List<string>(64);
            for (var i = 0; i < files.Count; i++)
            {
                apply(i, Prepare(files[i], previous[i], intern, seen, buffer, CancellationToken.None));
            }

            return;
        }

        var window = workers * 4;
        using var jobs = new BlockingCollection<int>(window);
        var slots = new Prep[window];
        var ready = new SemaphoreSlim[window];
        var free = new SemaphoreSlim[window];
        for (var slot = 0; slot < window; slot++)
        {
            ready[slot] = new SemaphoreSlim(0, 1);
            free[slot] = new SemaphoreSlim(1, 1);
        }

        using var cancel = new CancellationTokenSource();
        var stop = 0;
        Exception? failure = null;
        var producer = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < files.Count; i++)
                {
                    free[i % window].Wait(cancel.Token);
                    if (Volatile.Read(ref stop) != 0)
                    {
                        break;
                    }

                    jobs.Add(i);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                try
                {
                    jobs.CompleteAdding();
                }
                catch (InvalidOperationException)
                {
                }
            }
        })
        {
            IsBackground = true,
            Name = "ngrep-index-produce",
        };

        var threads = new Thread[workers];
        for (var worker = 0; worker < workers; worker++)
        {
            threads[worker] = new Thread(() =>
            {
                var seen = new HashSet<ulong>();
                var buffer = new List<string>(64);
                try
                {
                    foreach (var index in jobs.GetConsumingEnumerable())
                    {
                        if (Volatile.Read(ref stop) != 0)
                        {
                            break;
                        }

                        var slot = index % window;
                        Prep prep;
                        try
                        {
                            prep = Prepare(files[index], previous[index], intern, seen, buffer, cancel.Token);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
                        {
                            Interlocked.CompareExchange(ref failure, ex, null);
                            prep = new Prep { Kind = PrepKind.Fault, Error = ex };
                            try
                            {
                                cancel.Cancel();
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            prep = new Prep { Kind = PrepKind.Fault, Error = failure };
                        }

                        slots[slot] = prep;
                        try
                        {
                            ready[slot].Release();
                        }
                        catch (SemaphoreFullException)
                        {
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                }
            })
            {
                IsBackground = true,
                Name = "ngrep-index-read",
            };
        }

        producer.Start();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var slot = i % window;
                ready[slot].Wait();
                var prep = slots[slot];
                slots[slot] = null!;
                if (prep == null || prep.Kind == PrepKind.Fault)
                {
                    throw Volatile.Read(ref failure)
                        ?? prep?.Error
                        ?? new IOException("Failed to read a file while indexing.");
                }

                apply(i, prep);
                free[slot].Release();
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                jobs.CompleteAdding();
            }
            catch (InvalidOperationException)
            {
            }

            for (var slot = 0; slot < window; slot++)
            {
                try
                {
                    free[slot].Release();
                }
                catch (SemaphoreFullException)
                {
                }

                try
                {
                    ready[slot].Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }

            producer.Join();
            foreach (var thread in threads)
            {
                thread.Join();
            }

            for (var slot = 0; slot < window; slot++)
            {
                ready[slot].Dispose();
                free[slot].Dispose();
            }
        }
    }

    private static Prep Prepare(
        WalkedFile file,
        DocumentRecord? previous,
        ConcurrentDictionary<ulong, string> intern,
        HashSet<ulong> seen,
        List<string> grams,
        CancellationToken cancellation) =>
        PrepareAsync(file, previous, intern, seen, grams, cancellation).AsTask().GetAwaiter().GetResult();

    private static async ValueTask<Prep> PrepareAsync(
        WalkedFile file,
        DocumentRecord? previous,
        ConcurrentDictionary<ulong, string> intern,
        HashSet<ulong> seen,
        List<string> grams,
        CancellationToken cancellation)
    {
        long length;
        long mtime;
        try
        {
            var info = new FileInfo(file.FullPath);
            length = info.Length;
            mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Prep.Unreadable;
        }

        if (length > TextFiles.MaxFileBytes)
        {
            return Prep.TooLarge;
        }

        if (previous != null && previous.Size == length && previous.MtimeUnixMs == mtime)
        {
            return Prep.UnchangedKind;
        }

        // Probe buffer is 8 KiB, so a NUL aborts before the rest of a binary is read.
        // Text below the map threshold is then read in full into a pooled buffer.
        var bytes = await TextFiles.ReadAsync(file.FullPath, stop: null, cancellation, TextFiles.BinaryProbeLength).ConfigureAwait(false);
        using (bytes)
        {
            switch (bytes.Kind)
            {
                case ReadKind.Binary:
                    return Prep.Binary;
                case ReadKind.TooLarge:
                    return Prep.TooLarge;
                case ReadKind.Unreadable:
                    return Prep.Unreadable;
            }

            var hash = TextFiles.Sha256Hex(bytes.Span);
            if (previous != null && previous.Sha256 == hash)
            {
                return new Prep
                {
                    Kind = PrepKind.HashMatch,
                    Size = bytes.Length,
                    Mtime = mtime,
                    Hash = hash,
                };
            }

            var text = TextFiles.Decode(bytes.Span);
            grams.Clear();
            Trigrams.Collect(text.ToLowerInvariant(), seen, grams, intern);
            var copy = grams.Count == 0 ? Array.Empty<string>() : grams.ToArray();
            grams.Clear();
            return new Prep
            {
                Kind = PrepKind.Index,
                Size = bytes.Length,
                Mtime = mtime,
                Hash = hash,
                Grams = copy,
            };
        }
    }

    private static void WriteProgressLine(string line)
    {
        Console.Error.WriteLine(line);
        Console.Error.Flush();
    }

    private static void RemoveDoc(Dictionary<string, List<int>> postings, int id)
    {
        var empty = new List<string>();
        foreach (var pair in postings)
        {
            pair.Value.Remove(id);
            if (pair.Value.Count == 0)
            {
                empty.Add(pair.Key);
            }
        }

        foreach (var key in empty)
        {
            postings.Remove(key);
        }
    }

    private static void AddDoc(Dictionary<string, List<int>> postings, string gram, int id)
    {
        if (!postings.TryGetValue(gram, out var list))
        {
            postings[gram] = [id];
            return;
        }

        var last = list.Count - 1;
        if (last < 0 || list[last] < id)
        {
            list.Add(id);
            return;
        }

        if (list[last] == id)
        {
            return;
        }

        var index = list.BinarySearch(id);
        if (index < 0)
        {
            list.Insert(~index, id);
        }
    }

    private enum PrepKind : byte
    {
        Fault,
        Unreadable,
        TooLarge,
        Binary,
        Unchanged,
        HashMatch,
        Index,
    }

    private sealed class Prep
    {
        public static readonly Prep Unreadable = new() { Kind = PrepKind.Unreadable };
        public static readonly Prep TooLarge = new() { Kind = PrepKind.TooLarge };
        public static readonly Prep Binary = new() { Kind = PrepKind.Binary };
        public static readonly Prep UnchangedKind = new() { Kind = PrepKind.Unchanged };

        public PrepKind Kind { get; init; }

        public long Size { get; init; }

        public long Mtime { get; init; }

        public string? Hash { get; init; }

        public string[]? Grams { get; init; }

        public Exception? Error { get; init; }
    }
}
