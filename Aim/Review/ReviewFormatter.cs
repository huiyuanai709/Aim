using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aim;

internal static class ReviewFormatter
{
    private const string Reset = "\u001b[0m";
    private const string CriticalOrHigh = "\u001b[1;91m";
    private const string Medium = "\u001b[33m";
    private const string Low = "\u001b[2;36m";
    private const string PathStyle = "\u001b[1;36m";
    private const string LineStyle = "\u001b[1m";
    private const string CategoryStyle = "\u001b[35m";
    private const string LabelStyle = "\u001b[1;36m";
    private const string GutterStyle = "\u001b[90m";

    public static string ToJson(IReadOnlyList<ReviewComment> comments)
    {
        var list = comments as List<ReviewComment> ?? comments.ToList();
        return JsonSerializer.Serialize(list, ReviewJsonContext.Default.ListReviewComment);
    }

    public static string ToText(
        IReadOnlyList<ReviewComment> comments,
        bool diffTruncated,
        bool nameOnly,
        bool partial = false,
        bool color = false,
        string? diff = null,
        string? workingDirectory = null,
        string? revision = null)
    {
        if (comments.Count == 0)
        {
            var message = diffTruncated
                ? "No review comments for the portion of the diff that was sent."
                : "No review comments.";
            if (partial)
                message += " Some files or groups were not reviewed.";
            return message;
        }

        var includeSnippets = !nameOnly;
        var index = includeSnippets ? ReviewCodeIndex.FromDiff(diff) : ReviewCodeIndex.Empty;
        string? repoRoot = null;
        var sb = new StringBuilder();
        for (var i = 0; i < comments.Count; i++)
        {
            if (i > 0)
                sb.AppendLine();

            AppendComment(sb, comments[i], color, includeSnippets, index, workingDirectory, revision, ref repoRoot);
        }

        sb.AppendLine();
        sb.Append(comments.Count == 1 ? "1 comment." : $"{comments.Count} comments.");
        sb.Append(" Line numbers refer to the new file and are best-effort (not relocated).");
        if (nameOnly)
            sb.Append(" Only file names were sent, so lines are placeholders.");
        if (diffTruncated)
            sb.Append(" The diff was truncated before review.");
        if (partial)
            sb.Append(" Some files or groups were not reviewed.");

        return sb.ToString();
    }

    private static void AppendComment(
        StringBuilder sb,
        ReviewComment comment,
        bool color,
        bool includeSnippets,
        ReviewCodeIndex index,
        string? workingDirectory,
        string? revision,
        ref string? repoRoot)
    {
        var lines = comment.StartLine == comment.EndLine
            ? comment.StartLine.ToString(CultureInfo.InvariantCulture)
            : $"{comment.StartLine.ToString(CultureInfo.InvariantCulture)}-{comment.EndLine.ToString(CultureInfo.InvariantCulture)}";
        AppendStyled(sb, comment.Path, PathStyle, color);
        sb.Append(':');
        AppendStyled(sb, lines, LineStyle, color);
        sb.Append("  ");
        AppendSeverity(sb, comment.Severity, color);
        sb.Append("  ");
        AppendStyled(sb, comment.Category, CategoryStyle, color);
        sb.AppendLine();

        foreach (var line in comment.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            sb.AppendLine($"  {line}");

        if (includeSnippets)
            AppendSnippet(sb, comment, color, index, workingDirectory, revision, ref repoRoot);

        if (string.IsNullOrWhiteSpace(comment.SuggestionCode))
            return;

        AppendLabel(sb, "Suggestion:", color);
        var suggestion = default(ReviewHighlighter.State);
        foreach (var line in comment.SuggestionCode.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            sb.Append("    ");
            AppendCode(sb, line, comment.Path, color, ref suggestion);
            sb.AppendLine();
        }
    }

    private static void AppendStyled(StringBuilder sb, string text, string style, bool color)
    {
        if (!color || text.Length == 0)
        {
            sb.Append(text);
            return;
        }

        sb.Append(style);
        sb.Append(text);
        sb.Append(Reset);
    }

    private static void AppendLabel(StringBuilder sb, string label, bool color)
    {
        sb.Append("  ");
        AppendStyled(sb, label, LabelStyle, color);
        sb.AppendLine();
    }

    private static void AppendCode(StringBuilder sb, string text, string path, bool color, ref ReviewHighlighter.State state)
    {
        if (!color)
        {
            sb.Append(text);
            return;
        }

        ReviewHighlighter.Append(sb, text, path, ref state);
    }

    private static void AppendSeverity(StringBuilder sb, string severity, bool color)
    {
        var tint = color ? SeverityStyle(severity) : null;
        if (tint is null)
        {
            sb.Append(severity);
            return;
        }

        sb.Append(tint);
        sb.Append(severity);
        sb.Append(Reset);
    }

    private static string? SeverityStyle(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" or "high" => CriticalOrHigh,
        "medium" => Medium,
        "low" => Low,
        _ => null
    };

    private static void AppendSnippet(
        StringBuilder sb,
        ReviewComment comment,
        bool color,
        ReviewCodeIndex index,
        string? workingDirectory,
        string? revision,
        ref string? repoRoot)
    {
        var found = index.TryGet(comment.Path, comment.StartLine, comment.EndLine, out var lines, out var omitted);
        if (!found && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            found = ReviewCodeIndex.TryReadFallback(
                workingDirectory,
                revision,
                ref repoRoot,
                comment.Path,
                comment.StartLine,
                comment.EndLine,
                out lines,
                out omitted);
        }

        if (!found || lines.Count == 0)
            return;

        AppendLabel(sb, "Code:", color);
        var width = 1;
        foreach (var line in lines)
            width = Math.Max(width, line.Number.ToString(CultureInfo.InvariantCulture).Length);

        var code = default(ReviewHighlighter.State);
        foreach (var line in lines)
        {
            sb.Append("    ");
            var number = line.Number.ToString(CultureInfo.InvariantCulture).PadLeft(width);
            AppendGutter(sb, number, color);
            AppendCode(sb, line.Text, comment.Path, color, ref code);
            sb.AppendLine();
        }

        if (omitted > 0 && CoversPrintedWindow(lines, comment.StartLine))
        {
            sb.Append("    ");
            var more = omitted == 1 ? " more line" : " more lines";
            if (color)
                sb.Append(GutterStyle);
            sb.Append("... ");
            sb.Append(omitted.ToString(CultureInfo.InvariantCulture));
            sb.Append(more);
            if (color)
                sb.Append(Reset);
            sb.AppendLine();
        }
    }

    private static void AppendGutter(StringBuilder sb, string number, bool color)
    {
        if (!color)
        {
            sb.Append(number);
            sb.Append(" | ");
            return;
        }

        sb.Append(GutterStyle);
        sb.Append(number);
        sb.Append(" | ");
        sb.Append(Reset);
    }

    private static bool CoversPrintedWindow(List<ReviewSnippetLine> lines, int start)
    {
        if (lines.Count != ReviewCodeIndex.MaxLines)
            return false;

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Number != start + i)
                return false;
        }

        return true;
    }
}
