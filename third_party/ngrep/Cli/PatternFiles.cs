namespace Ngrep.Cli;

/// <summary>
/// Patterns from <c>-f</c> / <c>--file</c>. One pattern per line. The newline is not part of the
/// pattern. An empty line is an empty pattern (match every line). A line starting with <c>#</c>
/// is that pattern, not a comment. <c>\#</c> is kept as written; it is not a comment escape.
/// </summary>
internal static class PatternFiles
{
    public static IReadOnlyList<string> Read(string path)
    {
        string text;
        try
        {
            text = path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Pattern file not found: {path}");
        }

        return Parse(text);
    }

    public static List<string> Parse(string text)
    {
        var patterns = new List<string>();
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > 0 && normalized[0] == '\uFEFF')
        {
            normalized = normalized[1..];
        }

        if (normalized.Length == 0)
        {
            return patterns;
        }

        var lines = normalized.Split('\n');
        var count = normalized[^1] == '\n' ? lines.Length - 1 : lines.Length;
        for (var i = 0; i < count; i++)
        {
            patterns.Add(lines[i]);
        }

        return patterns;
    }
}
