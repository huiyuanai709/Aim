using System.Text;

namespace Ngrep.Output;

/// <summary>
/// How stdout is buffered. A terminal flushes every write so a match line is visible
/// before the next line is formatted. A pipe or file keeps a 64 KiB block and does not
/// auto-flush; that block is why redirected search stays in large writes.
/// </summary>
internal readonly record struct StdoutPolicy(int BufferSize, bool AutoFlush)
{
    public const int BlockBufferSize = 64 * 1024;

    /// <summary>
    /// Same size the runtime uses for a console writer. <see cref="AutoFlush"/> is what
    /// makes a short line leave the process; the buffer only holds a write that has not
    /// finished encoding.
    /// </summary>
    public const int TerminalBufferSize = 256;

    public static StdoutPolicy For(bool isTerminal) =>
        isTerminal
            ? new StdoutPolicy(TerminalBufferSize, AutoFlush: true)
            : new StdoutPolicy(BlockBufferSize, AutoFlush: false);
}

/// <summary>
/// Installs the process stdout writer. <see cref="Cli.NgrepCli.Run"/> does not call
/// <see cref="Install"/>, so a host or a test that replaced <see cref="Console.Out"/>
/// keeps its writer. The default <see cref="Active"/> policy is the block buffer, which
/// matches that host: line flushing turns on only when <see cref="Install"/> sees a terminal.
/// </summary>
internal static class Stdout
{
    private static bool _installed;
    private static Stream? _raw;

    /// <summary>
    /// Policy selected by <see cref="Install"/>, or the block buffer until then.
    /// Match output reads <see cref="StdoutPolicy.AutoFlush"/> so a terminal does not
    /// also sit in the per-file text buffer.
    /// </summary>
    internal static StdoutPolicy Active { get; private set; } = StdoutPolicy.For(isTerminal: false);

    /// <summary>True when raw UTF-8 bytes can be written without disturbing buffered text.</summary>
    public static bool CanWriteRaw => _installed;

    internal static void Use(StdoutPolicy policy) => Active = policy;

    /// <summary>
    /// Writes bytes that are already UTF-8. Buffered text is flushed first so the ordering is
    /// the same as if everything had gone through the writer. A host that replaced
    /// <see cref="Console.Out"/> (a test, an embedder) gets the decoded string instead.
    /// </summary>
    public static void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (!_installed)
        {
            Console.Out.Write(Encoding.UTF8.GetString(bytes));
            return;
        }

        Flush();
        _raw ??= Console.OpenStandardOutput();
        _raw.Write(bytes);
        if (!Active.AutoFlush)
        {
            return;
        }

        try
        {
            // The console stream is normally unbuffered. Flush anyway so a line cannot sit
            // in a runtime buffer after the writer was asked to show it.
            _raw.Flush();
        }
        catch (IOException)
        {
            // A closed pipe (grep -q, head) is not a search failure.
        }
    }

    /// <summary>
    /// Replaces <see cref="Console.Out"/>. Call after the output encoding is set, because
    /// setting the encoding rebuilds the console writer on a terminal. A terminal flushes
    /// each write. A redirect keeps the 64 KiB block. Flushes on process exit so a
    /// buffered tail is not lost.
    /// </summary>
    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        Use(StdoutPolicy.For(!Console.IsOutputRedirected));
        Console.SetOut(CreateWriter(Console.OpenStandardOutput(), Active));
        _installed = true;
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Flush();
    }

    internal static StreamWriter CreateWriter(Stream stream, StdoutPolicy policy) =>
        new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), policy.BufferSize)
        {
            AutoFlush = policy.AutoFlush,
        };

    public static void Flush()
    {
        if (!_installed)
        {
            return;
        }

        try
        {
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // A closed pipe (grep -q, head) is not a search failure.
        }
    }
}
