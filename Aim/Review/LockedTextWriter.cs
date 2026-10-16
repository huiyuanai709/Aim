using System.Text;

namespace Aim;

internal sealed class LockedTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly object _gate = new();

    public LockedTextWriter(TextWriter inner) => _inner = inner;

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value)
    {
        lock (_gate)
            _inner.Write(value);
    }

    public override void Write(string? value)
    {
        lock (_gate)
            _inner.Write(value);
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
            _inner.WriteLine(value);
    }
}
