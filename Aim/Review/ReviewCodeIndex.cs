using System.Globalization;

namespace Aim;

internal readonly record struct ReviewSnippetLine(int Number, string Text);

/// <summary>
/// New-file lines cited by a review comment. Lines come from the unified diff
/// when that diff contains them. Otherwise they are read from the reviewed
/// revision, or from the working tree when the review is not pinned to a revision.
/// </summary>
internal sealed class ReviewCodeIndex
{
    public const int MaxLines = 20;
    public const int MaxLineChars = 200;

    private readonly Dictionary<string, Dictionary<int, string>> _files;

    private ReviewCodeIndex(Dictionary<string, Dictionary<int, string>> files) => _files = files;

    public static ReviewCodeIndex Empty { get; } = new(new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal));

    public static ReviewCodeIndex FromDiff(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
            return Empty;

        var files = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        foreach (var patch in ReviewDiffPlanner.SplitPatches(diff))
        {
            var key = patch.Path;
            if (key.Length == 0 || key == "unknown")
                continue;

            if (RepoPathGuard.TryNormalize(key, out var relative, out _))
                key = relative;

            if (!files.TryGetValue(key, out var lines))
            {
                lines = [];
                files[key] = lines;
            }

            Collect(patch.Text, lines);
        }

        return new ReviewCodeIndex(files);
    }

    public bool TryGet(string path, int start, int end, out List<ReviewSnippetLine> lines, out int omitted)
    {
        lines = [];
        omitted = 0;
        if (!TryWindow(start, end, out var first, out var last, out omitted))
            return false;

        if (!TryLookupKey(path, out var relative) || !_files.TryGetValue(relative, out var map))
            return false;

        for (var number = first; number <= last; number++)
        {
            if (map.TryGetValue(number, out var text))
                lines.Add(new ReviewSnippetLine(number, Clip(text)));
        }

        return lines.Count > 0;
    }

    public static bool TryReadFallback(
        string workingDirectory,
        string? revision,
        ref string? repoRoot,
        string path,
        int start,
        int end,
        out List<ReviewSnippetLine> lines,
        out int omitted)
    {
        lines = [];
        omitted = 0;
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return false;

        if (!TryWindow(start, end, out var first, out var last, out omitted))
            return false;

        repoRoot ??= ResolveRoot(workingDirectory);
        if (!string.IsNullOrWhiteSpace(revision))
            return TryReadRevision(repoRoot, revision, path, first, last, out lines);

        return TryReadWorkingTree(repoRoot, path, first, last, out lines);
    }

    private static bool TryReadWorkingTree(string root, string path, int first, int last, out List<ReviewSnippetLine> lines)
    {
        lines = [];
        if (!RepoPathGuard.TryResolve(root, path, out _, out var fullPath, out _))
            return false;

        if (!File.Exists(fullPath))
            return false;

        try
        {
            using var reader = new StreamReader(fullPath);
            return TakeWindow(reader, first, last, out lines);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryReadRevision(string root, string revision, string path, int first, int last, out List<ReviewSnippetLine> lines)
    {
        lines = [];
        if (!RepoPathGuard.TryNormalize(path, out var relative, out _))
            return false;

        var (ok, output, _) = GitExecutor.RunGit(
            ["show", "--no-color", $"{revision}:{relative}"],
            root);
        if (!ok || output.Contains('\0'))
            return false;

        using var reader = new StringReader(output);
        return TakeWindow(reader, first, last, out lines);
    }

    private static string ResolveRoot(string workingDirectory)
    {
        var (ok, output, _) = GitExecutor.RunGit(["rev-parse", "--show-toplevel"], workingDirectory);
        if (!ok || string.IsNullOrWhiteSpace(output))
            return workingDirectory;

        var root = output.Trim();
        return Directory.Exists(root) ? root : workingDirectory;
    }

    private static bool TryWindow(int start, int end, out int first, out int last, out int omitted)
    {
        first = 0;
        last = 0;
        omitted = 0;
        if (start < 1)
            return false;

        if (end < start)
            end = start;

        var span = (long)end - start + 1;
        if (span < 1)
            return false;

        var take = (int)Math.Min(span, MaxLines);
        if (start > int.MaxValue - take)
            return false;

        var omittedLong = span - take;
        omitted = omittedLong > int.MaxValue ? int.MaxValue : (int)omittedLong;
        first = start;
        last = start + take - 1;
        return true;
    }

    private static bool TryLookupKey(string path, out string relative)
    {
        if (RepoPathGuard.TryNormalize(path, out relative, out _))
            return true;

        relative = path.Replace('\\', '/').Trim();
        return relative.Length > 0;
    }

    private static bool TakeWindow(TextReader reader, int first, int last, out List<ReviewSnippetLine> lines)
    {
        lines = [];
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (number < first)
                continue;

            lines.Add(new ReviewSnippetLine(number, Clip(line)));
            if (number >= last)
                break;
        }

        return lines.Count > 0;
    }

    private static void Collect(string patch, Dictionary<int, string> lines)
    {
        var normalized = patch.Replace("\r\n", "\n", StringComparison.Ordinal);
        var inHunk = false;
        var newLine = 0;
        foreach (var line in normalized.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunk = false;
                continue;
            }

            if (TryParseNewStart(line, out var start))
            {
                inHunk = true;
                newLine = start;
                continue;
            }

            if (!inHunk || newLine < 1)
                continue;

            if (line.StartsWith('\\'))
                continue;

            if (line.Length == 0)
            {
                lines[newLine] = "";
                newLine++;
                continue;
            }

            if (line[0] == '-')
                continue;

            if (line[0] is '+' or ' ')
            {
                lines[newLine] = line.Length == 1 ? "" : line[1..];
                newLine++;
            }
        }
    }

    private static bool TryParseNewStart(string line, out int newStart)
    {
        newStart = 0;
        if (!line.StartsWith("@@ ", StringComparison.Ordinal))
            return false;

        var plus = line.IndexOf(" +", StringComparison.Ordinal);
        if (plus < 0 || plus + 2 >= line.Length)
            return false;

        var i = plus + 2;
        var start = i;
        while (i < line.Length && line[i] is >= '0' and <= '9')
            i++;

        if (i == start)
            return false;

        if (i < line.Length && line[i] is not (',' or ' '))
            return false;

        return int.TryParse(line.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out newStart);
    }

    private static string Clip(string text)
    {
        if (text.Length <= MaxLineChars)
            return text;

        var end = MaxLineChars;
        if (char.IsHighSurrogate(text[end - 1]))
            end--;

        return string.Concat(text.AsSpan(0, end), "...");
    }
}
