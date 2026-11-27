using System.Globalization;
using System.Text;
using Ngrep.Search;

namespace Ngrep.Output;

/// <summary>
/// Text-mode prefixes. JSON, NDJSON, and rgjson do not use this.
/// Column and line number are independent. <c>--vimgrep</c> turns both on before this runs.
/// <c>-H</c> / <c>-I</c> decide whether a path is printed at all. <c>-0</c> replaces the separator
/// or newline that would have followed a path, and is absent when the path is omitted.
/// </summary>
internal static class TextLayout
{
    public static void WriteMatch(SearchMatch match, string body, SearchPresentation presentation, bool includePath, bool colorizeBody, StringBuilder? sink = null)
    {
        if (colorizeBody && presentation.Color)
        {
            body = presentation.OnlyMatching
                ? TextColor.Match + body + TextColor.Reset
                : Highlight(match.LineText, match.Column, match.Text);
        }

        WriteRecord(
            includePath ? match.Path : null,
            presentation.LineNumbers ? match.Line : null,
            presentation.Column ? match.Column : null,
            body,
            presentation.Color,
            presentation.NullPaths,
            context: false,
            sink);
    }

    public static void WriteContext(string path, int line, string text, SearchPresentation presentation, bool includePath, StringBuilder? sink = null)
    {
        WriteRecord(
            includePath ? path : null,
            presentation.LineNumbers ? line : null,
            column: null,
            text,
            presentation.Color,
            presentation.NullPaths,
            context: true,
            sink);
    }

    public static void WriteHeading(string path, SearchPresentation presentation)
    {
        WritePaint(path, presentation.Color ? TextColor.Path : null);
        if (presentation.NullPaths)
        {
            Console.Write('\0');
            return;
        }

        Console.WriteLine();
    }

    public static void WriteFilePath(string path, SearchPresentation presentation)
    {
        WritePaint(path, presentation.Color ? TextColor.Path : null);
        if (presentation.NullPaths)
        {
            Console.Write('\0');
            return;
        }

        Console.WriteLine();
    }

    public static void WriteCount(string path, int count, SearchPresentation presentation)
    {
        var text = count.ToString(CultureInfo.InvariantCulture);
        if (!presentation.WithFilename)
        {
            WritePaint(text, presentation.Color ? TextColor.Line : null);
            Console.WriteLine();
            return;
        }

        WritePaint(path, presentation.Color ? TextColor.Path : null);
        Console.Write(presentation.NullPaths ? '\0' : ':');
        WritePaint(text, presentation.Color ? TextColor.Line : null);
        Console.WriteLine();
    }

    [ThreadStatic]
    private static StringBuilder? _record;

    private static void WriteRecord(
        string? path,
        int? line,
        int? column,
        string body,
        bool color,
        bool nullPath,
        bool context,
        StringBuilder? sink)
    {
        var separator = context ? "-" : ":";
        // A record is a path, separators, and the body, then a newline. Console.Write locks
        // the synchronized writer and encodes on every call, so a dense search that prints
        // one line per match spent most of its time in ~6 calls per record. Build the record
        // once and hand the writer a single buffer instead. A caller that passes a sink
        // batches a whole file and writes it in bulk.
        var record = sink ?? (_record ??= new StringBuilder(256));
        if (sink == null)
        {
            record.Clear();
        }
        var pending = false;
        var afterPath = false;

        void Break()
        {
            if (!pending)
            {
                return;
            }

            record.Append(afterPath && nullPath ? "\0" : separator);
            pending = false;
            afterPath = false;
        }

        if (path != null)
        {
            Paint(record, path, color ? TextColor.Path : null);
            pending = true;
            afterPath = true;
        }

        if (line != null)
        {
            Break();
            Paint(record, line.Value.ToString(CultureInfo.InvariantCulture), color ? TextColor.Line : null);
            pending = true;
        }

        if (column != null)
        {
            Break();
            Paint(record, column.Value.ToString(CultureInfo.InvariantCulture), color ? TextColor.Column : null);
            pending = true;
        }

        Break();
        record.Append(body);
        record.Append(Console.Out.NewLine);
        if (sink == null)
        {
            Console.Out.Write(record);
        }
    }

    private static void Paint(StringBuilder target, string text, string? color)
    {
        if (color == null)
        {
            target.Append(text);
            return;
        }

        target.Append(color).Append(text).Append(TextColor.Reset);
    }

    /// <summary>
    /// Appends one plain text record whose body is copied as UTF-8 bytes. No color, no
    /// replacement: the separator layout matches <see cref="WriteRecord"/>, so the printed
    /// bytes are the same as the string path would produce.
    /// </summary>
    public static void WriteRawRecord(
        System.Buffers.IBufferWriter<byte> sink,
        ReadOnlySpan<byte> body,
        string? path,
        int? line,
        int? column,
        bool nullPath)
    {
        var pending = false;
        var afterPath = false;

        void Break()
        {
            if (!pending)
            {
                return;
            }

            Append(sink, afterPath && nullPath ? "\0" : ":");
            pending = false;
            afterPath = false;
        }

        if (path != null)
        {
            Append(sink, path);
            pending = true;
            afterPath = true;
        }

        if (line != null)
        {
            Break();
            Append(sink, line.Value.ToString(CultureInfo.InvariantCulture));
            pending = true;
        }

        if (column != null)
        {
            Break();
            Append(sink, column.Value.ToString(CultureInfo.InvariantCulture));
            pending = true;
        }

        Break();
        var span = sink.GetSpan(body.Length);
        body.CopyTo(span);
        sink.Advance(body.Length);
        Append(sink, Environment.NewLine);
    }

    private static void Append(System.Buffers.IBufferWriter<byte> sink, string text)
    {
        var span = sink.GetSpan(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            span[i] = (byte)text[i];
        }

        sink.Advance(text.Length);
    }

    private static void WritePaint(string text, string? color)
    {
        if (color == null)
        {
            Console.Write(text);
            return;
        }

        Console.Write(color);
        Console.Write(text);
        Console.Write(TextColor.Reset);
    }

    private static string Highlight(string lineText, int column, string matchText)
    {
        var start = column - 1;
        if (start < 0 || start > lineText.Length)
        {
            return lineText;
        }

        var length = matchText.Length;
        var newline = matchText.IndexOf('\n');
        if (newline >= 0)
        {
            length = newline;
            if (length > 0 && matchText[length - 1] == '\r')
            {
                length--;
            }
        }

        if (start + length > lineText.Length)
        {
            length = lineText.Length - start;
        }

        if (length <= 0 || !lineText.AsSpan(start, length).SequenceEqual(matchText.AsSpan(0, length)))
        {
            return lineText;
        }

        return string.Concat(
            lineText[..start],
            TextColor.Match,
            lineText.Substring(start, length),
            TextColor.Reset,
            lineText[(start + length)..]);
    }
}
