namespace Ngrep.Indexing;

internal sealed class DocumentRecord
{
    public int Id { get; set; }

    public string Path { get; set; } = "";

    public long Size { get; set; }

    public long MtimeUnixMs { get; set; }

    public string Sha256 { get; set; } = "";

    /// <summary>UTF-8 ndjson object without the trailing newline. Cleared when size or mtime changes.</summary>
    internal byte[]? CachedLine { get; set; }
}

internal sealed class IndexManifest
{
    public int FormatVersion { get; set; } = IndexPaths.FormatVersion;

    public string Tool { get; set; } = IndexPaths.ToolName;

    public string Root { get; set; } = "";

    public string CreatedUtc { get; set; } = "";

    public string UpdatedUtc { get; set; } = "";

    public int DocumentCount { get; set; }

    public int TrigramCount { get; set; }

    public string CaseFolding { get; set; } = "invariant-lowercase";

    public string ColumnUnit { get; set; } = "utf16-code-unit";
}

internal sealed class IndexData
{
    public required IndexManifest Manifest { get; init; }

    public required string Directory { get; init; }

    public List<DocumentRecord> Documents { get; init; } = [];

    /// <summary>Set by a search load. The caller reads the rows it needs from here.</summary>
    public string? DocumentsFile { get; init; }

    public Dictionary<string, List<int>> Postings { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class IndexResult
{
    public required string Root { get; init; }

    public required string IndexPath { get; init; }

    public int Documents { get; init; }

    public int Added { get; init; }

    public int Updated { get; init; }

    public int Unchanged { get; init; }

    public int Removed { get; init; }

    public int SkippedIgnored { get; init; }

    public int SkippedBinary { get; init; }

    public int SkippedTooLarge { get; init; }

    public int SkippedUnreadable { get; init; }
}

internal static class IndexPaths
{
    /// <summary>2 dropped <c>lineStarts</c>, which no reader consumed and which dominated the table.</summary>
    public const int FormatVersion = 2;
    public const string ToolName = "ngrep";
    public const string FolderName = ".ngrep";
    public const string IndexName = "index";

    public static string DefaultFor(string root) =>
        Path.Combine(Path.GetFullPath(root), FolderName, IndexName);

    public static string? Discover(string start)
    {
        var full = Path.GetFullPath(start);
        var dir = File.Exists(full) ? Path.GetDirectoryName(full) : full;
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        var current = new DirectoryInfo(dir);
        while (current != null)
        {
            var index = Path.Combine(current.FullName, FolderName, IndexName);
            if (File.Exists(Path.Combine(index, "manifest.json")))
            {
                return index;
            }

            var bak = index + ".bak";
            if (File.Exists(Path.Combine(bak, "manifest.json")))
            {
                return bak;
            }

            current = current.Parent;
        }

        return null;
    }
}
