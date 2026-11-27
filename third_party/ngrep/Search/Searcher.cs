using System.Collections.Concurrent;
using System.Diagnostics;
using Ngrep.Ignore;
using Ngrep.Indexing;
using Ngrep.Text;

namespace Ngrep.Search;

internal readonly record struct ContextLine(int Line, string Text, int Offset = 0);

internal readonly record struct SearchMatch(
    string Path,
    int Line,
    int Column,
    string Text,
    string LineText,
    IReadOnlyList<ContextLine>? Before = null,
    IReadOnlyList<ContextLine>? After = null,
    int Offset = 0);

internal enum ReportMode
{
    Matches,
    FilesWithMatches,
    FilesWithoutMatch,
    Count,
    Quiet,
}

internal sealed class SearchQuery
{
    public required string Pattern { get; init; }

    public IReadOnlyList<string> AdditionalPatterns { get; init; } = [];

    public required string Path { get; init; }

    /// <summary>
    /// Path the user named, when several roots are searched. Result paths are printed under it
    /// so two roots that both contain <c>App.cs</c> stay distinct. Null keeps the single-root
    /// path (index root, or the directory that was scanned). <c>.</c> stays null.
    /// </summary>
    public string? DisplayRoot { get; init; }

    /// <summary>
    /// Read file contents from <see cref="StandardInput"/> (or process stdin) instead of <see cref="Path"/>.
    /// The index, ignore rules, globs, and types do not apply: the stream is the whole search.
    /// </summary>
    public bool SearchStdin { get; init; }

    /// <summary>Content bytes when <see cref="SearchStdin"/> is set. Null opens process stdin.</summary>
    public Stream? StandardInput { get; init; }

    public string? IndexDirectory { get; init; }

    public bool IgnoreCase { get; init; }

    /// <summary>
    /// Per-pattern case folding when smart-case patterns disagree.
    /// Null when every pattern uses <see cref="IgnoreCase"/>.
    /// </summary>
    public bool[]? PatternIgnoreCase { get; init; }

    public bool Regex { get; init; }

    public bool ForceScan { get; init; }

    public bool WordRegexp { get; init; }

    public bool InvertMatch { get; init; }

    /// <summary>Whole-line match (<c>-x</c>). The line text, with a trailing CR removed, must match.</summary>
    public bool LineRegexp { get; init; }

    /// <summary>Suppress missing-path and named-unreadable diagnostics (<c>-s</c>). Exit code stays 2.</summary>
    public bool NoMessages { get; init; }

    public bool NoIgnore { get; init; }

    public bool NoIgnoreVcs { get; init; }

    public bool IncludeHidden { get; init; }

    /// <summary><c>-L</c> / <c>--follow</c>. The index does not store followed links, so search scans.</summary>
    public bool FollowSymlinks { get; init; }

    /// <summary>
    /// Ripgrep <c>-d</c> / <c>--max-depth</c>. Null does not cap depth.
    /// 0 is the start path (a directory is not listed). 1 is its immediate children.
    /// </summary>
    public int? MaxDepth { get; init; }

    /// <summary><c>--binary</c> / the third <c>-u</c>. Search files with a NUL in the first 8 KiB and print matching lines.</summary>
    public bool SearchBinary { get; init; }

    /// <summary>
    /// <c>-a</c> / <c>--text</c>. Same read as <see cref="SearchBinary"/>: a NUL in the first 8 KiB
    /// does not skip the file, and matching lines are printed. Ripgrep's <c>--binary</c> prints
    /// <c>binary file matches</c> instead of the line; this is the ripgrep flag that prints the line.
    /// </summary>
    public bool SearchText { get; init; }

    public bool Multiline { get; init; }

    public bool MultilineDotAll { get; init; }

    /// <summary><c>-P</c> / <c>--pcre</c>. Backtracking interpreter instead of the non-backtracking engine.</summary>
    public bool Backtracking { get; init; }

    public ReportMode Report { get; init; }

    /// <summary>When set, <c>-l</c> does not stop at the first hit, so line stats are complete.</summary>
    public bool Stats { get; init; }

    /// <summary>
    /// Plain text prints one row per matching line. The search then keeps only the first hit
    /// on each line instead of materializing every hit for the writer to discard.
    /// </summary>
    public bool OneRowPerLine { get; init; }

    /// <summary>The writer can take line byte ranges instead of decoded line strings.</summary>
    public bool RawTextLines { get; init; }

    public int BeforeContext { get; init; }

    public int AfterContext { get; init; }

    /// <summary>
    /// Matching lines per file before that file is stopped. Null means no limit.
    /// 0 reports nothing and does not read file bytes.
    /// </summary>
    public int? MaxCount { get; init; }

    public IReadOnlyList<GlobSpec> Globs { get; init; } = [];

    /// <summary><c>--ignore-file</c> paths, in command-line order. Earlier files lose to later ones.</summary>
    public IReadOnlyList<string> IgnoreFiles { get; init; } = [];

    public IReadOnlyList<string> Types { get; init; } = [];

    public IReadOnlyList<string> TypeNots { get; init; } = [];

    public IReadOnlyList<string> TypeAdds { get; init; } = [];

