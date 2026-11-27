using System.Text;
using System.Text.RegularExpressions;
using Ngrep.Ignore;
using Ngrep.Text;

namespace Ngrep.Search;

internal sealed class MatchPlan
{
    public required string[] Patterns { get; init; }

    public Regex[]? Regexes { get; init; }

    public required bool Regex { get; init; }

    public required bool IgnoreCase { get; init; }

    /// <summary>Smart-case patterns disagree. Byte confirm stays off; the prefilter folds.</summary>
    public bool MixedCase { get; init; }

    /// <summary>Per-pattern folding when <see cref="MixedCase"/> is set.</summary>
    public bool[]? PatternIgnoreCase { get; init; }

    /// <summary>
    /// Case folding for literal prefilters. Mixed smart-case uses an insensitive
    /// scan, which is a superset, and confirmation applies each pattern's own case.
    /// </summary>
    public bool PrefilterIgnoreCase => IgnoreCase || MixedCase;

    public required bool Word { get; init; }

    public required bool Invert { get; init; }

    public ReportMode Report { get; init; }

    public bool Multiline { get; init; }

    public bool MultilineDotAll { get; init; }

    public bool IncludeHidden { get; init; }

    /// <summary>Search NUL files instead of skipping them. Set by <c>--binary</c>, <c>-a</c>/<c>--text</c>, and <c>-uuu</c>.</summary>
    public bool SearchBinary { get; init; }

    public required int Before { get; init; }

    public required int After { get; init; }

    /// <summary>
    /// Matching lines per file. Null is unlimited. 0 is rejected here; the searcher
    /// returns before any read when the query is 0.
    /// </summary>
    public int? MaxCount { get; init; }

    public required PathFilter Filter { get; init; }

    public LiteralQuery? Literals { get; init; }

    public IgnorePolicy Ignore { get; init; } = IgnorePolicy.All;

    /// <summary>
    /// True when every ignore source is on, so an index built with those rules still matches the walk.
    /// <c>--no-ignore</c> and <c>--no-ignore-vcs</c> are false and force a content scan.
    /// <c>--ignore-file</c> does not: those rules only remove files the index already has, and search filters them.
    /// </summary>
    public bool ApplyGitIgnore => Ignore.Vcs && Ignore.Custom;

    public bool WantsContext => Before > 0 || After > 0;

    public bool CollectAll { get; init; }

    /// <summary>
    /// Plain text output prints one row per matching line, so a later hit on a line that
    /// already has one is dropped during confirmation instead of in the writer.
    /// </summary>
    public bool OneRowPerLine { get; init; }

    /// <summary>The writer takes line byte ranges instead of decoded line strings.</summary>
    public bool RawTextLines { get; init; }

    /// <summary>
    /// A text <c>-c</c> report needs the number of matching lines, not the matches. The scan
    /// then counts lines instead of building a record for each.
    /// </summary>
    public bool CountLinesOnly { get; init; }

    public bool LineRegexp { get; init; }

    public bool NoMessages { get; init; }

    /// <summary>
    /// False when the report cannot print a line (count, files-with/without-match, quiet).
    /// <see cref="SearchMatch.LineText"/> is then empty and no line is decoded or copied.
    /// </summary>
    public bool NeedLineText { get; init; } = true;

    /// <summary>
    /// False when nothing reads <see cref="SearchMatch.Text"/>: plain text output without
    /// color, <c>-o</c>, or <c>-r</c>, and the count/list reports. Each hit then skips the
    /// string for the matched slice.
    /// </summary>
    public bool NeedMatchText { get; init; } = true;

    /// <summary>Default regex engine (also selected by <c>-E</c>). False when <c>-P</c> selects the backtracking interpreter.</summary>
    public bool NonBacktracking { get; init; }

    /// <summary>ASCII byte automata for patterns the linear compiler recognized. Null when any pattern opted out.</summary>
    public ByteProgram[]? BytePrograms { get; init; }

    /// <summary>
    /// True when a byte program exists but should be preceded by the required-literal
    /// line prefilter. Set when any program is a Pike VM, which walks every byte with a
    /// thread list and loses badly to a SIMD literal scan that narrows the candidate lines.
    /// </summary>
    public bool BytePrefilterFirst { get; init; }

    public bool StopAtFirst =>
        (Report == ReportMode.FilesWithMatches || Report == ReportMode.Quiet) && !CollectAll;

    /// <summary>Regex matches may include a newline. Set by <c>-U</c> or <c>--multiline-dotall</c>.</summary>
    public bool CrossesLines => Multiline || MultilineDotAll;

    public bool CanUseTrigrams => !Invert && Literals is { TrigramSafe: true };

    /// <summary>Regex literals are confirmed per line. Absolute anchors and cross-line matches stay on the full string.</summary>
    public bool UseLinePrefilter { get; init; }

