using System.Buffers;

namespace Ngrep.Search;

/// <summary>
/// Plain text rows built straight from file bytes: one entry per printed line plus the
/// copied row bodies. The writer prints these bytes, so no line is decoded or encoded again.
/// </summary>
internal sealed class RawSink
{
    public ArrayBufferWriter<byte> Buffer { get; } = new(1 << 16);

    public List<RawLine> Rows { get; } = new(8);

    /// <summary>The row bodies, set once the file is finished.</summary>
    public byte[]? Body { get; private set; }

    public void Clear()
    {
        Rows.Clear();
        Buffer.Clear();
        Body = null;
    }

    public void Add(ReadOnlySpan<byte> line, int lineNumber, int column)
    {
        var offset = Buffer.WrittenCount;
        line.CopyTo(Buffer.GetSpan(line.Length));
        Buffer.Advance(line.Length);
        Rows.Add(new RawLine(lineNumber, column, offset, line.Length));
    }

    public void Complete() => Body = Buffer.WrittenSpan.ToArray();
}