    /// <summary>0 uses <c>NGREP_THREADS</c>, then <c>NETGREP_THREADS</c>, or <see cref="Environment.ProcessorCount"/>.</summary>
    public int MaxDegreeOfParallelism { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// False when the selected report never prints a line, so a match can skip the line
    /// text. Set by the CLI from the report and the output format.
    /// </summary>
    public bool NeedLineText { get; init; } = true;

    /// <summary>False when nothing reads the matched slice's text (plain text output, counts, lists).</summary>
    public bool NeedMatchText { get; init; } = true;

    /// <summary>
    /// When set, matches are handed off as each file is confirmed and are not retained.
    /// Index search emits in path order. A content scan emits as soon as the file is
    /// confirmed and does not wait for the rest of the walk.
    /// </summary>
    public SearchEvents? Events { get; init; }
}

internal sealed class SearchEvents
{
    public Action<string?>? Note { get; init; }

    public Action<SearchBatch>? Batch { get; init; }
}

internal readonly struct SearchBatch
{
    public required string Path { get; init; }

    public required List<SearchMatch> Matches { get; init; }

    public bool Examined { get; init; }

    public bool Matched { get; init; }

    public bool PrefilterMiss { get; init; }

    /// <summary>Row bodies already encoded as UTF-8. The writer prints these instead of decoded text.</summary>
    public byte[]? RawBody { get; init; }

    /// <summary>One entry per printed row, indexing <see cref="RawBody"/>.</summary>
    public List<RawLine>? RawRows { get; init; }

    /// <summary>Distinct matching lines, when the scan counted them without building records.</summary>
    public int Lines { get; init; }
}

/// <summary>
/// One printed row's body inside the per-file byte buffer. The search builds it on a worker;
/// the writer prints it on the consumer thread so output order is the same as before.
/// </summary>
internal readonly record struct RawLine(int Line, int Column, int Start, int Length);

internal sealed class SearchOutcome
{
    public required IReadOnlyList<SearchMatch> Matches { get; init; }

    public required bool UsedIndex { get; init; }

    public bool UsedTrigramPrefilter { get; init; }

    public string? Note { get; init; }

    /// <summary><c>parallel</c> when confirmation used more than one worker; otherwise <c>sequential</c>.</summary>
    public string Confirmation { get; init; } = "sequential";

    public int Workers { get; init; } = 1;

    public IReadOnlyList<string> FilesWithoutMatch { get; init; } = [];

    public int FilesSearched { get; init; }

    public int FilesMatched { get; init; }

    public int LinesMatched { get; init; }

    public int MatchCount { get; init; }

    public int WithoutCount { get; init; }

    /// <summary>True when matches were handed to <see cref="SearchEvents"/> and <see cref="SearchOutcome.Matches"/> is empty.</summary>
    public bool Streamed { get; init; }

    public long ElapsedMilliseconds { get; init; }
}

internal sealed class SearchTally
{
    private int _searched;
    private int _matched;

    public int FilesSearched => Volatile.Read(ref _searched);

    public int FilesMatched => Volatile.Read(ref _matched);

    public void File(bool matched)
    {
        Interlocked.Increment(ref _searched);
        if (matched)
        {
            Interlocked.Increment(ref _matched);
        }
    }
}

internal static class Searcher
{
    /// <summary>Test hook. Invoked with the relative path before an indexed or scanned file is read.</summary>
    internal static Action<string>? BeforeConfirm { get; set; }

    public static async Task<SearchOutcome> Search(SearchQuery query)
    {
        query.CancellationToken.ThrowIfCancellationRequested();
        if (query.MaxDepth is < 0)
        {
            throw new ArgumentException("--max-depth must be >= 0.");
        }

        if (query.SearchStdin)
        {
            return await SearchStandardInput(query);
        }

        var clock = Stopwatch.StartNew();
        var tally = new SearchTally();
        var plan = MatchPlan.Create(query);
        var workers = ResolveWorkers(query);
        var start = Path.GetFullPath(query.Path);
        if (!File.Exists(start) && !Directory.Exists(start))
        {
            throw new SearchPathException($"Path not found: {start}", query.NoMessages);
        }

        if (HiddenPaths.ContainsHardSkip(start))
        {
            return Finish([], usedIndex: false, note: null, workers: 1, usedPrefilter: false, without: null, tally, clock);
        }

        // ripgrep treats 0 as legal and searches nothing. The path is still checked above.
        if (query.MaxCount == 0)
        {
            return Finish([], usedIndex: false, note: null, workers: 1, usedPrefilter: false, without: null, tally, clock);
        }

        // Binary files are not in the index, so --binary and -a/--text have to read the tree.
        var skipIndex = query.ForceScan || !plan.ApplyGitIgnore || plan.SearchBinary || query.FollowSymlinks;
        if (!skipIndex && query.IndexDirectory != null)
        {
            if (ShortSubpath(query.IndexDirectory, start, plan))
            {
                return await ScanAndFinish(start, plan, workers, query, ShortSubpathNote, tally, clock);
            }

            var explicitIndex = LoadForSearch(query.IndexDirectory, plan)
                ?? throw new InvalidDataException($"Index not found at {Path.GetFullPath(query.IndexDirectory)}.");
            return await SearchIndex(explicitIndex, start, query, plan, workers, tally, clock);
        }

        if (!skipIndex)
        {
            var discovered = IndexPaths.Discover(start);
            if (discovered != null)
            {
                if (ShortSubpath(discovered, start, plan))
                {
                    return await ScanAndFinish(start, plan, workers, query, ShortSubpathNote, tally, clock);
                }

                var index = LoadForSearch(discovered, plan);
                if (index != null)
                {
                    return await SearchIndex(index, start, query, plan, workers, tally, clock);
                }
            }
        }

        var note = ScanNote(query, indexSkipped: skipIndex && !query.ForceScan && IndexWouldApply(start, query));
        return await ScanAndFinish(start, plan, workers, query, note, tally, clock);
    }

