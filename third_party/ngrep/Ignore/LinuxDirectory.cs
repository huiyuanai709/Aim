using System.Runtime.InteropServices;
using System.Text;

namespace Ngrep.Ignore;

internal static partial class LinuxDirectory
{
    private const byte DtUnknown = 0;
    private const byte DtDir = 4;
    private const byte DtReg = 8;
    private const byte DtLnk = 10;
    private const int TypeOffset = 18;
    private const int NameOffset = 19;

    public static bool TryList<TSink>(string directory, ref TSink sink, CancellationToken cancellation)
        where TSink : IDirentSink
    {
        cancellation.ThrowIfCancellationRequested();
        var dir = OpenDir(directory);
        if (dir == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var entry = ReadDir(dir);
                if (entry == IntPtr.Zero)
                {
                    return true;
                }

                var kind = Kind(entry);
                var nameBytes = Name(entry);
                if (nameBytes.Length == 0 || IsDot(nameBytes))
                {
                    continue;
                }

                var name = Encoding.UTF8.GetString(nameBytes);
                if (kind == DirentKind.Other)
                {
                    kind = StatKind(directory, name);
                }

                // Unix hidden names start with '.'. The walker checks that. There is no
                // separate hidden attribute on this path.
                sink.OnEntry(name, kind, hiddenAttribute: false);
            }
        }
        finally
        {
            CloseDir(dir);
        }
    }

    private static unsafe DirentKind Kind(IntPtr entry)
    {
        var type = *((byte*)entry + TypeOffset);
        return type switch
        {
            DtDir => DirentKind.Directory,
            DtReg => DirentKind.File,
            DtLnk => DirentKind.Symlink,
            DtUnknown => DirentKind.Other,
            _ => DirentKind.Other,
        };
    }

    private static unsafe ReadOnlySpan<byte> Name(IntPtr entry)
    {
        var start = (byte*)entry + NameOffset;
        var length = 0;
        while (start[length] != 0)
        {
            length++;
        }

        return new ReadOnlySpan<byte>(start, length);
    }

    private static bool IsDot(ReadOnlySpan<byte> name)
    {
        if (name.Length == 1 && name[0] == (byte)'.')
        {
            return true;
        }

        return name.Length == 2 && name[0] == (byte)'.' && name[1] == (byte)'.';
    }

    private static DirentKind StatKind(string directory, string name)
    {
        var full = Path.Join(directory, name);
        try
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return DirentKind.Symlink;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                return DirentKind.Directory;
            }

            return DirentKind.File;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DirentKind.Other;
        }
    }

    [LibraryImport("libc", EntryPoint = "opendir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr OpenDir(string name);

    [LibraryImport("libc", EntryPoint = "readdir")]
    private static partial IntPtr ReadDir(IntPtr dir);

    [LibraryImport("libc", EntryPoint = "closedir")]
    private static partial int CloseDir(IntPtr dir);
}
