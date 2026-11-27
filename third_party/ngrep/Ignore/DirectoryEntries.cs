using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Ngrep.Ignore;

internal enum DirentKind : byte
{
    File,
    Directory,
    Symlink,
    Other,
}

internal interface IDirentSink
{
    void OnEntry(string name, DirentKind kind, bool hiddenAttribute);
}

/// <summary>
/// Lists one directory. On Linux, <c>d_type</c> distinguishes files, directories, and
/// symlinks without an <c>lstat</c>. Other systems take attributes from
/// <see cref="FileSystemInfo"/> so each name is not statted again.
/// The hidden flag on each entry is the Windows or macOS hidden bit.
/// Dot names are not reported here; the walker treats those separately.
/// </summary>
internal static class DirectoryEntries
{
    public static bool TryList<TSink>(string directory, ref TSink sink, CancellationToken cancellation = default)
        where TSink : IDirentSink
    {
        if (OperatingSystem.IsLinux())
        {
            return LinuxDirectory.TryList(directory, ref sink, cancellation);
        }

        return ListManaged(directory, ref sink, cancellation);
    }

    private static bool ListManaged<TSink>(string directory, ref TSink sink, CancellationToken cancellation)
        where TSink : IDirentSink
    {
        try
        {
            // AttributesToSkip = 0 keeps hidden entries visible. The walker decides
            // whether --hidden is set. Skipping them here would make that flag a no-op.
            var options = new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };
            cancellation.ThrowIfCancellationRequested();
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
            {
                cancellation.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = info.Attributes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var hidden = (attributes & FileAttributes.Hidden) != 0;
                sink.OnEntry(info.Name, KindOf(attributes), hidden);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DirentKind KindOf(FileAttributes attributes)
    {
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
}