    /// <summary>
    /// One stream, no index, no scan note. Binary and oversize follow the file rules and are not errors.
    /// </summary>
    private static async Task<SearchOutcome> SearchStandardInput(SearchQuery query)
    {
        query.CancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var tally = new SearchTally();
        var plan = MatchPlan.Create(query);
        var stream = query.Events?.Batch != null ? new StreamCount(query.Events) : null;
        var without = stream == null && plan.Report == ReportMode.FilesWithoutMatch ? new ConcurrentBag<string>() : null;
        var matches = new List<SearchMatch>();
        var file = new ConfirmFile(StandardInput.DisplayPath, StandardInput.DisplayPath);
        var result = await TryCollectStreamAsync(file, plan, matches, without, tally, query);
        if (stream != null)
        {
            stream.AcceptFile(StandardInput.DisplayPath, matches, result);
            return FinishStream(usedIndex: false, note: null, workers: 1, usedPrefilter: false, stream, tally, clock);
        }

        return Finish(matches, usedIndex: false, note: null, workers: 1, usedPrefilter: false, without, tally, clock);
    }

    /// <summary>
    /// Trigrams cannot name this pattern, and the path sits strictly inside the indexed root.
    /// The document list is not a safe file set: a later file, or a directory the parent
    /// snapshot omitted, would be reported as a miss. Scan that path instead.
    /// </summary>
    private static bool ShortSubpath(string indexDirectory, string start, MatchPlan plan)
    {
        if (plan.CanUseTrigrams)
        {
            return false;
        }

        var root = IndexStore.TryReadRoot(indexDirectory);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return false;
        }

        var prefix = RelativeTo(root, start, out var outside, out _);
        return !outside && prefix.Length > 0;
    }

    private const string ShortSubpathNote = "pattern is not covered by trigrams; scanned content";

    private const string UnindexedPathNote = "path is not in the index; scanned content";

    private static async Task<SearchOutcome> ScanAndFinish(
        string start,
        MatchPlan plan,
        int workers,
        SearchQuery query,
        string? note,
        SearchTally tally,
        Stopwatch clock)
    {
        var scanned = await ContentScan(start, plan, workers, query.CancellationToken, tally, query, note);
        if (scanned.Stream != null)
        {
            return FinishStream(usedIndex: false, note, scanned.Workers, usedPrefilter: false, scanned.Stream, tally, clock);
        }

        return Finish(scanned.Matches, usedIndex: false, note, scanned.Workers, usedPrefilter: false, scanned.Without, tally, clock);
    }

    private static IndexData? LoadForSearch(string directory, MatchPlan plan)
    {
        HashSet<string>? grams = null;
        if (plan.CanUseTrigrams && plan.Literals != null)
        {
            grams = new HashSet<string>(StringComparer.Ordinal);
            if (!plan.Literals.TryCollectGrams(grams))
            {
                grams = null;
            }
        }

        return IndexStore.TryLoadForSearch(directory, grams);
    }

