namespace Aim;

/// <summary>
/// Whether text written to stdout should include ANSI color.
/// <c>NO_COLOR</c> (any non-empty value) turns color off.
/// <c>FORCE_COLOR</c> set to 0, false, or off also turns color off.
/// <c>FORCE_COLOR</c> set to empty, 1, 2, 3, true, or on turns color on even when stdout is redirected.
/// Otherwise color is on only for an interactive terminal that is not <c>TERM=dumb</c>.
/// </summary>
internal static class TerminalColor
{
    public static bool Enabled => Decide(
        Environment.GetEnvironmentVariable("NO_COLOR"),
        Environment.GetEnvironmentVariable("FORCE_COLOR"),
        IsOutputRedirected(),
        Environment.GetEnvironmentVariable("TERM"));

    internal static bool Decide(string? noColor, string? forceColor, bool outputRedirected, string? term)
    {
        if (!string.IsNullOrEmpty(noColor))
            return false;

        var forced = Force(forceColor);
        if (forced is false)
            return false;
        if (forced is true)
            return true;

        if (outputRedirected)
            return false;

        if (term is not null && term.Equals("dumb", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool? Force(string? forceColor)
    {
        if (forceColor is null)
            return null;

        var value = forceColor.Trim();
        if (value.Length == 0)
            return true;

        if (value.Equals("0", StringComparison.Ordinal) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return false;

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
            return true;

        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var level))
            return level > 0;

        return null;
    }

    private static bool IsOutputRedirected()
    {
        try
        {
            return Console.IsOutputRedirected;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
