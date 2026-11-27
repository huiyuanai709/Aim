using System.Text;
using System.Text.Json;
using Ngrep.Search;

namespace Ngrep.Output;

internal static partial class MachineOutput
{
    /// <summary>
    /// Writes search hits as files finish, in the order batches arrive.
    /// Call <see cref="Complete"/> after the search so JSON arrays and the rgjson summary close.
    /// </summary>
    internal sealed class MatchStream
    {
        private readonly OutputFormat _format;
        private readonly SearchPresentation _presentation;
        private readonly bool _holdAutoHeading;
        // Text records for one file are buffered here and written in bulk. A pipe flushes
        // that buffer at a bounded size so a file with many hits does not stay in memory.
        // A terminal flushes each line so the hit is visible before the file ends.
        private readonly StringBuilder _sink = new(1 << 16);
        private readonly bool _flushEachLine;
        private readonly System.Buffers.ArrayBufferWriter<byte> _raw = new(1 << 16);
        private const int SinkFlush = 1 << 16;
        private const int RawFlush = 1 << 16;
        private bool _useHeading;
        private List<SearchMatch>? _held;
        private string? _headingPath;
        private bool _headingStarted;
        private bool _jsonStarted;
        private bool _jsonAny;
        private int _examined;
        private int _rgFiles;
        private int _rgMatches;
        private int _rgLines;

        public MatchStream(OutputFormat format, SearchPresentation presentation, bool holdAutoHeading)
        {
            _format = format;
            _presentation = presentation;
            _holdAutoHeading = holdAutoHeading && format == OutputFormat.Text && presentation.Report == ReportMode.Matches;
            _useHeading = presentation.Heading;
            _flushEachLine = Stdout.Active.AutoFlush;
        }

        public void File(SearchBatch batch)
        {
            if (batch.PrefilterMiss)
            {
                if (_presentation.Report == ReportMode.FilesWithoutMatch)
                {
                    Without(batch.Path);
                }

                return;
            }

            if (!batch.Examined)
            {
                return;
            }

            if (batch.RawBody != null)
            {
                // Rows were prepared from the file bytes; print them here, on this thread, so
                // the order matches the decoded path.
                WriteRawRows(batch.Path, batch.RawBody, batch.RawRows ?? []);
                return;
            }

            switch (_presentation.Report)
            {
                case ReportMode.Quiet:
                    return;
                case ReportMode.FilesWithoutMatch:
                    if (!batch.Matched)
                    {
                        Without(batch.Path);
                    }

                    return;
                case ReportMode.FilesWithMatches:
                    if (batch.Matches.Count > 0)
                    {
                        Paths(batch);
                    }

                    return;
                case ReportMode.Count:
                    if (batch.Matches.Count > 0)
                    {
                        Counts(batch.Matches);
                    }
                    else if (batch.Lines > 0)
                    {
                        // The scan counted the lines without building a record per line.
                        CountOnly(batch.Path, batch.Lines);
                    }

                    return;
            }

            if (!_holdAutoHeading)
            {
                if (batch.Matches.Count > 0)
                {
                    WriteMatchFile(batch.Matches);
                }

                return;
            }

            _examined++;
            if (_held != null && _examined >= 2)
            {
                _useHeading = true;
                WriteMatchFile(_held);
                _held = null;
            }

            if (batch.Matches.Count == 0)
            {
                return;
            }

            if (_examined == 1)
            {
                _held = batch.Matches;
                return;
            }

            _useHeading = true;
            WriteMatchFile(batch.Matches);
        }