    private static int ResolveWorkers(SearchQuery query)
    {
        if (query.MaxDegreeOfParallelism < 0)
        {
            throw new ArgumentException("--threads must be a positive integer.");
        }

        if (query.MaxDegreeOfParallelism > 0)
        {
            return query.MaxDegreeOfParallelism;
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
            if (!int.TryParse(env, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                throw new ArgumentException($"{name} must be a positive integer.");
            }

            return parsed;
        }

        return Math.Max(1, Environment.ProcessorCount);
    }

    private static async Task<SearchOutcome> SearchIndex(IndexData index, string start, SearchQuery query, MatchPlan plan, int workers, SearchTally tally, Stopwatch clock)
    {
        var root = index.Manifest.Root;
        if (!Directory.Exists(root))
        {
            throw new SearchPathException($"Indexed root does not exist: {root}", query.NoMessages);
        }

        var prefix = RelativeTo(root, start, out var outside, out var prefixIsFile);
        if (outside)
        {
            const string note = "path is outside the indexed root; scanned content";
            return await ScanAndFinish(start, plan, workers, query, note, tally, clock);
        }

        if (prefix.Length > 0 && !plan.CanUseTrigrams)
        {
            return await ScanAndFinish(start, plan, workers, query, ShortSubpathNote, tally, clock);
        }

        HashSet<int>? selectedIds = null;
        var usedPrefilter = false;
        if (plan.CanUseTrigrams && plan.Literals!.TrySelect(index.Postings, out var ids))
        {
            usedPrefilter = true;
            selectedIds = ids;
        }

        // Rows the trigrams rejected are never materialized, so a selective query does not
        // parse the whole document table. Postings are already loaded at this point.
        // --files-without-match has to name those rejected files, so it reads every row.
        var onlyIds = plan.Report == ReportMode.FilesWithoutMatch ? null : selectedIds;
        var documents = index.DocumentsFile is { } documentsFile
            ? IndexStore.ReadSearchDocuments(documentsFile, onlyIds)
            : index.Documents;

        var stream = query.Events?.Batch != null ? new StreamCount(query.Events) : null;
        var files = new List<ConfirmFile>();
        var jobs = stream != null ? new List<EmitJob>() : null;
        var without = plan.Report == ReportMode.FilesWithoutMatch ? new ConcurrentBag<string>() : null;
        var covered = 0;
        string Shown(string documentPath)
        {
            if (query.DisplayRoot == null)
            {
                return documentPath;
            }

            if (prefixIsFile)
            {
                return DisplayPath(query.DisplayRoot, documentPath, rootIsFile: true);
            }

            return DisplayPath(query.DisplayRoot, PathUnderStart(documentPath, prefix), rootIsFile: false);
        }

        // --ignore-file only removes paths. The index was built with the higher sources,
        // so a whitelist there still keeps the file and a lower rule cannot put a skipped file back.
        var extra = plan.Ignore.HasExtra ? TreeWalker.LoadStack(root, plan.Ignore, plan.IncludeHidden) : null;
        foreach (var document in documents)
        {
            if (prefix.Length > 0 && !Under(document.Path, prefix, prefixIsFile))
            {
                continue;
            }

            if (!plan.IncludeHidden && HiddenPaths.IsHiddenBelow(document.Path, prefix))
            {
                continue;
            }

            if (!plan.Filter.Accepts(document.Path))
            {
                continue;
            }

            covered++;
            if (extra != null && extra.IsIgnored(TreeWalker.ScopePath(root, document.Path), false))
            {
                continue;
            }

            // The snapshot covers this path. Deeper documents stay unopened; they still
            // count as covered so a depth cap does not fall back to a content scan.
            if (DepthExceeds(document.Path, prefix, prefixIsFile, query.MaxDepth))
            {
                continue;
            }

            if (selectedIds != null && !selectedIds.Contains(document.Id))
            {
                // A required literal is absent, so the file cannot match and is not opened.
                if (stream != null)
                {
                    jobs!.Add(new EmitJob(false, new ConfirmFile("", Shown(document.Path))));
                }
                else if (without != null)
                {
                    without.Add(Shown(document.Path));
                }

                continue;
            }

            var full = Path.Combine(root, document.Path.Replace('/', Path.DirectorySeparatorChar));
            var file = new ConfirmFile(full, Shown(document.Path));
            if (stream != null)
            {
                jobs!.Add(new EmitJob(true, file));
            }
            else
            {
                // Existence is checked by the worker, in parallel with the reads, and only
                // until the first hit for -q. A serial stat here delayed every query.
                files.Add(file);
            }
        }

        if (prefix.Length > 0 && covered == 0)
        {
            // The snapshot has no row under this path. A walk can still see files
            // (added after index, or ignored only because the index root applied a
            // parent .gitignore that this search does not).
            return await ScanAndFinish(start, plan, workers, query, UnindexedPathNote, tally, clock);
        }

        var candidateCount = stream != null ? jobs!.Count : files.Count;
        var degree = candidateCount > 1 ? Math.Min(workers, candidateCount) : 1;
        var stopOnMatch = plan.Report == ReportMode.Quiet && plan.StopAtFirst;
        if (stream != null)
        {
            query.Events?.Note?.Invoke(null);
            await AsyncScheduler.EmitOrdered(
                jobs!,
                degree,
                query.CancellationToken,
                (file, local, token) => TryCollectAsync(file, plan, local, stats: null, filesWithoutMatch: null, tally, token, errorIfUnreadable: prefixIsFile, checkExists: true),
                (job, matches, result) => stream.Accept(job, matches, result),
                stopOnMatch);
            return FinishStream(usedIndex: true, note: null, degree, usedPrefilter, stream, tally, clock);
        }

        var matches = await AsyncScheduler.Run(
            files,
            degree,
            query.CancellationToken,
            (file, local, token) => TryCollectAsync(file, plan, local, stats: null, without, tally, token, errorIfUnreadable: prefixIsFile, checkExists: true),
            stopOnMatch);

        return Finish(matches, usedIndex: true, note: null, degree, usedPrefilter, without, tally, clock);
    }

    private static bool IndexWouldApply(string start, SearchQuery query)
    {
        if (query.IndexDirectory != null)
        {
            var full = Path.GetFullPath(query.IndexDirectory);
            return File.Exists(Path.Combine(full, "manifest.json"))
                || File.Exists(Path.Combine(full + ".bak", "manifest.json"));
        }

        return IndexPaths.Discover(start) != null;
    }

    private static string ScanNote(SearchQuery query, bool indexSkipped)
    {
        var parts = new List<string>(4);
        if (query.NoIgnore)
        {
            parts.Add("--no-ignore");
        }
        else if (query.NoIgnoreVcs)
        {
            parts.Add("--no-ignore-vcs");
        }

        if (query.SearchBinary)
        {
            parts.Add("--binary");
        }

        if (query.SearchText)
        {
            parts.Add("--text");
        }

        if (query.FollowSymlinks)
        {
            parts.Add("--follow");
        }

        string? detail = parts.Count == 0 ? null : string.Join(", ", parts);
        if (query.ForceScan && detail != null)
        {
            return $"content scan (--scan, {detail})";
        }

        if (query.ForceScan)
        {
            return "content scan (--scan)";
        }

        if (indexSkipped && detail != null)
        {
            return $"scanned content ({detail})";
        }

        if (detail != null)
        {
            return $"no index found; scanned content ({detail})";
        }

        return "no index found; scanned content";
    }

    private static async Task<ScanResult> ContentScan(
        string start,
        MatchPlan plan,
        int workers,
        CancellationToken cancellation,
        SearchTally tally,
        SearchQuery query,
        string? note)
    {
        var stream = query.Events?.Batch != null ? new StreamCount(query.Events) : null;
        var without = stream == null && plan.Report == ReportMode.FilesWithoutMatch ? new ConcurrentBag<string>() : null;
        if (File.Exists(start))
        {
            var parent = Path.GetDirectoryName(start) ?? start;
            var relative = Path.GetFileName(start);
            var matches = new List<SearchMatch>();
            // includeHidden so a path the user named is not treated as skipped merely for being a dotfile.
            if ((plan.Ignore == IgnorePolicy.None || !IsIgnoredFile(parent, start, plan.Ignore)) && plan.Filter.Accepts(relative))
            {
                var shown = DisplayPath(query.DisplayRoot, relative, rootIsFile: query.DisplayRoot != null);
                var result = await TryCollectAsync(new ConfirmFile(start, shown), plan, matches, stats: null, without, tally, cancellation, errorIfUnreadable: true, checkExists: false);
                query.Events?.Note?.Invoke(note);
                stream?.AcceptFile(shown, matches, result);
            }
            else
            {
                query.Events?.Note?.Invoke(note);
            }

            return new ScanResult(matches, 1, without, stream);
        }

        query.Events?.Note?.Invoke(note);

        var stats = new WalkStats();
        var degree = workers > 1 ? workers : 1;
        var stopOnMatch = plan.Report == ReportMode.Quiet && plan.StopAtFirst;
        ConfirmFile Show(ConfirmFile file) =>
            new(file.FullPath, DisplayPath(query.DisplayRoot, file.RelativePath, rootIsFile: false));
        if (stream != null && !stopOnMatch)
        {
            // Do not list or sort the tree before reading. Hits print as each file is
            // confirmed, while later directories are still being walked.
            await AsyncScheduler.EmitScan(
                start,
                IndexPaths.DefaultFor(start),
                stats,
                plan.Ignore,
                plan.IncludeHidden,
                query.MaxDepth,
                query.FollowSymlinks,
                plan.Filter,
                degree,
                cancellation,
                (file, local, token) => TryCollectAsync(Show(file), plan, local, stats, filesWithoutMatch: null, tally, token),
                (file, matches, result) => stream.Accept(new EmitJob(true, Show(file)), matches, result));
            return new ScanResult([], degree, null, stream);
        }

        var found = await AsyncScheduler.Scan(
            start,
            IndexPaths.DefaultFor(start),
            stats,
            plan.Ignore,
            plan.IncludeHidden,
            query.MaxDepth,
            query.FollowSymlinks,
            plan.Filter,
            degree,
            cancellation,
            (file, local, token) => TryCollectAsync(Show(file), plan, local, stats, without, tally, token),
            stopOnMatch);

        return new ScanResult(found, degree, without, stream);
    }

    private readonly struct ScanResult(List<SearchMatch> matches, int workers, ConcurrentBag<string>? without, StreamCount? stream)
    {
        public List<SearchMatch> Matches { get; } = matches;

        public int Workers { get; } = workers;

        public ConcurrentBag<string>? Without { get; } = without;

        public StreamCount? Stream { get; } = stream;
    }

    private static bool IsIgnoredFile(string walkRoot, string fullPath, IgnorePolicy policy) =>
        TreeWalker.IsIgnoredFile(walkRoot, fullPath, policy);

    private static async ValueTask<ConfirmResult> TryCollectStreamAsync(
        ConfirmFile file,
        MatchPlan plan,
        List<SearchMatch> matches,
        ConcurrentBag<string>? filesWithoutMatch,
        SearchTally tally,
        SearchQuery query)
    {
        BeforeConfirm?.Invoke(file.RelativePath);
        var stop = ByteConfirm.ArmFresh(plan);
        try
        {
            var input = query.StandardInput ?? StandardInput.Open();
            using var read = await TextFiles.ReadStreamAsync(input, stop, query.CancellationToken, plan.SearchBinary);
            ByteConfirm.Adopt(stop);
            if (MatchLoaded(read, file, plan, matches, stats: null, filesWithoutMatch, tally, errorIfUnreadable: true, raw: null, out var text, out var examined, out var matched))
            {
                return new ConfirmResult(examined, matched);
            }

            matched = CollectDecoded(text, file, plan, matches, filesWithoutMatch, tally, out var decodedLines);
            return new ConfirmResult(true, matched) { Lines = decodedLines };
        }
        finally
        {
            ByteConfirm.Disarm();
        }
    }

    private static ValueTask<ConfirmResult> TryCollectAsync(
        ConfirmFile file,
        MatchPlan plan,
        List<SearchMatch> matches,
        WalkStats? stats,
        ConcurrentBag<string>? filesWithoutMatch,
        SearchTally tally,
        CancellationToken cancellation,
        bool errorIfUnreadable = false,
        bool checkExists = false)
    {
        if (checkExists && !File.Exists(file.FullPath))
        {
            return new ValueTask<ConfirmResult>(ConfirmResult.Skip);
        }

        BeforeConfirm?.Invoke(file.RelativePath);
        var stop = ByteConfirm.ArmFresh(plan);
        try
        {
            using var read = TextFiles.Read(file.FullPath, stop, plan.SearchBinary, cancellation);
            ByteConfirm.Adopt(stop);
            var raw = plan.RawTextLines ? new RawSink() : null;
            if (MatchLoaded(read, file, plan, matches, stats, filesWithoutMatch, tally, errorIfUnreadable, raw, out var text, out var examined, out var matched))
            {
                var done = Done(plan, matches, examined, matched);
                if (raw?.Rows is { Count: > 0 } rows)
                {
                    raw.Complete();
                    done = done with { RawBody = raw.Body, RawRows = rows, Lines = rows.Count };
                }

                return new ValueTask<ConfirmResult>(done);
            }

            if (TryCollectRaw(text, read.Span, file, plan, matches, filesWithoutMatch, tally, out var rawBody, out var rawRows, out var rawMatched))
            {
                var result = Done(plan, matches, examined: true, rawMatched, raw: true);
                return new ValueTask<ConfirmResult>(result with { RawBody = rawBody, RawRows = rawRows, Lines = rawRows?.Count ?? 0 });
            }

            matched = CollectDecoded(text, file, plan, matches, filesWithoutMatch, tally, out var decodedLines);
            return new ValueTask<ConfirmResult>(Done(plan, matches, examined: true, matched) with { Lines = decodedLines });
        }
        finally
        {
            ByteConfirm.Disarm();
        }
    }

    /// <summary>
    /// Builds the printed rows for a plain text report from the file bytes, with no line
    /// strings. Runs on a worker, so it only fills a buffer; the consumer thread prints.
    /// </summary>
    private static bool TryCollectRaw(
        string text,
        ReadOnlySpan<byte> bytes,
        ConfirmFile file,
        MatchPlan plan,
        List<SearchMatch> matches,
        ConcurrentBag<string>? filesWithoutMatch,
        SearchTally tally,
        out byte[]? body,
        out List<RawLine>? rows,
        out bool matched)
    {
        body = null;
        rows = null;
        matched = false;
        if (!plan.RawTextLines || plan.Invert || plan.WantsContext || plan.CollectAll)
        {
            return false;
        }

        if (!System.Text.Ascii.IsValid(bytes))
        {
            return false;
        }

        var lines = new List<RawLine>();
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1 << 16);
        MatchPlan.CollectRawLines(text, bytes, plan, lines, buffer);
        tally.File(lines.Count > 0);
        if (lines.Count == 0)
        {
            filesWithoutMatch?.Add(file.RelativePath);
            return true;
        }

        body = buffer.WrittenSpan.ToArray();
        rows = lines;
        matched = true;
        return true;
    }

