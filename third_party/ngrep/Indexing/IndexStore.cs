using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ngrep.Indexing;

internal static class IndexStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // Runs after <index>.tmp is complete and before it replaces the live index.
    // A throw leaves the previous index in place. Tests clear this in a finally.
    internal static Action? TestHookBeforeCommit { get; set; }

    public static IndexData? TryLoad(string indexDirectory)
    {
        var dir = ResolveExisting(indexDirectory);
        if (dir == null)
        {
            return null;
        }

        return Read(dir, new IndexRead());
    }

    /// <summary>Indexed tree root, or null when no manifest is present. Does not read documents.</summary>
    public static string? TryReadRoot(string indexDirectory)
    {
        var dir = ResolveExisting(indexDirectory);
        if (dir == null)
        {
            return null;
        }

        return ReadManifest(File.ReadAllText(Path.Combine(dir, "manifest.json"))).Root;
    }

    /// <summary>
    /// Search load. Materializes only <paramref name="grams"/>. A null set checks the
    /// postings header and does not read posting lists.
    /// </summary>
    public static IndexData? TryLoadForSearch(string indexDirectory, HashSet<string>? grams)
    {
        var dir = ResolveExisting(indexDirectory);
        if (dir == null)
        {
            return null;
        }

        return ReadForSearch(dir, grams);
    }

    private readonly struct IndexRead
    {
        public IndexRead()
        {
            AllPostings = true;
            Grams = null;
        }

        public IndexRead(HashSet<string>? grams)
        {
            AllPostings = false;
            Grams = grams;
        }

        public bool AllPostings { get; }

        public HashSet<string>? Grams { get; }
    }

    public static void Write(string indexDirectory, IndexManifest manifest, IReadOnlyList<DocumentRecord> documents, Dictionary<string, List<int>> postings)
    {
        var finalDir = Path.GetFullPath(indexDirectory);
        var tempDir = finalDir + ".tmp";
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }

        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "manifest.json"), ManifestJson(manifest), Utf8);
        WriteDocuments(Path.Combine(tempDir, "documents.ndjson"), documents);
        using (var stream = CreateWrite(Path.Combine(tempDir, "postings.bin")))
        {
            WritePostings(stream, postings);
            stream.Flush(flushToDisk: true);
        }

        try
        {
            TestHookBeforeCommit?.Invoke();
        }
        catch
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }

            throw;
        }

        var backup = finalDir + ".bak";
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        if (Directory.Exists(finalDir))
        {
            Directory.Move(finalDir, backup);
        }

        Directory.Move(tempDir, finalDir);
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }
    }

    public static string ManifestJson(IndexManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", manifest.FormatVersion);
            writer.WriteString("tool", manifest.Tool);
            writer.WriteString("root", manifest.Root);
            writer.WriteString("createdUtc", manifest.CreatedUtc);
            writer.WriteString("updatedUtc", manifest.UpdatedUtc);
            writer.WriteNumber("documentCount", manifest.DocumentCount);
            writer.WriteNumber("trigramCount", manifest.TrigramCount);
            writer.WriteString("caseFolding", manifest.CaseFolding);
            writer.WriteString("columnUnit", manifest.ColumnUnit);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IndexData? Read(string directory, IndexRead read)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        var documentsPath = Path.Combine(directory, "documents.ndjson");
        var postingsPath = Path.Combine(directory, "postings.bin");
        if (!File.Exists(manifestPath) || !File.Exists(documentsPath) || !File.Exists(postingsPath))
        {
            throw new InvalidDataException($"Index at {directory} is incomplete.");
        }

        var manifest = ReadManifest(File.ReadAllText(manifestPath));
        if (manifest.FormatVersion != IndexPaths.FormatVersion)
        {
            // Another format is not read. A search scans and a build starts over,
            // which replaces the directory rather than failing on it.
            return null;
        }

        var documents = ReadDocumentMeta(documentsPath);
        Dictionary<string, List<int>> postings;
        using (var stream = File.OpenRead(postingsPath))
        {
            postings = read.AllPostings ? ReadPostings(stream) : ReadSelectedPostings(stream, read.Grams);
        }

        return new IndexData
        {
            Manifest = manifest,
            Directory = directory,
            Documents = documents,
            Postings = postings,
        };
    }

    /// <summary>
    /// Manifest and postings only. Document rows stay on disk until the caller knows
    /// which ids the trigrams kept, so a selective query never parses the whole table.
    /// </summary>
    private static IndexData? ReadForSearch(string directory, HashSet<string>? grams)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        var documentsPath = Path.Combine(directory, "documents.ndjson");
        var postingsPath = Path.Combine(directory, "postings.bin");
        if (!File.Exists(manifestPath) || !File.Exists(documentsPath) || !File.Exists(postingsPath))
        {
            throw new InvalidDataException($"Index at {directory} is incomplete.");
        }

        var manifest = ReadManifest(File.ReadAllText(manifestPath));
        if (manifest.FormatVersion != IndexPaths.FormatVersion)
        {
            // Another format is not read: the caller scans content instead.
            return null;
        }

        Dictionary<string, List<int>> postings;
        using (var stream = File.OpenRead(postingsPath))
        {
            postings = ReadSelectedPostings(stream, grams);
        }

        return new IndexData
        {
            Manifest = manifest,
            Directory = directory,
            DocumentsFile = documentsPath,
            Postings = postings,
        };
    }

    /// <summary>
    /// Ids and paths from <c>documents.ndjson</c>. Fields after <c>path</c> (size, mtime,
    /// hash) are not read, and a record outside <paramref name="onlyIds"/> does not become
    /// a string.
    /// </summary>
    public static List<DocumentRecord> ReadSearchDocuments(string path, HashSet<int>? onlyIds)
    {
        // Every candidate was filtered out, so no row can be materialized. The table
        // is large (it carries line starts) and reading it is pure cost for a miss.
        if (onlyIds is { Count: 0 })
        {
            return [];
        }

        var documents = new List<DocumentRecord>(onlyIds?.Count ?? 256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var chunk = ArrayPool<byte>.Shared.Rent(1 << 16);
        var line = new ArrayBufferWriter<byte>(256);
        try
        {
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                var span = chunk.AsSpan(0, read);
                var offset = 0;
                while (offset < span.Length)
                {
                    var newline = span[offset..].IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        line.Write(span[offset..]);
                        break;
                    }

                    line.Write(span.Slice(offset, newline));
                    AddSearchDocument(documents, line.WrittenSpan, onlyIds);
                    line.Clear();
                    offset += newline + 1;
                }
            }

            if (line.WrittenCount > 0)
            {
                AddSearchDocument(documents, line.WrittenSpan, onlyIds);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return documents;
    }

    private static void AddSearchDocument(List<DocumentRecord> documents, ReadOnlySpan<byte> line, HashSet<int>? onlyIds)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (line.IsEmpty)
        {
            return;
        }

        var reader = new Utf8JsonReader(line, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("documents.ndjson contains an invalid record.");
        }

        var id = -1;
        string? path = null;
        try
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("id"u8))
                {
                    reader.Read();
                    id = reader.GetInt32();
                    continue;
                }

                if (reader.ValueTextEquals("path"u8))
                {
                    reader.Read();
                    // A filtered-out id still has to be parsed, but not materialized.
                    if (onlyIds == null || onlyIds.Contains(id))
                    {
                        path = reader.GetString();
                    }

                    break;
                }

                reader.Read();
                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                {
                    reader.Skip();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("documents.ndjson contains an invalid record.", ex);
        }

        if (onlyIds != null && !onlyIds.Contains(id))
        {
            return;
        }

        if (path == null || id < 0)
        {
            throw new InvalidDataException("documents.ndjson contains an invalid record.");
        }

        documents.Add(new DocumentRecord { Id = id, Path = path });
    }

    private static string? ResolveExisting(string indexDirectory)
    {
        var full = Path.GetFullPath(indexDirectory);
        if (File.Exists(Path.Combine(full, "manifest.json")))
        {
            return full;
        }

        var bak = full + ".bak";
        if (File.Exists(Path.Combine(bak, "manifest.json")))
        {
            return bak;
        }

        return null;
    }

    private static IndexManifest ReadManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new IndexManifest
            {
                FormatVersion = RequiredInt(root, "formatVersion"),
                Tool = RequiredString(root, "tool"),
                Root = RequiredString(root, "root"),
                CreatedUtc = RequiredString(root, "createdUtc"),
                UpdatedUtc = RequiredString(root, "updatedUtc"),
                DocumentCount = RequiredInt(root, "documentCount"),
                TrigramCount = RequiredInt(root, "trigramCount"),
                CaseFolding = RequiredString(root, "caseFolding"),
                ColumnUnit = RequiredString(root, "columnUnit"),
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("manifest.json is not valid JSON.", ex);
        }
    }

    private const int WriteBufferBytes = 1 << 20;

    private static FileStream CreateWrite(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.None, WriteBufferBytes, FileOptions.SequentialScan);

    private static void WriteDocuments(string path, IReadOnlyList<DocumentRecord> documents)
    {
        var ordered = new DocumentRecord[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            ordered[i] = documents[i];
        }

        Array.Sort(ordered, static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        using var stream = CreateWrite(path);
        foreach (var document in ordered)
        {
            document.CachedLine ??= SerializeDocument(document);
            stream.Write(document.CachedLine);
            stream.WriteByte((byte)'\n');
        }
    }

    private static byte[] SerializeDocument(DocumentRecord document)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", document.Id);
            writer.WriteString("path", document.Path);
            writer.WriteNumber("size", document.Size);
            writer.WriteNumber("mtimeUnixMs", document.MtimeUnixMs);
            writer.WriteString("sha256", document.Sha256);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Document id, path, size, mtime, and hash.
    /// </summary>
    private static List<DocumentRecord> ReadDocumentMeta(string path)
    {
        var documents = new List<DocumentRecord>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var chunk = ArrayPool<byte>.Shared.Rent(1 << 16);
        var line = new ArrayBufferWriter<byte>(256);
        try
        {
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                var span = chunk.AsSpan(0, read);
                var offset = 0;
                while (offset < span.Length)
                {
                    var newline = span[offset..].IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        line.Write(span[offset..]);
                        break;
                    }

                    line.Write(span.Slice(offset, newline));
                    AddMeta(documents, line.WrittenSpan);
                    line.Clear();
                    offset += newline + 1;
                }
            }

            if (line.WrittenCount > 0)
            {
                AddMeta(documents, line.WrittenSpan);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return documents;
    }

    private static void AddMeta(List<DocumentRecord> documents, ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (line.IsEmpty)
        {
            return;
        }

        documents.Add(ParseMeta(line));
    }

    private static DocumentRecord ParseMeta(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException("documents.ndjson contains an invalid record.");
        }

        var id = 0;
        var path = "";
        long size = 0;
        long mtime = 0;
        var hash = "";
        var sawId = false;
        var sawPath = false;
        var sawSize = false;
        var sawMtime = false;
        var sawHash = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new InvalidDataException("documents.ndjson contains an invalid record.");
            }

            var name = reader.GetString();
            if (!reader.Read())
            {
                throw new InvalidDataException("documents.ndjson contains an invalid record.");
            }

            switch (name)
            {
                case "id":
                    id = reader.GetInt32();
                    sawId = true;
                    break;
                case "path":
                    path = reader.GetString() ?? "";
                    sawPath = true;
                    break;
                case "size":
                    size = reader.GetInt64();
                    sawSize = true;
                    break;
                case "mtimeUnixMs":
                    mtime = reader.GetInt64();
                    sawMtime = true;
                    break;
                case "sha256":
                    hash = reader.GetString() ?? "";
                    sawHash = true;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (!sawId || !sawPath || !sawSize || !sawMtime || !sawHash)
        {
            throw new InvalidDataException("documents.ndjson contains an invalid record.");
        }

        return new DocumentRecord
        {
            Id = id,
            Path = path,
            Size = size,
            MtimeUnixMs = mtime,
            Sha256 = hash,
        };
    }

    public static void WritePostings(Stream stream, Dictionary<string, List<int>> postings)
    {
        if (postings.Count == 0)
        {
            using var empty = new BinaryWriter(stream, Utf8, leaveOpen: true);
            empty.Write(Encoding.ASCII.GetBytes("NGTR"));
            empty.Write(IndexPaths.FormatVersion);
            empty.Write(0);
            return;
        }

        var keys = ArrayPool<string>.Shared.Rent(postings.Count);
        try
        {
            var count = 0;
            foreach (var key in postings.Keys)
            {
                keys[count++] = key;
            }

            Array.Sort(keys, 0, count, StringComparer.Ordinal);
            using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("NGTR"));
            writer.Write(IndexPaths.FormatVersion);
            writer.Write(count);
            for (var i = 0; i < count; i++)
            {
                var key = keys[i];
                var bytes = Encoding.UTF8.GetBytes(key);
                if (bytes.Length > ushort.MaxValue)
                {
                    throw new InvalidDataException($"Trigram is too long to store ({bytes.Length} bytes).");
                }

                writer.Write((ushort)bytes.Length);
                writer.Write(bytes);
                var ids = postings[key];
                writer.Write(ids.Count);
                foreach (var id in ids)
                {
                    writer.Write(id);
                }
            }
        }
        finally
        {
            ArrayPool<string>.Shared.Return(keys);
        }
    }

    public static Dictionary<string, List<int>> ReadPostings(Stream stream)
    {
        using var reader = new BinaryReader(stream, Utf8, leaveOpen: true);
        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'N' || magic[1] != (byte)'G' || magic[2] != (byte)'T' || magic[3] != (byte)'R')
        {
            throw new InvalidDataException("postings.bin magic mismatch (expected NGTR).");
        }

        var version = reader.ReadInt32();
        if (version != IndexPaths.FormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported postings version {version}. This build reads version {IndexPaths.FormatVersion}.");
        }

        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("postings.bin trigram count is invalid.");
        }

        var postings = new Dictionary<string, List<int>>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var len = reader.ReadUInt16();
            var bytes = reader.ReadBytes(len);
            if (bytes.Length != len)
            {
                throw new InvalidDataException("postings.bin ended inside a trigram.");
            }

            var gram = Encoding.UTF8.GetString(bytes);
            var n = reader.ReadInt32();
            if (n < 0)
            {
                throw new InvalidDataException("postings.bin posting count is invalid.");
            }

            var ids = new List<int>(n);
            for (var j = 0; j < n; j++)
            {
                ids.Add(reader.ReadInt32());
            }

            postings[gram] = ids;
        }

        return postings;
    }

    /// <summary>
    /// Reads posting lists for <paramref name="grams"/> only. A null set validates the
    /// header and returns an empty map. Scanning stops once every requested gram is loaded.
    /// </summary>
    private static Dictionary<string, List<int>> ReadSelectedPostings(Stream stream, HashSet<string>? grams)
    {
        var reader = new SeqReader(stream);
        try
        {
            var magic = new byte[4];
            reader.ReadBytes(magic);
            if (magic[0] != (byte)'N' || magic[1] != (byte)'G' || magic[2] != (byte)'T' || magic[3] != (byte)'R')
            {
                throw new InvalidDataException("postings.bin magic mismatch (expected NGTR).");
            }

            var version = reader.ReadInt32();
            if (version != IndexPaths.FormatVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported postings version {version}. This build reads version {IndexPaths.FormatVersion}.");
            }

            var count = reader.ReadInt32();
            if (count < 0)
            {
                throw new InvalidDataException("postings.bin trigram count is invalid.");
            }

            if (grams == null || grams.Count == 0 || count == 0)
            {
                return new Dictionary<string, List<int>>(StringComparer.Ordinal);
            }

            var pending = grams.Count;
            var postings = new Dictionary<string, List<int>>(grams.Count, StringComparer.Ordinal);
            var gramBuf = ArrayPool<byte>.Shared.Rent(16);
            try
            {
                for (var i = 0; i < count && pending > 0; i++)
                {
                    var len = reader.ReadUInt16();
                    if (gramBuf.Length < len)
                    {
                        ArrayPool<byte>.Shared.Return(gramBuf);
                        gramBuf = ArrayPool<byte>.Shared.Rent(len);
                    }

                    reader.ReadBytes(gramBuf.AsSpan(0, len));
                    var n = reader.ReadInt32();
                    if (n < 0)
                    {
                        throw new InvalidDataException("postings.bin posting count is invalid.");
                    }

                    var gram = Encoding.UTF8.GetString(gramBuf, 0, len);
                    if (!grams.Contains(gram))
                    {
                        var skip = (long)n * 4;
                        while (skip > 0)
                        {
                            var step = (int)Math.Min(skip, 1 << 20);
                            reader.Skip(step);
                            skip -= step;
                        }

                        continue;
                    }

                    var ids = new List<int>(n);
                    for (var j = 0; j < n; j++)
                    {
                        ids.Add(reader.ReadInt32());
                    }

                    postings[gram] = ids;
                    pending--;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(gramBuf);
            }

            return postings;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private struct SeqReader : IDisposable
    {
        private readonly Stream _stream;
        private byte[] _buffer;
        private int _pos;
        private int _filled;

        public SeqReader(Stream stream)
        {
            _stream = stream;
            _buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
            _pos = 0;
            _filled = 0;
        }

        public void Dispose()
        {
            if (_buffer != null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null!;
            }
        }

        public int ReadInt32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_pos));
            _pos += 4;
            return value;
        }

        public ushort ReadUInt16()
        {
            Require(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_pos));
            _pos += 2;
            return value;
        }

        public void ReadBytes(Span<byte> dest)
        {
            var offset = 0;
            while (offset < dest.Length)
            {
                if (_pos >= _filled)
                {
                    Fill();
                    if (_filled == 0)
                    {
                        throw new InvalidDataException("postings.bin ended early.");
                    }
                }

                var n = Math.Min(dest.Length - offset, _filled - _pos);
                _buffer.AsSpan(_pos, n).CopyTo(dest[offset..]);
                _pos += n;
                offset += n;
            }
        }

        public void Skip(int count)
        {
            if (count < 0)
            {
                throw new InvalidDataException("postings.bin posting count is invalid.");
            }

            while (count > 0)
            {
                if (_pos >= _filled)
                {
                    Fill();
                    if (_filled == 0)
                    {
                        throw new InvalidDataException("postings.bin ended early.");
                    }
                }

                var n = Math.Min(count, _filled - _pos);
                _pos += n;
                count -= n;
            }
        }

        private void Require(int count)
        {
            if (_filled - _pos >= count)
            {
                return;
            }

            var have = _filled - _pos;
            if (have > 0 && _pos > 0)
            {
                Buffer.BlockCopy(_buffer, _pos, _buffer, 0, have);
            }

            _pos = 0;
            _filled = have;
            while (_filled < count)
            {
                var read = _stream.Read(_buffer, _filled, _buffer.Length - _filled);
                if (read == 0)
                {
                    throw new InvalidDataException("postings.bin ended early.");
                }

                _filled += read;
            }
        }

        private void Fill()
        {
            var have = _filled - _pos;
            if (have > 0 && _pos > 0)
            {
                Buffer.BlockCopy(_buffer, _pos, _buffer, 0, have);
            }

            _pos = 0;
            _filled = have;
            var read = _stream.Read(_buffer, _filled, _buffer.Length - _filled);
            _filled += read;
        }
    }

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidDataException($"Missing integer field '{name}'.");

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Missing string field '{name}'.");
        }

        return value.GetString() ?? "";
    }
}