        public void Complete(SearchOutcome outcome)
        {
            if (_held != null)
            {
                WriteMatchFile(_held);
                _held = null;
            }

            switch (_presentation.Report)
            {
                case ReportMode.Quiet:
                    return;
                case ReportMode.Count when _format == OutputFormat.Json:
                    CloseJson("counts");
                    return;
                case ReportMode.FilesWithMatches when _format == OutputFormat.Json:
                case ReportMode.FilesWithoutMatch when _format == OutputFormat.Json:
                    CloseJson("paths");
                    return;
                case ReportMode.Matches when _format == OutputFormat.Json:
                    CloseJson("matches");
                    return;
                case ReportMode.Matches when _format == OutputFormat.RgJson:
                case ReportMode.FilesWithMatches when _format == OutputFormat.RgJson:
                case ReportMode.Count when _format == OutputFormat.RgJson:
                    WriteRgSummary(_rgFiles, _rgMatches, _rgLines, outcome.ElapsedMilliseconds);
                    return;
                case ReportMode.FilesWithoutMatch when _format == OutputFormat.RgJson:
                    WriteRgSummary(_rgFiles, matches: 0, matchedLines: 0, outcome.ElapsedMilliseconds);
                    return;
            }
        }

        private void Without(string path)
        {
            switch (_format)
            {
                case OutputFormat.Text:
                    TextLayout.WriteFilePath(path, _presentation);
                    break;
                case OutputFormat.Ndjson:
                    Console.WriteLine(PathJson(path));
                    break;
                case OutputFormat.Json:
                    JsonString(path);
                    break;
                case OutputFormat.RgJson:
                    WriteRgBegin(path);
                    WriteRgEnd(path, matches: 0, matchedLines: 0);
                    _rgFiles++;
                    break;
            }

            Console.Out.Flush();
        }

        private void Paths(SearchBatch batch)
        {
            var path = batch.Path;
            switch (_format)
            {
                case OutputFormat.Text:
                    TextLayout.WriteFilePath(path, _presentation);
                    break;
                case OutputFormat.Ndjson:
                    Console.WriteLine(PathJson(path));
                    break;
                case OutputFormat.Json:
                    JsonString(path);
                    break;
                case OutputFormat.RgJson:
                    WriteRgFile(batch.Matches);
                    break;
            }

            Console.Out.Flush();
        }

        private void CountOnly(string path, int count)
        {
            switch (_format)
            {
                case OutputFormat.Text:
                    TextLayout.WriteCount(path, count, _presentation);
                    break;
                case OutputFormat.Ndjson:
                    Console.WriteLine(CountJson(path, count));
                    break;
                case OutputFormat.Json:
                    JsonValue(CountJson(path, count));
                    break;
                case OutputFormat.RgJson:
                    WriteRgBegin(path);
                    WriteRgEnd(path, count, count);
                    _rgFiles++;
                    _rgMatches += count;
                    _rgLines += count;
                    break;
            }

            Console.Out.Flush();
        }

        private void Counts(IReadOnlyList<SearchMatch> matches)
        {
            var path = matches[0].Path;
            var lines = new HashSet<int>();
            foreach (var match in matches)
            {
                lines.Add(match.Line);
            }

            switch (_format)
            {
                case OutputFormat.Text:
                    TextLayout.WriteCount(path, lines.Count, _presentation);
                    break;
                case OutputFormat.Ndjson:
                    Console.WriteLine(CountJson(path, lines.Count));
                    break;
                case OutputFormat.Json:
                    JsonValue(CountJson(path, lines.Count));
                    break;
                case OutputFormat.RgJson:
                    WriteRgBegin(path);
                    WriteRgEnd(path, matches.Count, lines.Count);
                    _rgFiles++;
                    _rgMatches += matches.Count;
                    _rgLines += lines.Count;
                    break;
            }

            Console.Out.Flush();
        }

        private void WriteMatchFile(IReadOnlyList<SearchMatch> matches)
        {
            switch (_format)
            {
                case OutputFormat.Text:
                    WriteTextFile(matches);
                    break;
                case OutputFormat.Ndjson:
                    foreach (var match in matches)
                    {
                        Console.WriteLine(MatchJson(match));
                    }

                    break;
                case OutputFormat.Json:
                    foreach (var match in matches)
                    {
                        JsonValue(MatchJson(match));
                    }

                    break;
                case OutputFormat.RgJson:
                    WriteRgFile(matches);
                    break;
            }

            Console.Out.Flush();
        }