    /// <summary>
    /// Drops matching lines past <c>-m</c>. Callers stop their own scan; this keeps every
    /// path, including ones that already buffered the file, inside the same limit.
    /// </summary>
    private static ConfirmResult Done(MatchPlan plan, List<SearchMatch> matches, bool examined, bool matched, bool raw = false)
    {
        if (plan.MaxCount is int max && matches.Count > 0)
        {
            MatchLimits.Trim(matches, max);
            // Files-without-match can record a hit without storing lines. Only a list
            // that actually held matches is rewritten from what remains after the limit.
            matched = matches.Count > 0;
        }

        return new ConfirmResult(examined, matched, raw);
    }

    /// <summary>True when the file is finished. False when <paramref name="text"/> must be matched as a string.</summary>
    private static bool MatchLoaded(
        PooledBytes read,
        ConfirmFile file,
        MatchPlan plan,
        List<SearchMatch> matches,
        WalkStats? stats,
        ConcurrentBag<string>? filesWithoutMatch,
        SearchTally tally,
        bool errorIfUnreadable,
        RawSink? raw,
        out string text,
        out bool examined,
        out bool matched)
    {
        text = "";
        examined = false;
        matched = false;
        switch (read.Kind)
        {
            case ReadKind.TooLarge:
                stats?.IncrementSkippedTooLarge();
                return true;
            case ReadKind.Binary:
                stats?.IncrementSkippedBinary();
                return true;
            case ReadKind.Unreadable:
                stats?.IncrementSkippedUnreadable();
                if (errorIfUnreadable)
                {
                    throw new SearchPathException($"Unreadable: {file.FullPath}", plan.NoMessages);
                }

                return true;
        }

        // Plain text output can print the file bytes as they are, so try that before the
        // confirm that would decode every matching line into a SearchMatch.
        if (raw != null && ByteConfirm.TryHandleRaw(read.Span, plan, raw, out var rawMatched))
        {
            tally.File(rawMatched);
            if (!rawMatched)
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            examined = true;
            matched = rawMatched;
            return true;
        }

        raw?.Clear();

        if (ByteConfirm.TryHandle(read.Span, file.RelativePath, plan, matches, out var byteMatched))
        {
            tally.File(byteMatched);
            if (!byteMatched)
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            examined = true;
            matched = byteMatched;
            return true;
        }

        // The byte automaton already scans. A literal prefilter would read the file again.
        if (plan.BytePrograms == null && !plan.Invert && plan.Literals != null && plan.Literals.Misses(read.Span, plan.PrefilterIgnoreCase))
        {
            tally.File(matched: false);
            filesWithoutMatch?.Add(file.RelativePath);
            examined = true;
            return true;
        }

        // A Pike VM walks every byte, so narrow to candidate lines first. This is the
        // same literal filter the no-automaton path uses, and it leaves non-ASCII files
        // to the automaton below.
        if (plan.BytePrefilterFirst && MatchPlan.TryCollectBytes(read.Span, file.RelativePath, plan, matches, raw, out var narrowed))
        {
            tally.File(narrowed);
            if (!narrowed)
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            examined = true;
            matched = narrowed;
            return true;
        }

        raw?.Clear();

        if (MatchPlan.TryCollectAutomaton(read.Span, file.RelativePath, plan, matches, raw, out var automatonMatched))
        {
            tally.File(automatonMatched);
            if (!automatonMatched)
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            examined = true;
            matched = automatonMatched;
            return true;
        }

        raw?.Clear();

        if (MatchPlan.TryCollectBytes(read.Span, file.RelativePath, plan, matches, raw, out var prefiltered))
        {
            tally.File(prefiltered);
            if (!prefiltered)
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            examined = true;
            matched = prefiltered;
            return true;
        }

        text = TextFiles.Decode(read.Span);
        return false;
    }

