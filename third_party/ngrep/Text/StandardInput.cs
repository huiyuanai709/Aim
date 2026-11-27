namespace Ngrep.Text;

/// <summary>
/// Process stdin for a content search. <c>-f -</c> still reads patterns through
/// <see cref="Console.In"/> and does not use this stream.
/// </summary>
internal static class StandardInput
{
    /// <summary>Path text for matches read from stdin. Same label as GNU grep; ripgrep prints <c>&lt;stdin&gt;</c>.</summary>
    public const string DisplayPath = "(standard input)";

    private static Func<Stream>? _open;

    private static bool? _redirected;

    /// <summary>Test hook. Replaces <see cref="Console.OpenStandardInput"/>.</summary>
    internal static void SetOpen(Func<Stream>? open) => _open = open;

    /// <summary>
    /// Test hook. <see cref="Console.IsInputRedirected"/> follows the OS handle, not
    /// <see cref="Console.SetIn"/>, so tests set the heuristic directly.
    /// </summary>
    internal static void SetRedirected(bool? redirected) => _redirected = redirected;

    public static Stream Open() => _open != null ? _open() : Console.OpenStandardInput();

    /// <summary>True when stdin is a pipe or redirect, so a missing path searches stdin.</summary>
    public static bool IsRedirected => _redirected ?? Console.IsInputRedirected;
}