        private void WriteTextFile(IReadOnlyList<SearchMatch> matches)
        {
            var lines = LineIndex(matches, _presentation, _presentation.OnlyMatching);
            if (_presentation.OnlyMatching || matches.All(static match => match.Before == null && match.After == null))
            {
                string? lastPath = null;
                var lastWrittenLine = 0;
                foreach (var match in matches)
                {
                    if (_presentation.OneRowPerLine
                        && match.Line == lastWrittenLine
                        && string.Equals(lastPath, match.Path, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    lastPath = match.Path;
                    lastWrittenLine = match.Line;
                    EnsureHeading(match.Path);
                    WriteMatchLine(match, MatchBody(match, lines, _presentation, _presentation.OnlyMatching), _presentation, includePath: _presentation.WithFilename && !_useHeading, colorizeBody: _presentation.Replacer == null, _sink);
                    AfterLine();
                }

                FlushSink();
                return;
            }

            var fileMatches = new HashSet<int>();
            foreach (var match in matches)
            {
                fileMatches.Add(match.Line);
            }

            var path = matches[0].Path;
            var lastLine = 0;
            var printed = false;
            string? lastMatchPath = null;
            var lastMatchLine = 0;
            EnsureHeading(path);
            foreach (var match in matches)
            {
                if (_presentation.OneRowPerLine
                    && match.Line == lastMatchLine
                    && string.Equals(lastMatchPath, match.Path, StringComparison.Ordinal))
                {
                    continue;
                }

                lastMatchPath = match.Path;
                lastMatchLine = match.Line;
                if (match.Before == null && match.After == null)
                {
                    WriteMatchLine(match, MatchBody(match, lines, _presentation, onlyMatching: false), _presentation, includePath: _presentation.WithFilename && !_useHeading, colorizeBody: _presentation.Replacer == null, _sink);
                    AfterLine();
                    continue;
                }

                var before = match.Before ?? [];
                var upcoming = NextVisible(before, fileMatches, match.Line);
                if (printed && upcoming > lastLine + 1)
                {
                    _sink.Append("--").Append(Console.Out.NewLine);
                    AfterLine();
                }

                foreach (var line in before)
                {
                    if (line.Line <= lastLine || fileMatches.Contains(line.Line))
                    {
                        continue;
                    }

                    WriteContextLine(match.Path, line.Line, line.Text, _presentation, includePath: _presentation.WithFilename && !_useHeading, _sink);
                    lastLine = line.Line;
                    printed = true;
                    AfterLine();
                }

                WriteMatchLine(match, MatchBody(match, lines, _presentation, onlyMatching: false), _presentation, includePath: _presentation.WithFilename && !_useHeading, colorizeBody: _presentation.Replacer == null, _sink);
                if (match.Line > lastLine)
                {
                    lastLine = match.Line;
                }

                printed = true;
                AfterLine();
                foreach (var line in match.After ?? [])
                {
                    if (line.Line <= lastLine || fileMatches.Contains(line.Line))
                    {
                        continue;
                    }

                    WriteContextLine(match.Path, line.Line, line.Text, _presentation, includePath: _presentation.WithFilename && !_useHeading, _sink);
                    lastLine = line.Line;
                    AfterLine();
                }
            }

            FlushSink();
        }

        /// <summary>
        /// A terminal hands the line to the writer now. A pipe keeps appending until the block fills.
        /// </summary>
        private void AfterLine()
        {
            if (_flushEachLine)
            {
                FlushSink();
                return;
            }

            MaybeFlushSink();
        }

        /// <summary>Writes buffered records once they reach a bounded size, keeping order.</summary>
        private void MaybeFlushSink()
        {
            if (_sink.Length >= SinkFlush)
            {
                FlushSink();
            }
        }

        private void FlushSink()
        {
            if (_sink.Length == 0)
            {
                return;
            }

            foreach (var chunk in _sink.GetChunks())
            {
                Console.Out.Write(chunk.Span);
            }

            _sink.Clear();
        }

        /// <summary>
        /// Prints prepared rows from their UTF-8 bodies. A row is the same prefix the string
        /// path writes plus the original bytes, so no decode or re-encode happens.
        /// </summary>
        private void WriteRawRows(string path, byte[] body, List<RawLine> rows)
        {
            if (_format != OutputFormat.Text || rows.Count == 0)
            {
                return;
            }

            EnsureHeading(path);
            var includePath = _presentation.WithFilename && !_useHeading;
            var shownPath = includePath ? path : null;
            var bytes = body.AsSpan();
            foreach (var row in rows)
            {
                TextLayout.WriteRawRecord(
                    _raw,
                    bytes.Slice(row.Start, row.Length),
                    shownPath,
                    _presentation.LineNumbers ? row.Line : null,
                    _presentation.Column ? row.Column : null,
                    _presentation.NullPaths);
                if (_flushEachLine || _raw.WrittenCount >= RawFlush)
                {
                    FlushRaw();
                }
            }

            FlushRaw();
        }

        private void FlushRaw()
        {
            if (_raw.WrittenCount == 0)
            {
                return;
            }

            Stdout.WriteBytes(_raw.WrittenSpan);
            _raw.Clear();
        }

        private void EnsureHeading(string path)
        {
            if (!_presentation.WithFilename || !_useHeading || string.Equals(_headingPath, path, StringComparison.Ordinal))
            {
                return;
            }

            // The heading is written directly, so buffered records must land first.
            FlushSink();
            if (_headingStarted)
            {
                Console.WriteLine();
            }

            TextLayout.WriteHeading(path, _presentation);
            _headingPath = path;
            _headingStarted = true;
        }

        private void WriteRgFile(IReadOnlyList<SearchMatch> matches)
        {
            if (matches.Count == 0)
            {
                return;
            }

            var path = matches[0].Path;
            var fileLines = new HashSet<int>();
            HashSet<int>? contextLines = null;
            foreach (var match in matches)
            {
                if (match.Before == null && match.After == null)
                {
                    continue;
                }

                contextLines ??= [];
                contextLines.Add(match.Line);
            }

            WriteRgBegin(path);
            var lastLine = 0;
            foreach (var match in matches)
            {
                if (match.Before != null)
                {
                    foreach (var line in match.Before)
                    {
                        if (line.Line <= lastLine || (contextLines != null && contextLines.Contains(line.Line)))
                        {
                            continue;
                        }

                        WriteRgContext(path, line);
                        lastLine = line.Line;
                    }
                }

                fileLines.Add(match.Line);
                WriteRgMatch(match);
                if (match.Line > lastLine)
                {
                    lastLine = match.Line;
                }

                if (match.After == null)
                {
                    continue;
                }

                foreach (var line in match.After)
                {
                    if (line.Line <= lastLine || (contextLines != null && contextLines.Contains(line.Line)))
                    {
                        continue;
                    }

                    WriteRgContext(path, line);
                    lastLine = line.Line;
                }
            }

            WriteRgEnd(path, matches.Count, fileLines.Count);
            _rgFiles++;
            _rgMatches += matches.Count;
            _rgLines += fileLines.Count;
        }

        private void JsonString(string value) => JsonRaw(Quote(value));

        private void JsonValue(string json) => JsonRaw(json);

        private static string Quote(string value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, JsonOptions))
            {
                writer.WriteStringValue(value);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private void JsonRaw(string json)
        {
            if (!_jsonStarted)
            {
                var name = _presentation.Report switch
                {
                    ReportMode.Count => "counts",
                    ReportMode.FilesWithMatches or ReportMode.FilesWithoutMatch => "paths",
                    _ => "matches",
                };
                Console.Write("{\"");
                Console.Write(name);
                Console.Write("\":[");
                _jsonStarted = true;
            }

            if (_jsonAny)
            {
                Console.Write(',');
            }

            _jsonAny = true;
            Console.Write(json);
        }

        private void CloseJson(string name)
        {
            if (!_jsonStarted)
            {
                Console.Write("{\"");
                Console.Write(name);
                Console.Write("\":[]}");
            }
            else
            {
                Console.Write("]}");
            }

            Console.WriteLine();
            Console.Out.Flush();
        }
    }
}