    private static bool CollectDecoded(
        string text,
        ConfirmFile file,
        MatchPlan plan,
        List<SearchMatch> matches,
        ConcurrentBag<string>? filesWithoutMatch,
        SearchTally tally,
        out int lines)
    {
        lines = 0;
        if (plan.Report == ReportMode.FilesWithoutMatch && !plan.CollectAll)
        {
            var hit = MatchPlan.HasMatch(text, plan);
            tally.File(hit);
            if (!hit)
            {
                filesWithoutMatch?.Add(file.RelativePath);
            }

            lines = hit ? 1 : 0;
            return hit;
        }

        var before = matches.Count;
        lines = MatchPlan.Collect(text, file.RelativePath, plan, matches);
        var matched = lines > 0 || matches.Count > before;
        tally.File(matched);
        if (plan.Report == ReportMode.FilesWithoutMatch && !matched)
        {
            filesWithoutMatch?.Add(file.RelativePath);
        }

        return matched;
    }

    private static SearchOutcome Finish(
        List<SearchMatch> matches,
        bool usedIndex,
        string? note,
        int workers,
        bool usedPrefilter,
        ConcurrentBag<string>? without,
        SearchTally tally,
        Stopwatch clock)
    {
        SortIfNeeded(matches);
        var filesWithout = NormalizeWithout(without);
        return new SearchOutcome
        {
            Matches = matches,
            UsedIndex = usedIndex,
            Note = note,
            UsedTrigramPrefilter = usedPrefilter,
            Confirmation = workers > 1 ? "parallel" : "sequential",
            Workers = workers,
            FilesWithoutMatch = filesWithout,
            FilesSearched = tally.FilesSearched,
            FilesMatched = tally.FilesMatched,
            LinesMatched = CountMatchedLines(matches),
            MatchCount = matches.Count,
            WithoutCount = filesWithout.Length,
            ElapsedMilliseconds = clock.ElapsedMilliseconds,
        };
    }

