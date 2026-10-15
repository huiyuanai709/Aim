using System.Text;

namespace Aim;

internal readonly record struct DiffLimitResult(string Text, bool Truncated, int OmittedFiles);

internal readonly record struct DiffFitResult(
    string Text,
    bool Truncated,
    int OmittedFiles,
    bool NeedsNameOnlyFallback);

internal static class DiffLimiter
{
    public static DiffLimitResult Limit(string diff, int maxChars)
    {
        if (maxChars <= 0 || diff.Length <= maxChars)
            return new DiffLimitResult(diff, false, 0);

        var parts = SplitPatches(diff);
        var sb = new StringBuilder();
        var kept = 0;

        foreach (var part in parts)
        {
            var extra = sb.Length == 0 ? part.Length : part.Length + 1;
            if (sb.Length + extra <= maxChars)
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(part);
                kept++;
                continue;
            }

            if (kept == 0)
                return new DiffLimitResult(CutLines(part, maxChars), true, Math.Max(0, parts.Count - 1));

            return new DiffLimitResult(sb.ToString(), true, parts.Count - kept);
        }

        return new DiffLimitResult(sb.ToString(), false, 0);
    }

    /// <summary>
    /// Keeps a unified diff inside <paramref name="maxChars"/>. When the budget
    /// cannot hold a hunk, <see cref="DiffFitResult.NeedsNameOnlyFallback"/> is set
    /// so the caller can send file names instead.
    /// </summary>
    public static DiffFitResult Fit(string diff, int maxChars)
    {
        var limited = Limit(diff, maxChars);
        if (!limited.Truncated)
            return new DiffFitResult(limited.Text, false, 0, false);

        var usable = limited.Text.Contains("@@", StringComparison.Ordinal);
        return new DiffFitResult(limited.Text, true, limited.OmittedFiles, !usable);
    }

    private static List<string> SplitPatches(string diff)
    {
        var normalized = diff.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var parts = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && current.Length > 0)
            {
                parts.Add(current.ToString().TrimEnd('\n'));
                current.Clear();
            }

            current.Append(line);
            current.Append('\n');
        }

        if (current.Length > 0)
            parts.Add(current.ToString().TrimEnd('\n'));

        return parts;
    }

    private static string CutLines(string text, int maxChars)
    {
        if (maxChars <= 0)
            return "";

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Length <= maxChars)
            return normalized;

        var sb = new StringBuilder();
        foreach (var line in normalized.Split('\n'))
        {
            var extra = sb.Length == 0 ? line.Length : line.Length + 1;
            if (sb.Length + extra > maxChars)
                break;

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
        }

        if (sb.Length == 0)
            return normalized[..maxChars];

        return sb.ToString();
    }
}
