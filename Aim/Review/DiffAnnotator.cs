using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aim;

/// <summary>
/// Prefixes each kept or added line with its line number on the new file.
/// Callers keep the numbers the model reports. This tool does not relocate
/// comments onto the working tree afterward.
/// </summary>
internal static partial class DiffAnnotator
{
    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeaderRegex();

    public static string Annotate(string diff)
    {
        if (string.IsNullOrEmpty(diff))
            return diff;

        var normalized = diff.Replace("\r\n", "\n", StringComparison.Ordinal);
        var trailingNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n');
        if (trailingNewline && lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];

        var sb = new StringBuilder(normalized.Length + lines.Length * 8);
        var inHunk = false;
        var newLine = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                sb.Append('\n');

            var line = lines[i];
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunk = false;
                sb.Append(line);
                continue;
            }

            var hunk = HunkHeaderRegex().Match(line);
            if (hunk.Success)
            {
                inHunk = true;
                newLine = int.Parse(hunk.Groups[1].Value, CultureInfo.InvariantCulture);
                sb.Append(line);
                continue;
            }

            if (!inHunk)
            {
                sb.Append(line);
                continue;
            }

            if (line.Length == 0)
            {
                AppendNumbered(sb, newLine, "");
                newLine++;
                continue;
            }

            if (line[0] == '\\')
            {
                sb.Append(line);
                continue;
            }

            if (line[0] == '-')
            {
                sb.Append("       | ");
                sb.Append(line);
                continue;
            }

            if (line[0] is '+' or ' ')
            {
                AppendNumbered(sb, newLine, line);
                newLine++;
                continue;
            }

            sb.Append(line);
        }

        if (trailingNewline)
            sb.Append('\n');

        return sb.ToString();
    }

    private static void AppendNumbered(StringBuilder sb, int newLine, string line)
    {
        sb.Append(newLine.ToString(CultureInfo.InvariantCulture).PadLeft(6));
        sb.Append(" | ");
        sb.Append(line);
    }
}
