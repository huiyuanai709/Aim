namespace Ngrep.Output;

internal enum ColorChoice
{
    Auto,
    Always,
    Never,
}

internal static class TextColor
{
    public const string Reset = "\u001b[0m";
    public const string Path = "\u001b[35m";
    public const string Line = "\u001b[32m";
    public const string Column = "\u001b[34m";
    public const string Match = "\u001b[1;31m";

    public static ColorChoice Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return ColorChoice.Auto;
        }

        return value.ToLowerInvariant() switch
        {
            "auto" => ColorChoice.Auto,
            "always" => ColorChoice.Always,
            "never" => ColorChoice.Never,
            _ => throw new ArgumentException($"Unknown --color '{value}'. Expected auto, always, or never."),
        };
    }

    public static bool Enabled(ColorChoice choice)
    {
        return choice switch
        {
            ColorChoice.Always => true,
            ColorChoice.Never => false,
            _ => AutoDetect(),
        };
    }

    /// <summary>
    /// Stdout is a terminal. <c>TERM=dumb</c>, a redirect, and a <see cref="StringWriter"/> are not.
    /// <c>NO_COLOR</c> does not affect this; it only turns color off.
    /// </summary>
    public static bool IsTerminal()
    {
        var term = Environment.GetEnvironmentVariable("TERM");
        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !Console.IsOutputRedirected && Console.Out is not StringWriter;
    }

    private static bool AutoDetect()
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR");
        if (!string.IsNullOrEmpty(noColor))
        {
            return false;
        }

        return IsTerminal();
    }
}
