using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ngrep.Text;

internal static class TextFiles
{
    public const int BinaryProbeLength = 8192;
    public const long MaxFileBytes = 32L * 1024L * 1024L;

    /// <summary>At this size a warm file is cheaper to map than to copy into a pooled buffer.</summary>
    public const int MmapThreshold = 256 * 1024;

    public static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        var n = Math.Min(bytes.Length, BinaryProbeLength);
        return bytes[..n].Contains((byte)0);
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var bom = Utf8BomLength(bytes);
        return Encoding.UTF8.GetString(bytes[bom..]);
    }

    /// <summary>UTF-8 byte order mark length, or 0. <see cref="Decode"/> drops it and so does the byte path.</summary>
    public static int Utf8BomLength(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

    /// <summary>
    /// 1-based character column of <paramref name="index"/> on a line that starts at
    /// <paramref name="lineStart"/>. A byte offset is a character offset on ASCII, so the
    /// common path does no decoding at all.
    /// </summary>
    public static int Column(ReadOnlySpan<byte> bytes, int lineStart, int index)
    {
        var start = Math.Max(lineStart, Utf8BomLength(bytes));
        if (index <= start)
        {
            return 1;
        }

        return Ascii.IsValid(bytes[start..index])
            ? index - start + 1
            : 1 + Encoding.UTF8.GetCharCount(bytes[start..index]);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Reads file bytes for search. Buffers come from the array pool so a continuation
    /// can resume on another thread. <paramref name="stop"/> is consulted after each chunk,
    /// once the leading bytes have passed the NUL probe. Returning true ends the read before
    /// EOF. A miss still reads the whole file. Files at or above <see cref="MmapThreshold"/>
    /// map once the length is known; mapping has no async form and uses its own handle.
    /// </summary>
    public static ValueTask<PooledBytes> ReadAsync(string path, ReadStop? stop, CancellationToken cancellation) =>
        ReadAsync(path, stop, cancellation, searchBinary: false);

    /// <param name="searchBinary">
    /// When true, a NUL in the first 8 KiB does not skip the file. <c>--binary</c>, <c>-a</c>/<c>--text</c>, and <c>-uuu</c> set this.
    /// The 32 MiB cap still applies.
    /// </param>
    public static ValueTask<PooledBytes> ReadAsync(string path, ReadStop? stop, CancellationToken cancellation, bool searchBinary) =>
        ReadAsync(path, stop, cancellation, 64 * 1024, searchBinary);

    /// <param name="initialBuffer">
    /// First read size. Values below <see cref="BinaryProbeLength"/> are raised so a NUL
    /// in the probe window aborts before the rest of the file is read. Search uses 64 KiB.
    /// Index uses the probe length: tiny files finish in that one read, and binaries stop there.
    /// </param>
    internal static async ValueTask<PooledBytes> ReadAsync(string path, ReadStop? stop, CancellationToken cancellation, int initialBuffer, bool searchBinary = false)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ReadFromAsync(handle, path, stop, cancellation, initialBuffer, searchBinary).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PooledBytes.Unreadable();
        }
        finally
        {
            if (handle != null)
            {
                DisposeReadHandle(handle);
            }
        }
    }

    /// <summary>
    /// Closes a handle opened with <see cref="FileOptions.Asynchronous"/>.
    /// On Windows that handle is bound to the thread pool at open. Disposing the
    /// <see cref="SafeFileHandle"/> alone calls CloseHandle and then frees the cached
    /// overlapped (which clears its completion callback) while the binding can still
    /// deliver a packet. The packet runs on a pool thread,
    /// <c>Win32ThreadPoolNativeOverlapped.CompleteWithCallback</c> calls a null callback,
    /// and that <see cref="NullReferenceException"/> is unhandled, so the process dies.
    /// <see cref="FileStream"/> disposes the binding before the handle. Mapping must not
    /// use this handle either: a section created from an IOCP-bound handle posts
    /// completions that are not our overlapped.
    /// </summary>
    private static void DisposeReadHandle(SafeFileHandle handle)
    {
        if (handle.IsClosed)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                // bufferSize 1 keeps FileStream off its own buffer. The bool is unused by
                // the runtime; the handle's asynchronous flag selects the Windows strategy,
                // whose dispose closes the thread-pool binding first.
                var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: true);
                stream.Dispose();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ObjectDisposedException)
            {
                if (handle.IsClosed)
                {
                    return;
                }
            }
        }

        handle.Dispose();
    }

    /// <summary>
    /// Files smaller than the first buffer are one read and no length syscall.
    /// A full buffer means the file may be larger, so the length is checked then.
    /// </summary>
    private static async ValueTask<PooledBytes> ReadFromAsync(SafeFileHandle handle, string path, ReadStop? stop, CancellationToken cancellation, int initialBuffer, bool searchBinary)
    {
        var first = Math.Max(initialBuffer, BinaryProbeLength);
        byte[]? buffer = null;
        var owned = false;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(first);
            var offset = 0;
            var checkedBinary = false;
            var knownLength = -1L;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (offset == buffer.Length)
                {
                    var lengthLong = knownLength >= 0 ? knownLength : RandomAccess.GetLength(handle);
                    if (lengthLong < 0)
                    {
                        return PooledBytes.Unreadable();
                    }

                    knownLength = lengthLong;
                    if (lengthLong > MaxFileBytes)
                    {
                        return PooledBytes.TooLarge();
                    }

                    if (lengthLong <= offset)
                    {
                        break;
                    }

                    if (lengthLong >= MmapThreshold
                        && lengthLong <= int.MaxValue
                        && TryMap(path, (int)lengthLong, stop, searchBinary, out var mapped))
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            mapped.Dispose();
                            throw new OperationCanceledException(cancellation);
                        }

                        return mapped;
                    }

                    var target = stop == null
                        ? (int)lengthLong
                        : (int)Math.Min(lengthLong, offset + (1L << 20));
                    var grown = ArrayPool<byte>.Shared.Rent(target);
                    buffer.AsSpan(0, offset).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                var want = buffer.Length - offset;
                if (knownLength >= 0)
                {
                    var remain = (int)Math.Min(want, knownLength - offset);
                    want = remain;
                }

                if (want <= 0)
                {
                    break;
                }

                // CancellationToken.None: passing the caller's token makes ReadAsync call
                // CancelIoEx. That completion races the dispose below on Windows and is the
                // same null-callback crash. The token is checked between chunks instead.
                var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(offset, want), offset, CancellationToken.None).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (read == 0)
                {
                    break;
                }

                offset += read;
                var endOfFile = read < buffer.Length - (offset - read) || (knownLength >= 0 && offset >= knownLength);
                if (offset < buffer.Length)
                {
                    endOfFile = true;
                }

                if (!checkedBinary && (offset >= BinaryProbeLength || endOfFile))
                {
                    checkedBinary = true;
                    if (!searchBinary && IsBinary(buffer.AsSpan(0, offset)))
                    {
                        return PooledBytes.Binary();
                    }
                }

                if (stop != null && checkedBinary && stop(buffer.AsSpan(0, offset), endOfFile))
                {
                    cancellation.ThrowIfCancellationRequested();
                    owned = true;
                    return PooledBytes.Ok(buffer, offset);
                }

                if (endOfFile)
                {
                    break;
                }
            }

            if (!checkedBinary && !searchBinary && IsBinary(buffer.AsSpan(0, Math.Min(offset, BinaryProbeLength))))
            {
                return PooledBytes.Binary();
            }

            cancellation.ThrowIfCancellationRequested();
            owned = true;
            return PooledBytes.Ok(buffer, offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PooledBytes.Unreadable();
        }
        finally
        {
            if (!owned && buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Synchronous twin of <see cref="ReadAsync(string, ReadStop?, CancellationToken, int, bool)"/>,
    /// used by search workers. They already own a pool thread, so a blocking read costs one
    /// syscall instead of a dispatch and a continuation, which is most of the per-file
    /// overhead on a tree of small files. The indexer and stdin keep the async form.
    /// </summary>
    public static PooledBytes Read(string path, ReadStop? stop, bool searchBinary, CancellationToken cancellation, int initialBuffer = 128 * 1024)
    {
        SafeFileHandle? handle = null;
        try
        {
            // No FileOptions.Asynchronous: the dispose race that DisposeReadHandle guards
            // against only applies to an IOCP-bound handle.
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            return ReadFrom(handle, path, stop, searchBinary, cancellation, initialBuffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PooledBytes.Unreadable();
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static PooledBytes ReadFrom(SafeFileHandle handle, string path, ReadStop? stop, bool searchBinary, CancellationToken cancellation, int initialBuffer)
    {
        var first = Math.Max(initialBuffer, BinaryProbeLength);
        byte[]? buffer = null;
        var owned = false;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(first);
            var offset = 0;
            var checkedBinary = false;
            var knownLength = -1L;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (offset == buffer.Length)
                {
                    var lengthLong = knownLength >= 0 ? knownLength : RandomAccess.GetLength(handle);
                    if (lengthLong < 0)
                    {
                        return PooledBytes.Unreadable();
                    }

                    knownLength = lengthLong;
                    if (lengthLong > MaxFileBytes)
                    {
                        return PooledBytes.TooLarge();
                    }

                    if (lengthLong <= offset)
                    {
                        break;
                    }

                    if (lengthLong >= MmapThreshold
                        && lengthLong <= int.MaxValue
                        && TryMap(path, (int)lengthLong, stop, searchBinary, out var mapped))
                    {
                        return mapped;
                    }

                    var target = stop == null
                        ? (int)lengthLong
                        : (int)Math.Min(lengthLong, offset + (1L << 20));
                    var grown = ArrayPool<byte>.Shared.Rent(target);
                    buffer.AsSpan(0, offset).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                var want = buffer.Length - offset;
                if (knownLength >= 0)
                {
                    want = (int)Math.Min(want, knownLength - offset);
                }

                if (want <= 0)
                {
                    break;
                }

                var read = RandomAccess.Read(handle, buffer.AsSpan(offset, want), offset);
                if (read == 0)
                {
                    break;
                }

                offset += read;
                var endOfFile = read < buffer.Length - (offset - read) || (knownLength >= 0 && offset >= knownLength);
                if (offset < buffer.Length)
                {
                    endOfFile = true;
                }

                if (!checkedBinary && (offset >= BinaryProbeLength || endOfFile))
                {
                    checkedBinary = true;
                    if (!searchBinary && IsBinary(buffer.AsSpan(0, offset)))
                    {
                        return PooledBytes.Binary();
                    }
                }

                if (stop != null && checkedBinary && stop(buffer.AsSpan(0, offset), endOfFile))
                {
                    owned = true;
                    return PooledBytes.Ok(buffer, offset);
                }

                if (endOfFile)
                {
                    break;
                }
            }

            if (!checkedBinary && !searchBinary && IsBinary(buffer.AsSpan(0, Math.Min(offset, BinaryProbeLength))))
            {
                return PooledBytes.Binary();
            }

            owned = true;
            return PooledBytes.Ok(buffer, offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PooledBytes.Unreadable();
        }
        finally
        {
            if (!owned && buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Reads stdin (or another stream) with the same NUL probe, early stop, and 32 MiB cap as a file.
    /// A stream has no length, so the cap is a byte past <paramref name="maxBytes"/> rather than a stat.
    /// Nothing is mapped. The stream is left open; process stdin must stay open for the rest of the process.
    /// </summary>
    internal static ValueTask<PooledBytes> ReadStreamAsync(Stream stream, ReadStop? stop, CancellationToken cancellation) =>
        ReadStreamAsync(stream, stop, cancellation, MaxFileBytes, searchBinary: false);

    internal static ValueTask<PooledBytes> ReadStreamAsync(Stream stream, ReadStop? stop, CancellationToken cancellation, bool searchBinary) =>
        ReadStreamAsync(stream, stop, cancellation, MaxFileBytes, searchBinary);

    internal static async ValueTask<PooledBytes> ReadStreamAsync(Stream stream, ReadStop? stop, CancellationToken cancellation, long maxBytes, bool searchBinary = false)
    {
        if (maxBytes < 1 || maxBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        var cap = (int)maxBytes;
        byte[]? buffer = null;
        var owned = false;
        try
        {
            var first = Math.Min(cap, Math.Max(64 * 1024, BinaryProbeLength));
            buffer = ArrayPool<byte>.Shared.Rent(first);
            var offset = 0;
            var checkedBinary = false;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var room = Math.Min(buffer.Length, cap) - offset;
                if (room <= 0)
                {
                    if (offset >= cap)
                    {
                        // One byte past the cap is too large. An exact cap is still text.
                        var extra = new byte[1];
                        var more = await stream.ReadAsync(extra.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        if (more > 0)
                        {
                            return PooledBytes.TooLarge();
                        }

                        break;
                    }

                    var doubled = offset > cap / 2 ? cap : offset * 2;
                    var target = stop == null
                        ? Math.Min(cap, Math.Max(offset + 1, doubled))
                        : Math.Min(cap, offset + (1 << 20));
                    if (target <= offset)
                    {
                        target = Math.Min(cap, offset + 1);
                    }

                    var grown = ArrayPool<byte>.Shared.Rent(target);
                    buffer.AsSpan(0, offset).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                    continue;
                }

                // CancellationToken.None matches the file reader: the token is checked between chunks.
                var read = await stream.ReadAsync(buffer.AsMemory(offset, room), CancellationToken.None).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (read == 0)
                {
                    break;
                }

                offset += read;
                if (!checkedBinary && offset >= Math.Min(BinaryProbeLength, cap))
                {
                    checkedBinary = true;
                    if (!searchBinary && IsBinary(buffer.AsSpan(0, offset)))
                    {
                        return PooledBytes.Binary();
                    }
                }

                if (stop != null && checkedBinary && stop(buffer.AsSpan(0, offset), endOfFile: false))
                {
                    cancellation.ThrowIfCancellationRequested();
                    owned = true;
                    return PooledBytes.Ok(buffer, offset);
                }
            }

            if (!checkedBinary && !searchBinary && offset > 0 && IsBinary(buffer.AsSpan(0, Math.Min(offset, BinaryProbeLength))))
            {
                return PooledBytes.Binary();
            }

            stop?.Invoke(buffer.AsSpan(0, offset), endOfFile: true);
            cancellation.ThrowIfCancellationRequested();
            owned = true;
            return PooledBytes.Ok(buffer, offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return PooledBytes.Unreadable();
        }
        finally
        {
            if (!owned && buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static bool TryMap(string path, int length, ReadStop? stop, bool searchBinary, out PooledBytes mapped)
    {
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        var acquired = false;
        try
        {
            // Own handle, opened without FILE_FLAG_OVERLAPPED. The read handle is already
            // bound to the Windows thread pool; mapping that handle and then closing it
            // delivers completions CompleteWithCallback cannot survive.
            file = MemoryMappedFile.CreateFromFile(
                path,
                FileMode.Open,
                mapName: null,
                capacity: length,
                access: MemoryMappedFileAccess.Read);
            view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            unsafe
            {
                byte* pointer = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                acquired = true;
                var span = new ReadOnlySpan<byte>(pointer, length);
                if (!searchBinary && IsBinary(span))
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                    acquired = false;
                    view.Dispose();
                    file.Dispose();
                    mapped = PooledBytes.Binary();
                    return true;
                }

                stop?.Invoke(span, true);
                mapped = PooledBytes.Mapped(file, view, pointer, length);
                file = null;
                view = null;
                acquired = false;
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (acquired && view != null)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            view?.Dispose();
            file?.Dispose();
            mapped = null!;
            return false;
        }
    }
}

internal delegate bool ReadStop(ReadOnlySpan<byte> contents, bool endOfFile);

internal enum ReadKind
{
    Ok,
    TooLarge,
    Binary,
    Unreadable,
}

internal sealed class PooledBytes : IDisposable
{
    private byte[]? _buffer;
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private unsafe byte* _pointer;
    private int _held;

    private PooledBytes(ReadKind kind, byte[]? buffer, int length)
    {
        Kind = kind;
        _buffer = buffer;
        Length = length;
    }

    public ReadKind Kind { get; }

    public int Length { get; }

    public ReadOnlySpan<byte> Span
    {
        get
        {
            if (_buffer != null)
            {
                return _buffer.AsSpan(0, Length);
            }

            unsafe
            {
                if (_pointer == null || _held == 0)
                {
                    return [];
                }

                return new ReadOnlySpan<byte>(_pointer, Length);
            }
        }
    }

    public static PooledBytes Ok(byte[]? buffer, int length) => new(ReadKind.Ok, buffer, length);

    public static unsafe PooledBytes Mapped(MemoryMappedFile map, MemoryMappedViewAccessor view, byte* pointer, int length)
    {
        var pooled = new PooledBytes(ReadKind.Ok, null, length)
        {
            _map = map,
            _view = view,
            _pointer = pointer,
            _held = 1,
        };
        return pooled;
    }

    public static PooledBytes TooLarge() => new(ReadKind.TooLarge, null, 0);

    public static PooledBytes Binary() => new(ReadKind.Binary, null, 0);

    public static PooledBytes Unreadable() => new(ReadKind.Unreadable, null, 0);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer != null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (Interlocked.Exchange(ref _held, 0) == 1 && _view != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            unsafe
            {
                _pointer = null;
            }
        }

        _view?.Dispose();
        _view = null;
        _map?.Dispose();
        _map = null;
    }
}

internal static class LineMap
{
    public static int[] Build(string text)
    {
        var starts = new List<int> { 0 };
        SimdNewline.Append(text, starts);
        return starts.ToArray();
    }

    /// <summary>
    /// Real lines in <paramref name="text"/>. A trailing newline does not create an extra empty line.
    /// An empty file has zero lines.
    /// </summary>
    public static int LineCount(string text, int[] lineStarts)
    {
        if (text.Length == 0 || lineStarts.Length == 0)
        {
            return 0;
        }

        if (text[^1] == '\n')
        {
            return lineStarts.Length - 1;
        }

        return lineStarts.Length;
    }

    public static (int Line, int Column) Map(int[] lineStarts, int index)
    {
        if (lineStarts.Length == 0)
        {
            return (1, index + 1);
        }

        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lineStarts[mid] <= index)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var lineIndex = Math.Max(0, hi);
        return (lineIndex + 1, index - lineStarts[lineIndex] + 1);
    }

    public static string LineText(string text, int[] lineStarts, int line)
    {
        if (line < 1 || line > lineStarts.Length)
        {
            return "";
        }

        var start = lineStarts[line - 1];
        var exclusive = line < lineStarts.Length ? lineStarts[line] - 1 : text.Length;
        if (exclusive > start && text[exclusive - 1] == '\r')
        {
            exclusive--;
        }

        if (exclusive < start)
        {
            exclusive = start;
        }

        return text[start..exclusive];
    }
}
