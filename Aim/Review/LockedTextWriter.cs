using System.Text;

namespace Aim;

internal sealed class LockedTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly object _gate = new();

    public LockedTextWriter(TextWriter inner) => _inner = inner;

    public override Encoding Encoding => _inner.Encoding;

    public override void Flush()
    {
        lock (_gate)
            _inner.Flush();
    }

    public override void Write(char value)
    {
        lock (_gate)
            _inner.Write(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
            _inner.Write(buffer, index, count);
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
            _inner.Write(buffer);
    }

    public override void Write(string? value)
    {
        lock (_gate)
            _inner.Write(value);
    }

    public override void WriteLine()
    {
        lock (_gate)
            _inner.WriteLine();
    }

    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
            _inner.WriteLine(buffer);
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
            _inner.WriteLine(value);
    }
}
