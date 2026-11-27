namespace Aim.Tests;

internal static class CliCapture
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static Capture Hold()
    {
        Gate.Wait();
        return new Capture();
    }

    public sealed class Capture : IDisposable
    {
        private readonly TextWriter _out;
        private readonly TextWriter _error;
        private bool _disposed;

        public StringWriter Stdout { get; } = new();
        public StringWriter Stderr { get; } = new();

        public Capture()
        {
            _out = Console.Out;
            _error = Console.Error;
            Console.SetOut(Stdout);
            Console.SetError(Stderr);
        }

        public void Clear()
        {
            Stdout.GetStringBuilder().Clear();
            Stderr.GetStringBuilder().Clear();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Console.SetOut(_out);
            Console.SetError(_error);
            Gate.Release();
        }
    }
}