    public static MatchPlan Create(SearchQuery query)
    {
        var patterns = Combine(query);
        var folds = FoldFlags(query, patterns);
        var anyFold = false;
        var allFold = folds.Length > 0;
        foreach (var fold in folds)
        {
            anyFold |= fold;
            allFold &= fold;
        }

        var mixed = anyFold && !allFold;
        var regex = query.Regex;
        if (regex && !anyFold && VerbatimPattern.TryAll(patterns, out var literals))
        {
            // A case-sensitive pattern with no metacharacters matches the same spans as a literal.
            // Ignore-case stays on the regex engine so Unicode case folding does not change.
            // Mixed smart-case is not converted: some patterns fold and some do not.
            patterns = literals;
            regex = false;
        }

        Regex[]? regexes = null;
        ByteProgram[]? bytePrograms = null;
        var nonBacktracking = false;
        if (regex)
        {
            // Compiled is not AOT-safe for a user pattern. NonBacktracking builds a linear
            // automaton in memory and does not emit IL. -P keeps the backtracking interpreter.
            var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
            if (!query.Backtracking)
            {
                options |= RegexOptions.NonBacktracking;
                nonBacktracking = true;
            }

            if (query.MultilineDotAll)
            {
                options |= RegexOptions.Singleline;
            }

            regexes = new Regex[patterns.Length];
            var programs = new ByteProgram[patterns.Length];
            var allBytes = !query.Backtracking && !query.LineRegexp;
            for (var i = 0; i < patterns.Length; i++)
            {
                var patternOptions = folds[i] ? options | RegexOptions.IgnoreCase : options;
                var source = query.LineRegexp ? AnchorWholeLine(patterns[i]) : patterns[i];
                try
                {
                    regexes[i] = new Regex(source, patternOptions, TimeSpan.FromSeconds(5));
                }
                catch (NotSupportedException ex) when (nonBacktracking)
                {
                    // UseSystemResourceKeys makes ex.Message a resource id, so the
                    // sentence below is the one the user reads. The inner exception
                    // still carries the runtime's reason (backreference, lookaround,
                    // atomic group, or the size limit).
                    throw new ArgumentException(
                        $"'{patterns[i]}' is not supported by the non-backtracking engine. Use -P/--pcre for backreferences, lookaround, and atomic groups.",
                        ex);
                }

                var byteOptions = new ByteAutomaton.Options(folds[i], query.MultilineDotAll);
                if (allBytes && ByteAutomaton.TryCompile(patterns[i], byteOptions, out var program))
                {
                    programs[i] = program;
                }
                else
                {
                    allBytes = false;
                }
            }

            if (allBytes)
            {
                bytePrograms = programs;
            }
        }

        var literalsQuery = LiteralQuery.FromPatterns(patterns, regex);

        if (query.BeforeContext < 0 || query.AfterContext < 0)
        {
            throw new ArgumentException("Context length must be >= 0.");
        }

        if (query.MaxCount is < 0)
        {
            throw new ArgumentException("-m/--max-count must be a non-negative integer.");
        }

        var crosses = query.Multiline || query.MultilineDotAll;
        var useLinePrefilter = regex && !crosses && literalsQuery is { HasLinePrefilter: true } && !HasAbsoluteAnchor(patterns);
        var bytePrefilterFirst = useLinePrefilter && bytePrograms != null
            && Array.Exists(bytePrograms, static program => program.PrefersLiteralPrefilter);
        return new MatchPlan
        {
            Patterns = patterns,
            Regexes = regexes,
            Regex = regex,
            IgnoreCase = allFold && anyFold,
            MixedCase = mixed,
            PatternIgnoreCase = mixed ? folds : null,
            Word = query.WordRegexp,
            Invert = query.InvertMatch,
            Before = query.BeforeContext,
            After = query.AfterContext,
            MaxCount = query.MaxCount,
            Filter = PathFilter.Create(query.Globs, query.Types, query.TypeNots, query.TypeAdds),
            Literals = literalsQuery,
            Ignore = (query.NoIgnore
                ? IgnorePolicy.None
                : query.NoIgnoreVcs ? IgnorePolicy.CustomOnly : IgnorePolicy.All).WithExtra(query.IgnoreFiles),
            Report = query.Report,
            Multiline = crosses,
            MultilineDotAll = query.MultilineDotAll,
            IncludeHidden = query.IncludeHidden,
            SearchBinary = query.SearchBinary || query.SearchText,
            CollectAll = query.Stats,
            OneRowPerLine = query.OneRowPerLine && !query.Stats,
            RawTextLines = query.RawTextLines && patterns.Length == 1 && patterns[0].Length > 0 && !query.LineRegexp,
            CountLinesOnly = query.Report == ReportMode.Count
                && !query.Stats
                && !query.InvertMatch
                && !query.NeedLineText
                && !query.NeedMatchText,
            LineRegexp = query.LineRegexp,
            NoMessages = query.NoMessages,
            NeedLineText = query.NeedLineText,
            NeedMatchText = query.NeedMatchText,
            NonBacktracking = nonBacktracking,
            BytePrograms = bytePrograms,
            BytePrefilterFirst = bytePrefilterFirst,
            UseLinePrefilter = useLinePrefilter,
        };
    }

    /// <summary>
    /// Whole-line regex. The caller passes one line with the trailing CR already removed,
    /// so <c>\A</c> and <c>\z</c> mean that line and not the file.
    /// </summary>
    private static string AnchorWholeLine(string pattern) => @"\A(?:" + pattern + @")\z";

    private static bool HasAbsoluteAnchor(string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            for (var i = 0; i < pattern.Length - 1; i++)
            {
                if (pattern[i] != '\\')
                {
                    continue;
                }

                var next = pattern[i + 1];
                if (next is 'A' or 'z' or 'Z' or 'G')
                {
                    return true;
                }

                i++;
            }
        }

