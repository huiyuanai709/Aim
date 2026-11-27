using System.Globalization;
using System.Text;
using Ngrep.Search;

namespace Aim;

/// <summary>
/// Worktree <c>code_search</c>. Fixed string, repo-relative paths, binaries skipped.
/// Stops once <paramref name="maxLines"/> matching lines are known instead of reading
/// the rest of the tree. Uses <c>.ngrep/index</c> when one is found and does not write an index.
/// </summary>
internal static class NgrepWorktreeSearch
{
    public readonly record struct Text(string? Error, string Output);

    public static Text Collect(string repoRoot, string? relativePath, string query, int maxLines)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);
        SearchCommand.PrepareThreads();

        var start = repoRoot;
        string? display = null;
        if (!string.IsNullOrEmpty(relativePath))
        {
            start = Path.GetFullPath(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            display = relativePath.Replace('\\', '/');
        }

        using var cts = new CancellationTokenSource();
        var lines = new List<string>(Math.Min(maxLines, 128));
        var gate = new object();
        var events = new SearchEvents
        {
            Batch = batch =>
            {
                lock (gate)
                {
                    if (lines.Count >= maxLines)
                    {
                        cts.Cancel();
                        return;
                    }

                    foreach (var match in batch.Matches)
                    {
                        if (lines.Count >= maxLines)
                            break;
                        lines.Add(Format(match));
                    }

                    if (lines.Count >= maxLines)
                        cts.Cancel();
                }
            }
        };

        try
        {
            Searcher.Search(new SearchQuery
            {
                Pattern = query,
                Path = start,
                DisplayRoot = display,
                Regex = false,
                OneRowPerLine = true,
                NeedLineText = true,
                NeedMatchText = false,
                RawTextLines = false,
                MaxCount = maxLines,
                IncludeHidden = true,
                Report = ReportMode.Matches,
                Events = events,
                CancellationToken = cts.Token
            }).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (SearchPathException ex)
        {
            return new Text(ex.Message, "");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new Text(ex.Message, "");
        }

        if (lines.Count == 0)
            return new Text(null, "");

        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.AppendLine(line);
        return new Text(null, sb.ToString());
    }

    private static string Format(SearchMatch match)
    {
        var path = match.Path.Replace('\\', '/');
        var text = match.LineText.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        return string.Concat(path, ":", match.Line.ToString(CultureInfo.InvariantCulture), ":", text);
    }
}
