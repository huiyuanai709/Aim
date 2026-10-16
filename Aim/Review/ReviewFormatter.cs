using System.Text;
using System.Text.Json;

namespace Aim;

internal static class ReviewFormatter
{
    public static string ToJson(IReadOnlyList<ReviewComment> comments)
    {
        var list = comments as List<ReviewComment> ?? comments.ToList();
        return JsonSerializer.Serialize(list, ReviewJsonContext.Default.ListReviewComment);
    }

    public static string ToText(
        IReadOnlyList<ReviewComment> comments,
        bool diffTruncated,
        bool nameOnly,
        bool partial = false)
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

        var sb = new StringBuilder();
        for (var i = 0; i < comments.Count; i++)
        {
            if (i > 0)
                sb.AppendLine();

            AppendComment(sb, comments[i]);
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

    private static void AppendComment(StringBuilder sb, ReviewComment comment)
    {
        var lines = comment.StartLine == comment.EndLine
            ? comment.StartLine.ToString()
            : $"{comment.StartLine}-{comment.EndLine}";
        sb.AppendLine($"{comment.Path}:{lines}  {comment.Severity}  {comment.Category}");

        foreach (var line in comment.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            sb.AppendLine($"  {line}");

        if (string.IsNullOrWhiteSpace(comment.SuggestionCode))
            return;

        sb.AppendLine("  Suggestion:");
        foreach (var line in comment.SuggestionCode.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            sb.AppendLine($"    {line}");
    }
}