    private static SearchOutcome FinishStream(
        bool usedIndex,
        string? note,
        int workers,
        bool usedPrefilter,
        StreamCount stream,
        SearchTally tally,
        Stopwatch clock)
    {
        return new SearchOutcome
        {
            Matches = [],
            UsedIndex = usedIndex,
            Note = note,
            UsedTrigramPrefilter = usedPrefilter,
            Confirmation = workers > 1 ? "parallel" : "sequential",
            Workers = workers,
            FilesWithoutMatch = [],
            FilesSearched = tally.FilesSearched,
            FilesMatched = tally.FilesMatched,
            LinesMatched = stream.Lines,
            MatchCount = stream.MatchCount,
            WithoutCount = stream.Without,
            Streamed = true,
            ElapsedMilliseconds = clock.ElapsedMilliseconds,
        };
    }

    private sealed class StreamCount
    {
        private readonly SearchEvents _events;

        public StreamCount(SearchEvents events) => _events = events;

        public int MatchCount { get; private set; }

        public int Lines { get; private set; }

        public int Without { get; private set; }

        public void AcceptFile(string path, List<SearchMatch> matches, ConfirmResult result) =>
            Accept(new EmitJob(true, new ConfirmFile("", path)), matches, result);

        public void Accept(EmitJob job, List<SearchMatch> matches, ConfirmResult result)
        {
            if (matches.Count > 1)
            {
                SortIfNeeded(matches);
            }

            var examined = job.Confirm && result.Examined;
            var matched = job.Confirm && result.Matched;
            if (!job.Confirm || (examined && !matched))
            {
                Without++;
            }

            var counted = result.Lines >= 0 ? result.Lines : CountMatchedLines(matches);
            MatchCount += matches.Count > 0 ? matches.Count : counted;
            Lines += counted;
            _events.Batch?.Invoke(new SearchBatch
            {
                Path = job.File.RelativePath,
                Matches = matches,
                Examined = examined,
                Matched = matched,
                PrefilterMiss = !job.Confirm,
                RawBody = result.RawBody,
                RawRows = result.RawRows,
                Lines = counted,
            });
        }
    }