        return false;
    }

    public static bool HasMatch(string text, MatchPlan plan)
    {
        if (!plan.Invert)
        {
            return FirstHit(text, plan) != null;
        }

        var starts = LineMap.Build(text);
        var lineCount = LineMap.LineCount(text, starts);
        if (lineCount == 0)
        {
            return false;
        }

        var matchingLines = new HashSet<int>();
        MarkMatchingLines(text, starts, plan, matchingLines);
        for (var line = 1; line <= lineCount; line++)
        {
            if (!matchingLines.Contains(line))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs a linear byte automaton on an ASCII file. False when the pattern was not compiled,
    /// the file is not ASCII, or the match needs the string engine (context, <c>-x</c>, <c>-w</c>, <c>-v</c>).
    /// </summary>
    public static bool TryCollectAutomaton(
        ReadOnlySpan<byte> bytes,
        string relativePath,
        MatchPlan plan,
        List<SearchMatch> matches,
        RawSink? raw,
        out bool matched)
    {
        matched = false;
        if (plan.BytePrograms == null || plan.Invert || plan.LineRegexp || plan.WantsContext || plan.Word)
        {
            return false;
        }

        bytes = bytes[TextFiles.Utf8BomLength(bytes)..];

        // The VM cannot produce spans cheaply, so the decoded engine confirms the file. The
        // DFA in front of it still settles a miss without a decode, but only ASCII bytes are
        // safe for that: pattern classes are byte classes, so a multi-byte character would
        // look like several unrelated bytes.
        var defer = false;
        foreach (var program in plan.BytePrograms)
        {
            if (program.PrefersDecoded)
            {
                defer = true;
                break;
            }
        }

        if (defer && System.Text.Unicode.Utf8.IsValid(bytes))
        {
            if (Ascii.IsValid(bytes))
            {
                var everyProgramFilters = true;
                var anyMaybe = false;
                var lineSafe = true;
                foreach (var program in plan.BytePrograms)
                {
                    if (program is DfaProgram filter)
                    {
                        anyMaybe |= filter.MayMatch(bytes);
                        lineSafe &= filter.LineSafe;
                    }
                    else
                    {
                        everyProgramFilters = false;
                    }
                }

                if (everyProgramFilters && !anyMaybe)
                {
                    // Every pattern rejected the bytes, so the file has no match at all.
                    return true;
                }

                // The filter is exact, so a file-level hit only needs the lines it accepted
                // confirmed. A pattern that reads \A or \z still needs the whole text.
                if (everyProgramFilters && lineSafe && !plan.CrossesLines && !plan.CountLinesOnly
                    && TryCollectDfaLines(bytes, relativePath, plan, matches, raw, out matched))
                {
                    return true;
                }

                return false;
            }

            return false;
        }

        var countOnly = plan.Report == ReportMode.FilesWithoutMatch && !plan.CollectAll;
        var hits = countOnly ? null : new List<Hit>();
        foreach (var program in plan.BytePrograms)
        {
            var found = new List<ByteMatch>();
            program.Collect(bytes, plan.CrossesLines, plan.StopAtFirst || countOnly, found);
            foreach (var candidate in found)
            {
                if (countOnly)
                {
                    // An ASCII pattern's bytes are the same in UTF-8, so a hit exists
                    // whether or not a later byte is non-ASCII.
                    matched = true;
                    return true;
                }

                var value = plan.NeedMatchText
                    ? Encoding.ASCII.GetString(bytes.Slice(candidate.Index, candidate.Length))
                    : "";
                hits!.Add(new Hit(candidate.Index, candidate.Length, value));
                if (plan.StopAtFirst)
                {
                    break;
                }
            }

            if (plan.StopAtFirst && hits is { Count: > 0 })
            {
                break;
            }
        }

        if (countOnly)
        {
            return true;
        }

        if (hits!.Count == 0)
        {
            matched = false;
            return true;
        }

        // An ASCII pattern matches the same bytes in UTF-8 and 0x0A never appears inside
        // a multi-byte sequence, so the line and the hit are real. Only the column needs
        // characters instead of bytes, and only on the lines that hold non-ASCII text;
        // each hit line is checked on its own instead of the whole file.
        var before = matches.Count;
        if (!EmitByteHits(bytes, relativePath, hits!, matches, plan.MaxCount, raw))
        {
            matches.RemoveRange(before, matches.Count - before);
            return false;
        }

        matched = true;
        return true;
    }

    /// <summary>
    /// Confirms the lines the DFA accepted, one decode per line, instead of decoding the
    /// whole file. Exact for a pattern with no <c>\A</c> or <c>\z</c>, where a line slice
    /// sees the same text the whole-file engine would.
    /// </summary>
    private static bool TryCollectDfaLines(
        ReadOnlySpan<byte> bytes,
        string relativePath,
        MatchPlan plan,
        List<SearchMatch> matches,
        RawSink? raw,
        out bool matched)
    {
        matched = false;
        if (plan.Regexes == null || plan.BytePrograms == null)
        {
            return false;
        }

        var existenceOnly = plan.Report == ReportMode.FilesWithoutMatch && !plan.CollectAll;
        var limit = plan.StopAtFirst || existenceOnly ? 1 : plan.MaxCount ?? int.MaxValue;
        var starts = new List<int>();
        var found = new List<int>();
        foreach (var program in plan.BytePrograms)
        {
            found.Clear();
            ((DfaProgram)program).CollectMatchLines(bytes, found, limit);
            MergeSorted(starts, found);
            if (starts.Count >= limit)
            {
                break;
            }
        }

        if (starts.Count == 0)
        {
            return false;
        }

        var before = matches.Count;
        var hits = new List<Hit>(8);
        var budget = new LineBudget(plan.MaxCount);
        var lineNo = 1;
        var cursor = 0;
        foreach (var lineStart in starts)
        {
            lineNo += bytes[cursor..lineStart].Count((byte)'\n');
            cursor = lineStart;
            if (!budget.Allow(lineNo))
            {
                break;
            }

            var relative = bytes[lineStart..].IndexOf((byte)'\n');
            var end = relative < 0 ? bytes.Length : lineStart + relative;
            var textEnd = end;
            if (textEnd > lineStart && bytes[textEnd - 1] == (byte)'\r')
            {
                textEnd--;
            }

            // Match the line as the engine would in the whole text: the CR before the
            // LF is part of the search text, but not of the printed line.
            var slice = Encoding.ASCII.GetString(bytes[lineStart..end]);
            var shown = Encoding.ASCII.GetString(bytes[lineStart..textEnd]);
            hits.Clear();
            foreach (var regex in plan.Regexes)
            {
                foreach (var match in regex.EnumerateMatches(slice))
                {
                    if (plan.Word && !WordBoundary.At(slice, match.Index, match.Length))
                    {
                        continue;
                    }

                    hits.Add(new Hit(match.Index, match.Length, ""));
                }
            }

            if (hits.Count == 0)
            {
                // The pattern reaches this line only across a newline, which a slice
                // cannot confirm. Let the decoded engine decide.
                matches.RemoveRange(before, matches.Count - before);
                return false;
            }

            matched = true;
            if (existenceOnly)
            {
                return true;
            }

            if (raw != null)
            {
                var first = hits[0];
                for (var i = 1; i < hits.Count; i++)
                {
                    if (hits[i].Index < first.Index)
                    {
                        first = hits[i];
                    }
                }

                raw.Add(bytes[lineStart..textEnd], lineNo, first.Index + 1);
            }
            else
            {
                EmitLineHits(slice, lineStart, lineNo, relativePath, plan, hits, matches, shown);
            }

            if (plan.StopAtFirst)
            {
                return true;
            }
        }

        return true;
    }

    private static void MergeSorted(List<int> target, List<int> source)
    {
        if (source.Count == 0)
        {
            return;
        }

        if (target.Count == 0)
        {
            target.AddRange(source);
            return;
        }

        var merged = new List<int>(target.Count + source.Count);
        var left = 0;
        var right = 0;
        var last = -1;
        while (left < target.Count || right < source.Count)
        {
            var value = right >= source.Count || (left < target.Count && target[left] <= source[right])
                ? target[left++]
                : source[right++];
            if (value != last)
            {
                merged.Add(value);
                last = value;
            }
        }

        target.Clear();
        target.AddRange(merged);
    }

    /// <summary>
    /// Confirms a line-prefiltered regex on ASCII bytes. Only candidate lines are decoded.
    /// False when the file is not ASCII, context is required, or the literal filter cannot run.
    /// </summary>
    public static bool TryCollectBytes(
        ReadOnlySpan<byte> bytes,
        string relativePath,
        MatchPlan plan,
        List<SearchMatch> matches,
        RawSink? raw,
        out bool matched)
    {
        matched = false;
        if (!plan.UseLinePrefilter || plan.Invert || plan.WantsContext || plan.Literals == null || plan.Regexes == null)
        {
            return false;
        }

        if (!Ascii.IsValid(bytes))
        {
            return false;
        }

        var lineStarts = new List<int>();
        if (!plan.Literals.CandidateLineStarts(bytes, plan.PrefilterIgnoreCase, lineStarts))
        {
            return false;
        }

        var countOnly = plan.Report == ReportMode.FilesWithoutMatch && !plan.CollectAll;
        // -c in text output needs the matching line count, not the matches. IsMatch skips the
        // span work that EnumerateMatches does (finding the start after the end), which is the
        // largest cost of a match-dense regex. Word boundaries need the span, so that stays out.
        var countLinesOnly = plan.Report == ReportMode.Count
            && !plan.CollectAll
            && !plan.Word
            && !plan.NeedLineText
            && !plan.NeedMatchText;
        var lineHits = new List<Hit>(8);
        var lineNo = 1;
        var previousEnd = -1;
        var linesEmitted = 0;
        var limit = plan.MaxCount ?? int.MaxValue;
        foreach (var lineStart in lineStarts)
        {
            // IndexOf scans the line vectorized; a byte loop costs the whole line.
            var relative = bytes[lineStart..].IndexOf((byte)'\n');
            var end = relative < 0 ? bytes.Length : lineStart + relative;

            // Candidate lines are usually adjacent, so the line number is one more.
            // A gap only happens when a line without a required literal is skipped.
            if (previousEnd >= 0 && lineStart == previousEnd + 1)
            {
                lineNo++;
            }
            else
            {
                // Count from the previous line's own newline, so a gap of N skipped
                // lines adds N + 1.
                lineNo += bytes[(previousEnd < 0 ? 0 : previousEnd)..lineStart].Count((byte)'\n');
            }

            previousEnd = end;
            var textEnd = end;
            if (textEnd > lineStart && bytes[textEnd - 1] == (byte)'\r')
            {
                textEnd--;
            }

            // The file is ASCII, so this one decode serves both the regex and the printed line.
            var slice = Encoding.ASCII.GetString(bytes[lineStart..textEnd]);
            if (countLinesOnly)
            {
                var hitsLine = false;
                for (var i = 0; i < plan.Regexes.Length; i++)
                {
                    if (plan.Patterns[i].Length != 0 && plan.Regexes[i].IsMatch(slice))
                    {
                        hitsLine = true;
                        break;
                    }
                }

                if (!hitsLine)
                {
                    continue;
                }

                matched = true;
                if (linesEmitted >= limit)
                {
                    break;
                }

                linesEmitted++;
                // One record per matching line; -c prints only Line, so the texts stay empty.
                matches.Add(new SearchMatch(relativePath, lineNo, 1, "", "", null, null, lineStart));
                continue;
            }

            lineHits.Clear();
            for (var i = 0; i < plan.Regexes.Length; i++)
            {
                if (plan.Patterns[i].Length == 0)
                {
                    continue;
                }

                foreach (var match in plan.Regexes[i].EnumerateMatches(slice))
                {
                    if (plan.Word && !WordBoundary.At(slice, match.Index, match.Length))
                    {
                        continue;
                    }

                    lineHits.Add(new Hit(match.Index, match.Length, ""));
                }
            }

            if (lineHits.Count == 0)
            {
                continue;
            }

            matched = true;
            if (countOnly)
            {
                return true;
            }

            if (linesEmitted >= limit)
            {
                break;
            }

            linesEmitted++;
            if (raw != null)
            {
                var first = lineHits[0];
                for (var h = 1; h < lineHits.Count; h++)
                {
                    if (lineHits[h].Index < first.Index)
                    {
                        first = lineHits[h];
                    }
                }

                raw.Add(bytes[lineStart..textEnd], lineNo, first.Index + 1);
            }
            else
            {
                EmitLineHits(slice, lineStart, lineNo, relativePath, plan, lineHits, matches);
            }

            if (plan.StopAtFirst)
            {
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// Appends one candidate line's hits in (index, length) order, dropping the duplicates
    /// a second matching pattern produces. The line's byte start and number are already
    /// known, so no hit is re-located and the decoded line is reused for the text.
    /// </summary>
    private static void EmitLineHits(
        string slice,
        int lineStart,
        int lineNo,
        string relativePath,
        MatchPlan plan,
        List<Hit> hits,
        List<SearchMatch> matches,
        string? lineText = null)
    {
        if (hits.Count > 1)
        {
            hits.Sort(static (left, right) =>
            {
                var index = left.Index.CompareTo(right.Index);
                return index != 0 ? index : left.Length.CompareTo(right.Length);
            });
        }

        var shown = plan.NeedLineText ? (lineText ?? slice) : "";
        var lastIndex = -1;
        var lastLength = -1;
        foreach (var hit in hits)
        {
            if (hit.Index == lastIndex && hit.Length == lastLength)
            {
                continue;
            }

            lastIndex = hit.Index;
            lastLength = hit.Length;
            var value = plan.NeedMatchText ? slice.Substring(hit.Index, hit.Length) : "";
            matches.Add(new SearchMatch(
                relativePath,
                lineNo,
                hit.Index + 1,
                value,
                shown,
                null,
                null,
                lineStart + hit.Index));
            if (plan.StopAtFirst || plan.OneRowPerLine)
            {
                // One row per line: later hits on this line would be dropped by the writer.
                return;
            }
        }
    }

    private static bool EmitByteHits(ReadOnlySpan<byte> bytes, string relativePath, List<Hit> hits, List<SearchMatch> matches, int? maxLines, RawSink? raw)
    {
        hits.Sort(static (left, right) =>
        {
            var index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Length.CompareTo(right.Length);
        });
        var lineNo = 1;
        var cursor = 0;
        var sharedStart = -1;
        var sharedAscii = true;
        var sharedText = "";
        var budget = new LineBudget(maxLines);
        foreach (var hit in Dedup(hits))
        {
            // LastIndexOf and IndexOf scan vectorized; a byte loop costs a minified line.
            var newline = bytes[..hit.Index].LastIndexOf((byte)'\n');
            var lineStart = newline < 0 ? 0 : newline + 1;
            lineNo += bytes[cursor..lineStart].Count((byte)'\n');
            cursor = lineStart;
            if (!budget.Allow(lineNo))
            {
                break;
            }

            var rel = bytes[lineStart..].IndexOf((byte)'\n');
            var end = rel < 0 ? bytes.Length : lineStart + rel;
            var textEnd = end;
            if (textEnd > lineStart && bytes[textEnd - 1] == (byte)'\r')
            {
                textEnd--;
            }

            var lineSpan = bytes[lineStart..Math.Max(lineStart, textEnd)];
            if (lineStart != sharedStart)
            {
                var lineAscii = Ascii.IsValid(lineSpan);
                if (!lineAscii && !System.Text.Unicode.Utf8.IsValid(lineSpan))
                {
                    return false;
                }

                sharedStart = lineStart;
                if (raw != null)
                {
                    raw.Add(lineSpan, lineNo, lineAscii ? hit.Index - lineStart + 1 : TextFiles.Column(bytes, lineStart, hit.Index));
                }
                else
                {
                    sharedAscii = lineAscii;
                    sharedText = Encoding.UTF8.GetString(lineSpan);
                }
            }

            // One row per line: the writer would drop later hits on the same line.
            if (raw != null)
            {
                continue;
            }

            var column = sharedAscii ? hit.Index - lineStart + 1 : TextFiles.Column(bytes, lineStart, hit.Index);
            matches.Add(new SearchMatch(
                relativePath,
                lineNo,
                column,
                hit.Value,
                sharedText,
                null,
                null,
                hit.Index));
        }

        return true;
    }

    public static int Collect(string text, string relativePath, MatchPlan plan, List<SearchMatch> matches)
    {
        var starts = LineMap.Build(text);
        var lineCount = LineMap.LineCount(text, starts);
        if (plan.Invert)
        {
            var matchingLines = new HashSet<int>();
            MarkMatchingLines(text, starts, plan, matchingLines);
            for (var line = 1; line <= lineCount; line++)
            {
                if (matchingLines.Contains(line))
                {
                    continue;
                }

                var lineText = plan.NeedLineText || plan.NeedMatchText ? LineMap.LineText(text, starts, line) : "";
                var (before, after) = Context(text, starts, lineCount, line, plan);
                var offset = line - 1 < starts.Length ? starts[line - 1] : 0;
                matches.Add(new SearchMatch(
                    relativePath,
                    line,
                    1,
                    plan.NeedMatchText ? lineText : "",
                    plan.NeedLineText ? lineText : "",
                    before,
                    after,
                    offset));
                if (plan.StopAtFirst || (plan.MaxCount is int limit && matches.Count >= limit))
                {
                    return matches.Count;
                }
            }

            return matches.Count;
        }

        if (plan.LineRegexp)
        {
            CollectLines(text, starts, lineCount, relativePath, plan, matches);
            return matches.Count;
        }

        if (plan.StopAtFirst)
        {
            if (FirstHit(text, plan) is { } first)
            {
                var firstLine = default(SharedLine);
                AddHit(text, starts, lineCount, relativePath, plan, first, matches, ref firstLine);
            }

            return matches.Count;
        }

        if (plan.UseLinePrefilter)
        {
            CollectPrefiltered(text, starts, lineCount, relativePath, plan, matches);
            // This path does not track distinct lines, so the caller recounts.
            return -1;
        }

        var hits = new List<Hit>();
        // One pattern can stop at the Nth line. Several patterns still have to be merged
        // so an earlier hit from a later pattern is not dropped.
        var lineLimit = plan.Patterns.Length == 1 ? plan.MaxCount ?? 0 : 0;
        if (plan.Regex)
        {
            for (var i = 0; i < plan.Regexes!.Length; i++)
            {
                if (plan.Patterns[i].Length == 0)
                {
                    CollectEmpty(text, plan.Word, hits, lineLimit);
                    continue;
                }

                var line = 1;
                var cursor = 0;
                var kept = 0;
                var lastLine = 0;
                foreach (var match in plan.Regexes[i].EnumerateMatches(text))
                {
                    if (!plan.CrossesLines && ContainsLineBreak(text, match.Index, match.Length))
                    {
                        continue;
                    }

                    if (plan.Word && !WordBoundary.At(text, match.Index, match.Length))
                    {
                        continue;
                    }

                    if (lineLimit > 0 && !AcceptLimitedLine(text, match.Index, lineLimit, ref line, ref cursor, ref kept, ref lastLine))
                    {
                        break;
                    }

                    hits.Add(new Hit(
                        match.Index,
                        match.Length,
                        plan.NeedMatchText ? text.Substring(match.Index, match.Length) : ""));
                }
            }
        }
        else
        {
            for (var i = 0; i < plan.Patterns.Length; i++)
            {
                var pattern = plan.Patterns[i];
                if (pattern.Length == 0)
                {
                    CollectEmpty(text, plan.Word, hits, lineLimit);
                    continue;
                }

                CollectLiteral(text, pattern, ComparisonAt(plan, i), plan.Word, hits, lineLimit, plan.NeedMatchText);
            }
        }

        // A single pattern enumerates in ascending order and never repeats a span, so the
        // sort and the dedup only re-check an order that already holds.
        if (plan.Patterns.Length > 1)
        {
            hits.Sort(static (left, right) =>
            {
                var index = left.Index.CompareTo(right.Index);
                return index != 0 ? index : left.Length.CompareTo(right.Length);
            });
            hits = Dedup(hits);
        }

        var cache = default(SharedLine);
        var budget = new LineBudget(plan.MaxCount);
        var lineCursor = 0;
        var lines = 0;
        var lastCountedLine = 0;
        foreach (var hit in hits)
        {
            // Hits ascend, so the line only moves forward: walk the line starts instead of
            // binary searching them twice per hit.
            while (lineCursor + 1 < starts.Length && starts[lineCursor + 1] <= hit.Index)
            {
                lineCursor++;
            }

            var line = lineCursor + 1;
            if (!budget.Allow(line))
            {
                break;
            }

            if (plan.CountLinesOnly)
            {
                // -c prints a line count, so a repeat hit on a line adds nothing.
                if (line != lastCountedLine)
                {
                    lastCountedLine = line;
                    lines++;
                }

                continue;
            }

            if (line != lastCountedLine)
            {
                lastCountedLine = line;
                lines++;
            }

            AddHit(text, starts, lineCount, relativePath, plan, hit, matches, ref cache);
        }

        return lines;
    }

    private static void AddHit(
        string text,
        int[] starts,
        int lineCount,
        string relativePath,
        MatchPlan plan,
        Hit hit,
        List<SearchMatch> matches,
        ref SharedLine cache)
    {
        var (line, column) = LineMap.Map(starts, hit.Index);
        if (!cache.Ready || cache.Line != line)
        {
            cache.Ready = true;
            cache.Line = line;
            cache.Text = plan.NeedLineText ? LineMap.LineText(text, starts, line) : "";
            (cache.Before, cache.After) = Context(text, starts, lineCount, line, plan);
        }
        else if (plan.OneRowPerLine)
        {
            // Plain text prints one row per line, so a later hit on this line adds nothing.
            return;
        }

        matches.Add(new SearchMatch(
            relativePath,
            line,
            column,
            hit.Value,
            cache.Text!,
            cache.Before,
            cache.After,
            hit.Index));
    }

    /// <summary>One decoded line, reused by every hit on that line.</summary>
    private struct SharedLine
    {
        public bool Ready;
        public int Line;
        public string? Text;
        public IReadOnlyList<ContextLine>? Before;
        public IReadOnlyList<ContextLine>? After;
    }

    private static void CollectLines(
        string text,
        int[] starts,
        int lineCount,
        string relativePath,
        MatchPlan plan,
        List<SearchMatch> matches)
    {
        var cache = default(SharedLine);
        for (var line = 1; line <= lineCount; line++)
        {
            if (!WholeLineHit(text, starts, line, plan, out var hit))
            {
                continue;
            }

            AddHit(text, starts, lineCount, relativePath, plan, hit, matches, ref cache);
            if (plan.StopAtFirst || (plan.MaxCount is int limit && matches.Count >= limit))
            {
                return;
            }
        }
    }

    private static Hit? FirstWholeLine(string text, MatchPlan plan)
    {
        var starts = LineMap.Build(text);
        var lineCount = LineMap.LineCount(text, starts);
        for (var line = 1; line <= lineCount; line++)
        {
            if (WholeLineHit(text, starts, line, plan, out var hit))
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// A hit whose span is the line text. <see cref="LineMap.LineText"/> already drops a trailing CR.
    /// </summary>
    private static bool WholeLineHit(string text, int[] starts, int line, MatchPlan plan, out Hit hit)
    {
        var lineText = LineMap.LineText(text, starts, line);
        var start = line - 1 < starts.Length ? starts[line - 1] : 0;
        hit = default;
        if (!LineMatches(lineText, plan))
        {
            return false;
        }

        if (plan.Word && !WordBoundary.At(text, start, lineText.Length))
        {
            return false;
        }

        hit = new Hit(start, lineText.Length, lineText);
        return true;
    }

    private static bool LineMatches(string lineText, MatchPlan plan)
    {
        if (plan.Regex)
        {
            foreach (var regex in plan.Regexes!)
            {
                if (regex.IsMatch(lineText))
                {
                    return true;
                }
            }

            return false;
        }

        for (var i = 0; i < plan.Patterns.Length; i++)
        {
            if (lineText.Equals(plan.Patterns[i], ComparisonAt(plan, i)))
            {
                return true;
            }
        }

        return false;
    }

    private static Hit? FirstHit(string text, MatchPlan plan)
    {
        if (plan.LineRegexp)
        {
            return FirstWholeLine(text, plan);
        }

        if (plan.UseLinePrefilter)
        {
            return FirstPrefiltered(text, plan);
        }

        Hit? best = null;
        if (plan.Regex)
        {
            for (var i = 0; i < plan.Regexes!.Length; i++)
            {
                if (plan.Patterns[i].Length == 0)
                {
                    ConsiderEmpty(text, plan.Word, ref best);
                    continue;
                }

                ConsiderFirstRegex(text, plan.Regexes[i], plan, ref best);
            }
        }
        else
        {
            for (var i = 0; i < plan.Patterns.Length; i++)
            {
                ConsiderFirstLiteral(text, plan.Patterns[i], ComparisonAt(plan, i), plan.Word, ref best);
            }
        }

        return best;
    }

    private static void CollectPrefiltered(string text, int[] starts, int lineCount, string relativePath, MatchPlan plan, List<SearchMatch> matches)
    {
        var hits = new List<Hit>();
        ForCandidateLines(text, starts, lineCount, plan, (start, slice) =>
        {
            for (var i = 0; i < plan.Regexes!.Length; i++)
            {
                if (plan.Patterns[i].Length == 0)
                {
                    continue;
                }

                foreach (var match in plan.Regexes[i].EnumerateMatches(slice))
                {
                    var index = start + match.Index;
                    if (plan.Word && !WordBoundary.At(text, index, match.Length))
                    {
                        continue;
                    }

                    var value = plan.NeedMatchText ? slice.Substring(match.Index, match.Length) : "";
                    hits.Add(new Hit(index, match.Length, value));
                }
            }
        });

        hits.Sort(static (left, right) =>
        {
            var index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Length.CompareTo(right.Length);
        });
        var cache = default(SharedLine);
        var budget = new LineBudget(plan.MaxCount);
        foreach (var hit in Dedup(hits))
        {
            if (!budget.Allow(LineMap.Map(starts, hit.Index).Line))
            {
                break;
            }

            AddHit(text, starts, lineCount, relativePath, plan, hit, matches, ref cache);
        }
    }

    private static Hit? FirstPrefiltered(string text, MatchPlan plan)
    {
        var starts = LineMap.Build(text);
        var lineCount = LineMap.LineCount(text, starts);
        Hit? found = null;
        ForCandidateLines(text, starts, lineCount, plan, (start, slice) =>
        {
            if (found != null)
            {
                return;
            }

            var bestRel = int.MaxValue;
            for (var i = 0; i < plan.Regexes!.Length; i++)
            {
                if (plan.Patterns[i].Length == 0)
                {
                    continue;
                }

                var from = 0;
                while (from <= slice.Length && from < bestRel)
                {
                    var match = plan.Regexes[i].Match(slice, from);
                    if (!match.Success || match.Index >= bestRel)
                    {
                        break;
                    }

                    var index = start + match.Index;
                    if (!plan.Word || WordBoundary.At(text, index, match.Length))
                    {
                        found = new Hit(index, match.Length, match.Value);
                        bestRel = match.Index;
                        break;
                    }

                    var next = match.Index + Math.Max(1, match.Length);
                    from = next <= from ? from + 1 : next;
                }
            }
        });
        return found;
    }

    private static void MarkPrefiltered(string text, int[] starts, MatchPlan plan, HashSet<int> lines)
    {
        var lineCount = LineMap.LineCount(text, starts);
        ForCandidateLines(text, starts, lineCount, plan, (start, slice) =>
        {
            if (SliceHits(text, slice, start, plan))
            {
                lines.Add(LineMap.Map(starts, start).Line);
            }
        });
    }

    private static bool SliceHits(string text, string slice, int start, MatchPlan plan)
    {
        foreach (var regex in plan.Regexes!)
        {
            var from = 0;
            while (from <= slice.Length)
            {
                var match = regex.Match(slice, from);
                if (!match.Success)
                {
                    break;
                }

                if (!plan.Word || WordBoundary.At(text, start + match.Index, match.Length))
                {
                    return true;
                }

                var next = match.Index + Math.Max(1, match.Length);
                from = next <= from ? from + 1 : next;
            }
        }

        return false;
    }

    private static void ForCandidateLines(string text, int[] starts, int lineCount, MatchPlan plan, Action<int, string> body)
    {
        if (lineCount <= 0 || plan.Literals == null)
        {
            return;
        }

        var candidates = new bool[lineCount];
        plan.Literals.MarkLines(text, starts, lineCount, plan.PrefilterIgnoreCase, candidates);
        for (var line = 1; line <= lineCount; line++)
        {
            if (!candidates[line - 1])
            {
                continue;
            }

            var start = starts[line - 1];
            var end = line < starts.Length ? starts[line] - 1 : text.Length;
            if (end < start)
            {
                end = start;
            }

            body(start, text.Substring(start, end - start));
        }
    }

    private static void ConsiderFirstLiteral(string text, string pattern, StringComparison comparison, bool word, ref Hit? best)
    {
        if (pattern.Length == 0)
        {
            ConsiderEmpty(text, word, ref best);
            return;
        }

        if (pattern.Length > text.Length)
        {
            return;
        }

        var limit = best?.Index ?? int.MaxValue;
        var from = 0;
        while (from <= text.Length - pattern.Length && from < limit)
        {
            var idx = text.IndexOf(pattern, from, comparison);
            if (idx < 0 || idx >= limit)
            {
                return;
            }

            if (!word || WordBoundary.At(text, idx, pattern.Length))
            {
                best = new Hit(idx, pattern.Length, text.Substring(idx, pattern.Length));
                return;
            }

            from = idx + 1;
        }
    }

    private static void ConsiderFirstRegex(string text, Regex regex, MatchPlan plan, ref Hit? best)
    {
        var limit = best?.Index ?? int.MaxValue;
        var from = 0;
        while (from <= text.Length && from < limit)
        {
            var match = regex.Match(text, from);
            if (!match.Success || match.Index >= limit)
            {
                return;
            }

            var crosses = ContainsLineBreak(text, match.Index, match.Length);
            if ((plan.CrossesLines || !crosses) && (!plan.Word || WordBoundary.At(text, match.Index, match.Length)))
            {
                best = new Hit(match.Index, match.Length, match.Value);
                return;
            }

            var next = match.Index + Math.Max(1, match.Length);
            from = next <= from ? from + 1 : next;
        }
    }

    /// <summary>
    /// Collects the printed rows for a plain text report straight from the text, with no line
    /// strings. One row per matching line, in path order, holding the byte range the writer
    /// copies. ASCII input only: character offsets are byte offsets there.
    /// </summary>
    public static void CollectRawLines(
        string text,
        ReadOnlySpan<byte> bytes,
        MatchPlan plan,
        List<RawLine> lines,
        System.Buffers.ArrayBufferWriter<byte> body)
    {
        var starts = LineMap.Build(text);
        var lineCount = LineMap.LineCount(text, starts);
        if (lineCount == 0 || plan.Regexes == null)
        {
            return;
        }

        var limit = plan.MaxCount ?? int.MaxValue;
        var lastLine = 0;
        var lineCursor = 0;
        for (var i = 0; i < plan.Regexes.Length; i++)
        {
            if (plan.Patterns[i].Length == 0)
            {
                continue;
            }

            foreach (var match in plan.Regexes[i].EnumerateMatches(text))
            {
                if (!plan.CrossesLines && ContainsLineBreak(text, match.Index, match.Length))
                {
                    continue;
                }

                if (plan.Word && !WordBoundary.At(text, match.Index, match.Length))
                {
                    continue;
                }

                while (lineCursor + 1 < starts.Length && starts[lineCursor + 1] <= match.Index)
                {
                    lineCursor++;
                }

                var line = lineCursor + 1;
                if (line <= lastLine)
                {
                    continue;
                }

                if (lines.Count >= limit)
                {
                    return;
                }

                lastLine = line;
                var start = starts[line - 1];
                var end = line < starts.Length ? starts[line] - 1 : text.Length;
                if (end > start && text[end - 1] == '\r')
                {
                    end--;
                }

                // Copy the row body once, as bytes. No line string is ever built.
                var offset = body.WrittenCount;
                var slice = bytes.Slice(start, end - start);
                slice.CopyTo(body.GetSpan(slice.Length));
                body.Advance(slice.Length);
                lines.Add(new RawLine(line, match.Index - start + 1, offset, slice.Length));
            }
        }
    }

    private static bool ContainsLineBreak(string text, int index, int length)
    {
        if (length <= 0 || index < 0 || index >= text.Length)
        {
            return false;
        }

        var end = index + length;
        if (end > text.Length)
        {
            end = text.Length;
        }

        return text.IndexOf('\n', index, end - index) >= 0;
    }

    private static StringComparison ComparisonAt(MatchPlan plan, int index)
    {
        var fold = plan.PatternIgnoreCase != null ? plan.PatternIgnoreCase[index] : plan.IgnoreCase;
        return fold ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    private static bool[] FoldFlags(SearchQuery query, string[] patterns)
    {
        if (query.PatternIgnoreCase == null)
        {
            var folds = new bool[patterns.Length];
            Array.Fill(folds, query.IgnoreCase);
            return folds;
        }

        if (query.PatternIgnoreCase.Length != patterns.Length)
        {
            throw new ArgumentException("Pattern case flags do not match the patterns.");
        }

        return query.PatternIgnoreCase;
    }

    private static string[] Combine(SearchQuery query)
    {
        var extra = query.AdditionalPatterns;
        var patterns = new string[1 + extra.Count];
        patterns[0] = query.Pattern;
        for (var i = 0; i < extra.Count; i++)
        {
            patterns[i + 1] = extra[i];
        }

        return patterns;
    }

    private static void MarkMatchingLines(string text, int[] starts, MatchPlan plan, HashSet<int> lines)
    {
        if (plan.LineRegexp)
        {
            var lineCount = LineMap.LineCount(text, starts);
            for (var line = 1; line <= lineCount; line++)
            {
                if (WholeLineHit(text, starts, line, plan, out _))
                {
                    lines.Add(line);
                }
            }

            return;
        }

        if (plan.Regex)
        {
            if (plan.UseLinePrefilter)
            {
                MarkPrefiltered(text, starts, plan, lines);
                return;
            }

            foreach (var regex in plan.Regexes!)
            {
                MarkRegex(text, starts, regex, plan, lines);
            }

            return;
        }

        for (var i = 0; i < plan.Patterns.Length; i++)
        {
            MarkLiteral(text, starts, plan.Patterns[i], ComparisonAt(plan, i), plan.Word, lines);
        }
    }

    private static void MarkLiteral(string text, int[] starts, string pattern, StringComparison comparison, bool word, HashSet<int> lines)
    {
        if (pattern.Length == 0)
        {
            MarkEmpty(text, starts, word, lines);
            return;
        }

        if (pattern.Length > text.Length)
        {
            return;
        }

        var from = 0;
        while (from <= text.Length - pattern.Length)
        {
            var idx = text.IndexOf(pattern, from, comparison);
            if (idx < 0)
            {
                return;
            }

            if (!word || WordBoundary.At(text, idx, pattern.Length))
            {
                var startLine = LineMap.Map(starts, idx).Line;
                var endIndex = idx + pattern.Length - 1;
                var endLine = LineMap.Map(starts, endIndex).Line;
                for (var line = startLine; line <= endLine; line++)
                {
                    lines.Add(line);
                }

                if (endLine == startLine)
                {
                    var newline = text.IndexOf('\n', idx);
                    if (newline < 0)
                    {
                        return;
                    }

                    from = newline + 1;
                    continue;
                }

                from = idx + Math.Max(1, pattern.Length);
                continue;
            }

            from = idx + 1;
        }
    }

    private static void MarkRegex(string text, int[] starts, Regex regex, MatchPlan plan, HashSet<int> lines)
    {
        if (regex.ToString().Length == 0)
        {
            MarkEmpty(text, starts, plan.Word, lines);
            return;
        }

        var from = 0;
        while (from <= text.Length)
        {
            var match = regex.Match(text, from);
            if (!match.Success)
            {
                return;
            }

            var crosses = ContainsLineBreak(text, match.Index, match.Length);
            var accept = (plan.CrossesLines || !crosses) && (!plan.Word || WordBoundary.At(text, match.Index, match.Length));
            if (accept)
            {
                var startLine = LineMap.Map(starts, match.Index).Line;
                var endIndex = match.Length == 0 ? match.Index : match.Index + match.Length - 1;
                if (text.Length == 0)
                {
                    endIndex = match.Index;
                }
                else if (endIndex >= text.Length)
                {
                    endIndex = text.Length - 1;
                }

                var endLine = text.Length == 0 ? startLine : LineMap.Map(starts, endIndex).Line;
                for (var line = startLine; line <= endLine; line++)
                {
                    lines.Add(line);
                }

                if (!crosses)
                {
                    if (match.Index >= text.Length)
                    {
                        return;
                    }

                    var newline = text.IndexOf('\n', match.Index);
                    if (newline < 0)
                    {
                        return;
                    }

                    from = newline + 1;
                    continue;
                }
            }

            var next = match.Index + Math.Max(1, match.Length);
            from = next <= from ? from + 1 : next;
        }
    }

    /// <summary>
    /// An empty pattern matches every line once, at column 1. Ripgrep's empty regex also
    /// reports a zero-width match at every offset; text output still shows each line once.
    /// </summary>
    private static void CollectEmpty(string text, bool word, List<Hit> hits, int maxLines = 0)
    {
        if (text.Length == 0)
        {
            return;
        }

        var kept = 0;
        var from = 0;
        while (from < text.Length)
        {
            if (!word || WordBoundary.At(text, from, 0))
            {
                if (maxLines > 0 && kept >= maxLines)
                {
                    return;
                }

                hits.Add(new Hit(from, 0, ""));
                kept++;
            }

            var newline = text.IndexOf('\n', from);
            if (newline < 0)
            {
                break;
            }

            from = newline + 1;
        }
    }

    private static void ConsiderEmpty(string text, bool word, ref Hit? best)
    {
        if (text.Length == 0 || (word && !WordBoundary.At(text, 0, 0)))
        {
            return;
        }

        var limit = best?.Index ?? int.MaxValue;
        if (0 < limit)
        {
            best = new Hit(0, 0, "");
        }
    }

    private static void MarkEmpty(string text, int[] starts, bool word, HashSet<int> lines)
    {
        if (text.Length == 0)
        {
            return;
        }

        var from = 0;
        while (from < text.Length)
        {
            if (!word || WordBoundary.At(text, from, 0))
            {
                lines.Add(LineMap.Map(starts, from).Line);
            }

            var newline = text.IndexOf('\n', from);
            if (newline < 0)
            {
                break;
            }

            from = newline + 1;
        }
    }

    private static void CollectLiteral(string text, string pattern, StringComparison comparison, bool word, List<Hit> hits, int maxLines = 0, bool needText = true)
    {
        if (pattern.Length == 0 || pattern.Length > text.Length)
        {
            return;
        }

        var line = 1;
        var cursor = 0;
        var kept = 0;
        var lastLine = 0;
        var from = 0;
        while (from <= text.Length - pattern.Length)
        {
            var idx = text.IndexOf(pattern, from, comparison);
            if (idx < 0)
            {
                break;
            }

            if (!word || WordBoundary.At(text, idx, pattern.Length))
            {
                if (maxLines > 0 && !AcceptLimitedLine(text, idx, maxLines, ref line, ref cursor, ref kept, ref lastLine))
                {
                    return;
                }

                hits.Add(new Hit(idx, pattern.Length, needText ? text.Substring(idx, pattern.Length) : ""));
                from = idx + pattern.Length;
            }
            else
            {
                from = idx + 1;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="index"/> still belongs to one of the first <paramref name="maxLines"/> lines.
    /// The cursor walks forward so each newline is counted once.
    /// </summary>
    private static bool AcceptLimitedLine(string text, int index, int maxLines, ref int line, ref int cursor, ref int kept, ref int lastLine)
    {
        for (var i = cursor; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        cursor = index;
        if (kept > 0 && line == lastLine)
        {
            return true;
        }

        if (kept >= maxLines)
        {
            return false;
        }

        kept++;
        lastLine = line;
        return true;
    }

    private static List<Hit> Dedup(List<Hit> hits)
    {
        if (hits.Count < 2)
        {
            return hits;
        }

        var deduped = new List<Hit>(hits.Count);
        foreach (var hit in hits)
        {
            if (deduped.Count > 0)
            {
                var last = deduped[^1];
                if (last.Index == hit.Index && last.Length == hit.Length)
                {
                    continue;
                }
            }

            deduped.Add(hit);
        }

        return deduped;
    }

    private static (IReadOnlyList<ContextLine>? Before, IReadOnlyList<ContextLine>? After) Context(
        string text,
        int[] starts,
        int lineCount,
        int line,
        MatchPlan plan)
    {
        if (!plan.WantsContext)
        {
            return (null, null);
        }

        return (SliceBefore(text, starts, line, plan.Before), SliceAfter(text, starts, lineCount, line, plan.After));
    }

    private static ContextLine[] SliceBefore(string text, int[] starts, int line, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var startLine = count >= line ? 1 : line - count;
        var length = line - startLine;
        if (length <= 0)
        {
            return [];
        }

        var lines = new ContextLine[length];
        for (var i = 0; i < length; i++)
        {
            var number = startLine + i;
            lines[i] = new ContextLine(number, LineMap.LineText(text, starts, number), LineStart(starts, number));
        }

        return lines;
    }

    private static ContextLine[] SliceAfter(string text, int[] starts, int lineCount, int line, int count)
    {
        if (count <= 0 || line >= lineCount)
        {
            return [];
        }

        var available = lineCount - line;
        var take = count >= available ? available : count;
        var lines = new ContextLine[take];
        for (var i = 0; i < take; i++)
        {
            var number = line + 1 + i;
            lines[i] = new ContextLine(number, LineMap.LineText(text, starts, number), LineStart(starts, number));
        }

        return lines;
    }

    private static int LineStart(int[] starts, int line) =>
        line >= 1 && line - 1 < starts.Length ? starts[line - 1] : 0;

    private readonly record struct Hit(int Index, int Length, string Value);
}

internal static class WordBoundary
{
    public static bool At(string text, int index, int length)
    {
        if (length < 0 || index < 0 || index > text.Length || index + length > text.Length)
        {
            return false;
        }

        return IsBoundary(text, index) && IsBoundary(text, index + length);
    }

    public static bool IsWord(char value) => value == '_' || char.IsLetterOrDigit(value);

    private static bool IsBoundary(string text, int index)
    {
        var left = index > 0 && IsWord(text[index - 1]);
        var right = index < text.Length && IsWord(text[index]);
        return left != right;
    }
}

/// <summary>
/// A regex with no metacharacters (escapes of those metacharacters are unescaped).
/// Case-sensitive search then matches the same spans as the interpreter.
/// </summary>
internal static class VerbatimPattern
{
    public static bool TryAll(string[] patterns, out string[] literals)
    {
        literals = new string[patterns.Length];
        for (var i = 0; i < patterns.Length; i++)
        {
            if (!TryOne(patterns[i], out literals[i]))
            {
                literals = [];
                return false;
            }
        }

        return true;
    }

    public static bool TryOne(string pattern, out string literal)
    {
        var built = new System.Text.StringBuilder(pattern.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                if (i + 1 >= pattern.Length || !IsEscapedLiteral(pattern[i + 1]))
                {
                    literal = "";
                    return false;
                }

                built.Append(pattern[++i]);
                continue;
            }

            if (IsMeta(ch))
            {
                literal = "";
                return false;
            }

            built.Append(ch);
        }

        literal = built.ToString();
        // A newline inside a regex is a cross-line match. Without -U those spans are
        // discarded, so this is not the same as a literal search.
        if (literal.IndexOf('\n') >= 0 || literal.IndexOf('\r') >= 0)
        {
            literal = "";
            return false;
        }

        return true;
    }

    private static bool IsMeta(char value) =>
        value is '.' or '|' or '(' or ')' or '[' or ']' or '{' or '}' or '^' or '$' or '*' or '+' or '?';

    private static bool IsEscapedLiteral(char value) => IsMeta(value) || value == '\\';
}

/// <summary>
/// Counts distinct matching lines toward <c>-m</c>. Another hit on the current line stays allowed.
/// </summary>
internal struct LineBudget
{
    private readonly int _limit;
    private int _lines;
    private int _line;

    public LineBudget(int? maxCount)
    {
        _limit = maxCount ?? int.MaxValue;
        _lines = 0;
        _line = 0;
    }

    public bool Allow(int line)
    {
        if (_lines > 0 && line == _line)
        {
            return true;
        }

        if (_lines >= _limit)
        {
            return false;
        }

        _lines++;
        _line = line;
        return true;
    }
}

internal static class MatchLimits
{
    public static void Trim(List<SearchMatch> matches, int maxLines)
    {
        if (matches.Count == 0)
        {
            return;
        }

        if (maxLines <= 0)
        {
            matches.Clear();
            return;
        }

        if (matches.Count > 1)
        {
            matches.Sort(static (left, right) =>
            {
                var line = left.Line.CompareTo(right.Line);
                if (line != 0)
                {
                    return line;
                }

                var column = left.Column.CompareTo(right.Column);
                return column != 0 ? column : left.Text.Length.CompareTo(right.Text.Length);
            });
        }

        var lines = 0;
        var lineNo = 0;
        var keep = 0;
        for (; keep < matches.Count; keep++)
        {
            var next = matches[keep].Line;
            if (keep == 0 || next != lineNo)
            {
                if (lines == maxLines)
                {
                    break;
                }

                lines++;
                lineNo = next;
            }
        }

        if (keep < matches.Count)
        {
            matches.RemoveRange(keep, matches.Count - keep);
        }
    }
}