    /// <summary>
    /// The byte and text confirm paths already emit hits in path, line, column order, which is
    /// the order <see cref="CompareMatches"/> defines. A sort of tens of thousands of matches
    /// then only re-checks an order that already holds, so scan first and sort only a disorder.
    /// </summary>
    private static void SortIfNeeded(List<SearchMatch> matches)
    {
        for (var i = 1; i < matches.Count; i++)
        {
            if (CompareMatches(matches[i - 1], matches[i]) > 0)
            {
                matches.Sort(CompareMatches);
                return;
            }
        }
    }

    private static int CountMatchedLines(List<SearchMatch> matches)
    {
        var count = 0;
        string? path = null;
        var line = 0;
        foreach (var match in matches)
        {
            if (!string.Equals(path, match.Path, StringComparison.Ordinal) || line != match.Line)
            {
                count++;
                path = match.Path;
                line = match.Line;
            }
        }

        return count;
    }

    private static string[] NormalizeWithout(ConcurrentBag<string>? without)
    {
        if (without == null || without.IsEmpty)
        {
            return [];
        }

        return without.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// When <paramref name="multiple"/> is set, result paths are printed under the path the user
    /// named. <c>.</c> keeps the single-root relative path so a search of the working directory
    /// does not gain a prefix.
    /// </summary>
    internal static string? MultiRootDisplay(string path, bool multiple)
    {
        if (!multiple)
        {
            return null;
        }

        var root = path.Replace('\\', '/');
        if (root.Length > 1)
        {
            root = root.TrimEnd('/');
        }

        if (root.Length == 0 || root == ".")
        {
            return null;
        }

        return path;
    }

    /// <summary>
    /// Prefix <paramref name="relative"/> (a path under the search root) with the path the user named.
    /// A null root leaves <paramref name="relative"/> unchanged. A file root is that path itself.
    /// </summary>
    private static string DisplayPath(string? displayRoot, string relative, bool rootIsFile)
    {
        if (displayRoot == null)
        {
            return relative;
        }

        var root = displayRoot.Replace('\\', '/');
        if (root.Length > 1)
        {
            root = root.TrimEnd('/');
        }

        if (rootIsFile)
        {
            return root;
        }

        relative = relative.Replace('\\', '/');
        if (relative.Length == 0)
        {
            return root;
        }

        return root + "/" + relative;
    }

    private static string PathUnderStart(string documentPath, string prefix)
    {
        if (prefix.Length == 0)
        {
            return documentPath;
        }

        if (documentPath.Equals(prefix, StringComparison.Ordinal))
        {
            return "";
        }

        var head = prefix + "/";
        if (documentPath.StartsWith(head, StringComparison.Ordinal))
        {
            return documentPath[head.Length..];
        }

        return documentPath;
    }

    private static string RelativeTo(string root, string start, out bool outside, out bool prefixIsFile)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        start = Path.GetFullPath(start);
        prefixIsFile = File.Exists(start);
        if (string.Equals(root, start.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
        {
            outside = false;
            return "";
        }

        var rel = Path.GetRelativePath(root, start).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            outside = true;
            return "";
        }

        outside = false;
        return rel == "." ? "" : rel;
    }

    /// <summary>
    /// True when <paramref name="docPath"/> is deeper than <paramref name="maxDepth"/>
    /// relative to the search path. An explicit file is depth 0.
    /// </summary>
    private static bool DepthExceeds(string docPath, string prefix, bool prefixIsFile, int? maxDepth)
    {
        if (maxDepth is not int limit)
        {
            return false;
        }

        var relative = prefixIsFile ? "" : RelativeDocument(docPath, prefix);
        return TreeWalker.DepthOf(relative) > limit;
    }

    private static string RelativeDocument(string docPath, string prefix)
    {
        if (prefix.Length == 0 || docPath.Equals(prefix, StringComparison.Ordinal))
        {
            return prefix.Length == 0 ? docPath : "";
        }

        return docPath[(prefix.Length + 1)..];
    }

    private static bool Under(string docPath, string prefix, bool prefixIsFile)
    {
        if (prefix.Length == 0)
        {
            return true;
        }

        if (prefixIsFile)
        {
            return docPath.Equals(prefix, StringComparison.Ordinal);
        }

        return docPath.Equals(prefix, StringComparison.Ordinal)
            || docPath.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    private static int CompareMatches(SearchMatch left, SearchMatch right)
    {
        var path = string.CompareOrdinal(left.Path, right.Path);
        if (path != 0)
        {
            return path;
        }

        var line = left.Line.CompareTo(right.Line);
        if (line != 0)
        {
            return line;
        }

        var column = left.Column.CompareTo(right.Column);
        return column != 0 ? column : left.Text.Length.CompareTo(right.Text.Length);
    }
}

/// <summary>
/// The search path is missing, or a file the user named cannot be read.
/// <see cref="Suppress"/> is <c>-s</c>: the message stays off stderr and the exit code is still 2.
/// </summary>
internal sealed class SearchPathException : Exception
{
    public SearchPathException(string message, bool suppress)
        : base(message)
    {
        Suppress = suppress;
    }

    public bool Suppress { get; }
}
