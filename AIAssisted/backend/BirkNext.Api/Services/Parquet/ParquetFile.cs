using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace BirkNext.Api.Services.Parquet;

/// <summary>
/// A read-only, dependency-free Parquet reader for the data explorer. The whole file is held in memory (uploads are size-bounded), the footer is
/// parsed with bounds checks, and column chunks are decoded on demand. Supported: physical types BOOLEAN, INT32, INT64, INT96, FLOAT, DOUBLE,
/// BYTE_ARRAY, FIXED_LEN_BYTE_ARRAY; PLAIN and dictionary (PLAIN_DICTIONARY / RLE_DICTIONARY) value encodings; RLE / bit-packed levels; data pages
/// v1 and v2; codecs UNCOMPRESSED, SNAPPY and GZIP; flat and non-repeated nested (struct) columns. Not supported (reported per column, never
/// guessed): repeated values (lists, maps), DELTA_* and BYTE_STREAM_SPLIT encodings, ZSTD / LZ4 / BROTLI / LZO codecs.
/// </summary>
public sealed class ParquetFile
{
    public const int MaxFooterBytes = 16 * 1024 * 1024, MaxColumns = 2000, MaxRowGroups = 100_000, MaxPageBytes = 128 * 1024 * 1024;
    public static readonly string[] PhysicalNames = ["BOOLEAN", "INT32", "INT64", "INT96", "FLOAT", "DOUBLE", "BYTE_ARRAY", "FIXED_LEN_BYTE_ARRAY"];
    public static readonly Dictionary<int, string> CodecNames = new() { [0] = "UNCOMPRESSED", [1] = "SNAPPY", [2] = "GZIP", [3] = "LZO", [4] = "BROTLI", [5] = "LZ4", [6] = "ZSTD", [7] = "LZ4_RAW" };
    public static readonly Dictionary<int, string> EncodingNames = new()
    {
        [0] = "PLAIN", [2] = "PLAIN_DICTIONARY", [3] = "RLE", [4] = "BIT_PACKED", [5] = "DELTA_BINARY_PACKED", [6] = "DELTA_LENGTH_BYTE_ARRAY", [7] = "DELTA_BYTE_ARRAY",
        [8] = "RLE_DICTIONARY", [9] = "BYTE_STREAM_SPLIT",
    };

    private readonly byte[] _data;

    public sealed class Node
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public int? Physical { get; init; }
        public int? TypeLength { get; init; }
        public int Repetition { get; init; }
        public int? Converted { get; init; }
        public int? Scale { get; init; }
        public int? Precision { get; init; }
        public ThriftCompact.Fields? Logical { get; init; }
        public List<Node> Children { get; } = [];
        public bool IsGroup => Physical is null;
    }

    public sealed class Leaf
    {
        public Node Node { get; init; } = new();
        public int Index { get; init; }
        public int MaxDefinition { get; init; }
        public int MaxRepetition { get; init; }
        public string Path => Node.Path;
        public int Physical => Node.Physical!.Value;
        public string? NotReadable { get; set; }
    }

    public sealed record Statistics(byte[]? Min, byte[]? Max, long? NullCount);
    public sealed record Chunk(int Leaf, int Codec, List<int> Encodings, long NumValues, long DataPageOffset, long? DictionaryPageOffset, long Compressed, long Uncompressed, Statistics? Stats);
    public sealed record RowGroup(int Index, long Rows, long TotalBytes, List<Chunk?> Chunks);

    public int Version { get; private set; }
    public long Rows { get; private set; }
    public string? CreatedBy { get; private set; }
    public Node Root { get; private set; } = new();
    public List<Leaf> Leaves { get; } = [];
    public List<RowGroup> RowGroups { get; } = [];
    public List<(string Key, string? Value)> KeyValues { get; } = [];
    public long Size => _data.Length;

    private ParquetFile(byte[] data) => _data = data;

    public static ParquetFile Open(byte[] data)
    {
        if (data.Length == 0) throw new ParquetFormatException("The file is empty.");
        if (data.Length < 12) throw new ParquetFormatException("The file is too small to be Parquet.");
        if (!data.AsSpan(0, 4).SequenceEqual("PAR1"u8)) throw new ParquetFormatException("Not a Parquet file: the header magic is missing.");
        if (!data.AsSpan(data.Length - 4, 4).SequenceEqual("PAR1"u8)) throw new ParquetFormatException("Not a complete Parquet file: the footer magic is missing (truncated or encrypted footer).");
        var footerLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(data.Length - 8, 4));
        if (footerLength <= 0 || footerLength > MaxFooterBytes || footerLength > data.Length - 12)
            throw new ParquetFormatException("The footer length is invalid (corrupt or truncated file).");
        var file = new ParquetFile(data);
        var meta = new ThriftCompact(data, data.Length - 8 - footerLength, data.Length - 8).ReadStruct();
        file.Version = meta.I32(1) ?? 0;
        file.Rows = meta.I64(3) ?? throw new ParquetFormatException("The footer does not state the row count.");
        file.CreatedBy = meta.Str(6);
        foreach (var kv in meta.Structs(5)) file.KeyValues.Add((kv.Str(1) ?? "", kv.Str(2)));
        var schema = meta.Structs(2).ToList();
        if (schema.Count == 0) throw new ParquetFormatException("The footer has no schema.");
        if (schema.Count > MaxColumns * 2) throw new ParquetFormatException($"The schema has more than {MaxColumns * 2} elements.");
        var cursor = 0;
        file.Root = file.BuildNode(schema, ref cursor, "", 0);
        if (cursor != schema.Count) throw new ParquetFormatException("The schema's child counts do not match its elements.");
        file.CollectLeaves(file.Root, 0, 0, false);
        if (file.Leaves.Count > MaxColumns) throw new ParquetFormatException($"More than {MaxColumns} columns.");
        var groups = meta.Structs(4).ToList();
        if (groups.Count > MaxRowGroups) throw new ParquetFormatException($"More than {MaxRowGroups} row groups.");
        var byPath = file.Leaves.ToDictionary(l => l.Path, StringComparer.Ordinal);
        foreach (var (g, gi) in groups.Select((g, i) => (g, i)))
        {
            var chunks = new Chunk?[file.Leaves.Count];
            foreach (var c in g.Structs(1))
            {
                if (c.Str(1) is { Length: > 0 }) throw new ParquetFormatException("Column chunks in external files are not supported.");
                var m = c.Struct(3) ?? throw new ParquetFormatException("A column chunk has no metadata.");
                var path = string.Join('.', m.List(3).OfType<byte[]>().Select(b => Encoding.UTF8.GetString(b)));
                if (!byPath.TryGetValue(path, out var leaf)) throw new ParquetFormatException($"A column chunk refers to an unknown column {Safe(path)}.");
                var st = m.Struct(12);
                chunks[leaf.Index] = new Chunk(leaf.Index, m.I32(4) ?? 0, m.List(2).OfType<long>().Select(e => (int)e).ToList(), m.I64(5) ?? 0, m.I64(9) ?? 0, m.I64(11),
                    m.I64(7) ?? 0, m.I64(6) ?? 0, st is null ? null : new Statistics(st.Bin(6) ?? st.Bin(2), st.Bin(5) ?? st.Bin(1), st.I64(3)));
            }
            file.RowGroups.Add(new RowGroup(gi, g.I64(3) ?? 0, g.I64(2) ?? 0, [.. chunks]));
        }
        if (file.RowGroups.Sum(r => r.Rows) != file.Rows) throw new ParquetFormatException("The row groups do not add up to the file's row count.");
        foreach (var leaf in file.Leaves) leaf.NotReadable ??= file.Readability(leaf);
        return file;
    }

    private Node BuildNode(List<ThriftCompact.Fields> schema, ref int cursor, string parent, int depth)
    {
        if (cursor >= schema.Count || depth > 64) throw new ParquetFormatException("The schema is malformed.");
        var e = schema[cursor++];
        var name = e.Str(4) ?? "";
        var node = new Node
        {
            Name = name, Path = depth == 0 ? "" : parent.Length == 0 ? name : parent + "." + name, Physical = e.I32(1), TypeLength = e.I32(2), Repetition = e.I32(3) ?? 0,
            Converted = e.I32(6), Scale = e.I32(7), Precision = e.I32(8), Logical = e.Struct(10),
        };
        var children = e.I32(5) ?? 0;
        if (children < 0 || children > MaxColumns) throw new ParquetFormatException("The schema is malformed.");
        for (var i = 0; i < children; i++) node.Children.Add(BuildNode(schema, ref cursor, node.Path, depth + 1));
        return node;
    }

    private void CollectLeaves(Node node, int def, int rep, bool isRoot)
    {
        foreach (var child in node.Children)
        {
            var d = def + (child.Repetition != 0 ? 1 : 0);
            var r = rep + (child.Repetition == 2 ? 1 : 0);
            if (child.IsGroup) CollectLeaves(child, d, r, false);
            else Leaves.Add(new Leaf { Node = child, Index = Leaves.Count, MaxDefinition = d, MaxRepetition = r });
        }
    }

    private string? Readability(Leaf leaf)
    {
        if (leaf.MaxRepetition > 0) return "Repeated values (lists, maps) are not read by this explorer; the schema is shown.";
        if (leaf.Physical is < 0 or > 7) return "Unknown physical type.";
        foreach (var group in RowGroups)
        {
            if (group.Rows == 0) continue;
            if (group.Chunks[leaf.Index] is not { } c) return "The file has no column chunk for this column.";
            if (c.Codec is not (0 or 1 or 2)) return $"Codec {CodecNames.GetValueOrDefault(c.Codec, c.Codec.ToString(CultureInfo.InvariantCulture))} is not supported (UNCOMPRESSED, SNAPPY and GZIP are).";
            if (c.Encodings.FirstOrDefault(e => e is 5 or 6 or 7 or 9) is var unsupported and > 0)
                return $"Encoding {EncodingNames.GetValueOrDefault(unsupported, unsupported.ToString(CultureInfo.InvariantCulture))} is not supported (PLAIN and dictionary are).";
        }
        return null;
    }

    private static string Safe(string text) => new(text.Where(c => !char.IsControl(c)).Take(200).ToArray());

    // ── Type description ─────────────────────────────────────────────────────────────────────────────────────────────────

    public static string? LogicalName(Node n)
    {
        if (n.Logical is { } l)
        {
            if (l.ContainsKey(1)) return "STRING";
            if (l.ContainsKey(2)) return "MAP";
            if (l.ContainsKey(3)) return "LIST";
            if (l.ContainsKey(4)) return "ENUM";
            if (l.Struct(5) is { } dec) return $"DECIMAL({dec.I32(2)},{dec.I32(1)})";
            if (l.ContainsKey(6)) return "DATE";
            if (l.Struct(7) is { } time) return $"TIME({Unit(time.Struct(2))}{(time.Bool(1) == true ? ", UTC" : "")})";
            if (l.Struct(8) is { } ts) return $"TIMESTAMP({Unit(ts.Struct(2))}{(ts.Bool(1) == true ? ", UTC" : ", local")})";
            if (l.Struct(10) is { } i) return $"INT({i.I32(1) ?? (int?)i.I64(1)}, {(i.Bool(2) == false ? "unsigned" : "signed")})";
            if (l.ContainsKey(12)) return "JSON";
            if (l.ContainsKey(13)) return "BSON";
            if (l.ContainsKey(14)) return "UUID";
            if (l.ContainsKey(15)) return "FLOAT16";
        }
        return n.Converted switch
        {
            0 => "STRING", 1 => "MAP", 3 => "LIST", 4 => "ENUM", 5 => $"DECIMAL({n.Precision},{n.Scale})", 6 => "DATE", 7 => "TIME(MILLIS)", 8 => "TIME(MICROS)",
            9 => "TIMESTAMP(MILLIS)", 10 => "TIMESTAMP(MICROS)", 19 => "JSON", 20 => "BSON",
            >= 11 and <= 18 => $"INT({(n.Converted is 11 or 15 ? 8 : n.Converted is 12 or 16 ? 16 : n.Converted is 13 or 17 ? 32 : 64)}, {(n.Converted < 15 ? "unsigned" : "signed")})",
            _ => null,
        };
    }

    private static string Unit(ThriftCompact.Fields? u) => u is null ? "?" : u.ContainsKey(1) ? "MILLIS" : u.ContainsKey(2) ? "MICROS" : u.ContainsKey(3) ? "NANOS" : "?";

    // ── Value decoding ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The values of one column chunk, one entry per row (null = null). Throws ParquetFormatException for malformed pages.</summary>
    public object?[] ReadColumn(int rowGroup, int leafIndex)
    {
        var leaf = Leaves[leafIndex];
        if (leaf.NotReadable is { } why) throw new ParquetFormatException(why);
        var group = RowGroups[rowGroup];
        var chunk = group.Chunks[leafIndex] ?? throw new ParquetFormatException("The file has no column chunk for this column.");
        if (chunk.NumValues != group.Rows) throw new ParquetFormatException("The column chunk's value count does not match the row group.");
        var result = new object?[group.Rows];
        object?[]? dictionary = null;
        long pos = chunk.DictionaryPageOffset is { } d && d > 0 && d < chunk.DataPageOffset ? d : chunk.DataPageOffset;
        var filled = 0L;
        var guard = 0;
        while (filled < group.Rows)
        {
            if (++guard > 1_000_000 || pos < 4 || pos >= _data.Length - 8) throw new ParquetFormatException("A column chunk's pages run past the data.");
            var reader = new ThriftCompact(_data, (int)pos, _data.Length - 8);
            var header = reader.ReadStruct();
            var type = header.I32(1) ?? -1;
            var uncompressed = header.I32(2) ?? -1;
            var compressed = header.I32(3) ?? -1;
            if (uncompressed < 0 || compressed < 0 || uncompressed > MaxPageBytes || compressed > MaxPageBytes) throw new ParquetFormatException("A page declares an invalid size.");
            var bodyStart = reader.Position;
            if (bodyStart + (long)compressed > _data.Length - 8) throw new ParquetFormatException("A page runs past the end of the file (truncated).");
            var body = _data.AsSpan(bodyStart, compressed);
            pos = bodyStart + compressed;
            switch (type)
            {
                case 2: // dictionary page
                    var dh = header.Struct(7) ?? throw new ParquetFormatException("A dictionary page has no header.");
                    var count = dh.I32(1) ?? 0;
                    if (count < 0 || count > 10_000_000) throw new ParquetFormatException("A dictionary declares an invalid size.");
                    var plain = Decompress(chunk.Codec, body, uncompressed);
                    var offset = 0;
                    dictionary = DecodePlain(leaf, plain, ref offset, count);
                    break;
                case 0: // data page v1
                {
                    var h = header.Struct(5) ?? throw new ParquetFormatException("A data page has no header.");
                    var n = h.I32(1) ?? 0;
                    var page = Decompress(chunk.Codec, body, uncompressed);
                    var o = 0;
                    if (leaf.MaxRepetition > 0) throw new ParquetFormatException("Repeated values are not supported.");
                    int[]? defs = null;
                    if (leaf.MaxDefinition > 0)
                    {
                        if (page.Length < 4) throw new ParquetFormatException("A data page is too short.");
                        var len = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(0, 4));
                        if (len < 0 || 4 + len > page.Length) throw new ParquetFormatException("Malformed definition levels.");
                        defs = Hybrid(page.AsSpan(4, len), BitWidth(leaf.MaxDefinition), n);
                        o = 4 + len;
                    }
                    Fill(leaf, h.I32(2) ?? 0, page.AsSpan(o), n, defs, dictionary, result, ref filled);
                    break;
                }
                case 3: // data page v2
                {
                    var h = header.Struct(8) ?? throw new ParquetFormatException("A data page has no header.");
                    var n = h.I32(1) ?? 0;
                    var defLen = h.I32(5) ?? 0;
                    var repLen = h.I32(6) ?? 0;
                    if (defLen < 0 || repLen < 0 || defLen + repLen > compressed) throw new ParquetFormatException("Malformed page levels.");
                    if (leaf.MaxRepetition > 0) throw new ParquetFormatException("Repeated values are not supported.");
                    var defs = leaf.MaxDefinition > 0 ? Hybrid(body.Slice(repLen, defLen), BitWidth(leaf.MaxDefinition), n) : null;
                    var valuesCompressed = body[(repLen + defLen)..];
                    var values = h.Bool(7) == false ? valuesCompressed.ToArray() : Decompress(chunk.Codec, valuesCompressed, uncompressed - repLen - defLen);
                    Fill(leaf, h.I32(4) ?? 0, values, n, defs, dictionary, result, ref filled);
                    break;
                }
                default:
                    break; // index pages and unknown page types are skipped
            }
        }
        return result;
    }

    private static int BitWidth(int max) => max == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)max);

    private static byte[] Decompress(int codec, ReadOnlySpan<byte> body, int uncompressed)
    {
        switch (codec)
        {
            case 0:
                if (body.Length != uncompressed) throw new ParquetFormatException("An uncompressed page does not match its declared size.");
                return body.ToArray();
            case 1:
                return Snappy.Decompress(body, uncompressed);
            case 2:
                using (var input = new MemoryStream(body.ToArray()))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                {
                    var output = new byte[uncompressed];
                    var read = 0;
                    while (read < output.Length && gzip.Read(output, read, output.Length - read) is var r and > 0) read += r;
                    if (read != uncompressed || gzip.ReadByte() != -1) throw new ParquetFormatException("A GZIP page does not match its declared size (corrupt or a decompression bomb).");
                    return output;
                }
            default:
                throw new ParquetFormatException($"Codec {CodecNames.GetValueOrDefault(codec, codec.ToString(CultureInfo.InvariantCulture))} is not supported.");
        }
    }

    private void Fill(Leaf leaf, int encoding, ReadOnlySpan<byte> values, int n, int[]? defs, object?[]? dictionary, object?[] result, ref long filled)
    {
        var present = defs is null ? n : defs.Count(d => d == leaf.MaxDefinition);
        object?[] decoded;
        if (encoding is 2 or 8)
        {
            if (dictionary is null) throw new ParquetFormatException("A dictionary-encoded page has no dictionary.");
            if (present > 0 && values.Length == 0) throw new ParquetFormatException("Malformed dictionary indices.");
            var indices = present == 0 ? [] : Hybrid(values[1..], values[0], present);
            decoded = new object?[present];
            for (var i = 0; i < present; i++)
            {
                if ((uint)indices[i] >= (uint)dictionary.Length) throw new ParquetFormatException("A dictionary index is out of range.");
                decoded[i] = dictionary[indices[i]];
            }
        }
        else if (encoding == 0)
        {
            var o = 0;
            decoded = DecodePlain(leaf, values.ToArray(), ref o, present);
        }
        else throw new ParquetFormatException($"Encoding {EncodingNames.GetValueOrDefault(encoding, encoding.ToString(CultureInfo.InvariantCulture))} is not supported.");
        if (filled + n > result.Length) throw new ParquetFormatException("A page has more values than its row group.");
        var v = 0;
        for (var i = 0; i < n; i++) result[filled + i] = defs is null || defs[i] == leaf.MaxDefinition ? decoded[v++] : null;
        filled += n;
    }

    private object?[] DecodePlain(Leaf leaf, byte[] data, ref int o, int count)
    {
        var values = new object?[count];
        var span = data.AsSpan();
        static void Need(int bytes, int at, int length) { if (at + bytes > length || at < 0) throw new ParquetFormatException("Page values run past the page."); }
        for (var i = 0; i < count; i++)
        {
            switch (leaf.Physical)
            {
                case 0: Need(1, o + i / 8, span.Length); values[i] = (span[o + i / 8] >> (i % 8) & 1) == 1; if (i == count - 1) o += (count + 7) / 8; break;
                case 1: Need(4, o, span.Length); values[i] = Convert(leaf, BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o, 4))); o += 4; break;
                case 2: Need(8, o, span.Length); values[i] = Convert(leaf, BinaryPrimitives.ReadInt64LittleEndian(span.Slice(o, 8))); o += 8; break;
                case 3: Need(12, o, span.Length); values[i] = Int96(span.Slice(o, 12)); o += 12; break;
                case 4: Need(4, o, span.Length); values[i] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o, 4)); o += 4; break;
                case 5: Need(8, o, span.Length); values[i] = BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(o, 8)); o += 8; break;
                case 6:
                    Need(4, o, span.Length);
                    var len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o, 4));
                    if (len < 0) throw new ParquetFormatException("A negative value length.");
                    Need(len, o + 4, span.Length);
                    values[i] = Convert(leaf, span.Slice(o + 4, len).ToArray());
                    o += 4 + len;
                    break;
                case 7:
                    var w = leaf.Node.TypeLength ?? throw new ParquetFormatException("A fixed-length column has no length.");
                    Need(w, o, span.Length);
                    values[i] = Convert(leaf, span.Slice(o, w).ToArray());
                    o += w;
                    break;
            }
        }
        return values;
    }

    /// <summary>Applies the logical annotation: DATE → DateOnly, TIMESTAMP → DateTimeOffset (UTC), DECIMAL → decimal, STRING/ENUM/JSON → string.</summary>
    private static object Convert(Leaf leaf, object raw)
    {
        var logical = LogicalName(leaf.Node) ?? "";
        if (logical.StartsWith("DATE", StringComparison.Ordinal) && raw is int days) return DateOnly.FromDayNumber(719162 + days);
        if (logical.StartsWith("TIMESTAMP", StringComparison.Ordinal) && raw is long t)
        {
            var ticks = logical.Contains("MILLIS") ? t * TimeSpan.TicksPerMillisecond : logical.Contains("NANOS") ? t / 100 : t * 10;
            return new DateTimeOffset(DateTime.UnixEpoch.Ticks + ticks, TimeSpan.Zero);
        }
        if (logical.StartsWith("DECIMAL", StringComparison.Ordinal) && (leaf.Node.Scale ?? leaf.Node.Logical?.Struct(5)?.I32(1)) is { } scale)
        {
            var unscaled = raw switch { int i => (System.Numerics.BigInteger)i, long l => l, byte[] b => new System.Numerics.BigInteger(b, isUnsigned: false, isBigEndian: true), _ => 0 };
            return (decimal)unscaled / (decimal)Math.Pow(10, scale);
        }
        if (raw is byte[] bytes && (logical is "STRING" or "ENUM" or "JSON" || leaf.Node.Converted is 0 or 4 or 19)) return Encoding.UTF8.GetString(bytes);
        return raw;
    }

    private static DateTimeOffset Int96(ReadOnlySpan<byte> b)
    {
        var nanos = BinaryPrimitives.ReadInt64LittleEndian(b[..8]);
        var julian = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(8, 4));
        return new DateTimeOffset(DateTime.UnixEpoch.AddDays(julian - 2440588).Ticks + nanos / 100, TimeSpan.Zero);
    }

    /// <summary>RLE / bit-packed hybrid decoding of exactly <paramref name="count"/> values.</summary>
    internal static int[] Hybrid(ReadOnlySpan<byte> data, int bitWidth, int count)
    {
        if (bitWidth is < 0 or > 32) throw new ParquetFormatException("An invalid bit width.");
        var result = new int[count];
        var o = 0; var n = 0;
        var byteWidth = (bitWidth + 7) / 8;
        while (n < count)
        {
            long header = 0;
            for (var shift = 0; ; shift += 7)
            {
                if (o >= data.Length || shift > 35) throw new ParquetFormatException("Malformed RLE data.");
                var b = data[o++];
                header |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
            }
            if ((header & 1) == 0)
            {
                var run = (int)Math.Min(header >> 1, count - n);
                if (o + byteWidth > data.Length) throw new ParquetFormatException("Malformed RLE run.");
                var value = 0;
                for (var i = 0; i < byteWidth; i++) value |= data[o + i] << (8 * i);
                o += byteWidth;
                if (run == 0 && header >> 1 == 0) throw new ParquetFormatException("An empty RLE run.");
                for (var i = 0; i < run; i++) result[n++] = value;
            }
            else
            {
                var values = (int)(header >> 1) * 8;
                if (values == 0) throw new ParquetFormatException("An empty bit-packed run.");
                var bytes = values * bitWidth / 8;
                if (o + bytes > data.Length) throw new ParquetFormatException("Malformed bit-packed run.");
                long buffer = 0; var bits = 0; var p = o;
                for (var i = 0; i < values; i++)
                {
                    while (bits < bitWidth) { buffer |= (long)data[p++] << bits; bits += 8; }
                    var v = (int)(buffer & ((1L << bitWidth) - 1));
                    buffer >>= bitWidth; bits -= bitWidth;
                    if (n < count) result[n++] = v;
                }
                o += bytes;
            }
        }
        return result;
    }

    // ── Statistics and formatting ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Decodes a statistics value with the column's type (null when absent or not decodable).</summary>
    public object? StatValue(int leafIndex, byte[]? raw)
    {
        if (raw is null) return null;
        var leaf = Leaves[leafIndex];
        try
        {
            return leaf.Physical switch
            {
                1 when raw.Length == 4 => Convert(leaf, BinaryPrimitives.ReadInt32LittleEndian(raw)),
                2 when raw.Length == 8 => Convert(leaf, BinaryPrimitives.ReadInt64LittleEndian(raw)),
                4 when raw.Length == 4 => BinaryPrimitives.ReadSingleLittleEndian(raw),
                5 when raw.Length == 8 => BinaryPrimitives.ReadDoubleLittleEndian(raw),
                6 or 7 => Convert(leaf, raw),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException) { return null; }
    }

    /// <summary>Type-aware display; binary values are summarised (never rendered as text).</summary>
    public static string? Format(object? value) => value switch
    {
        null => null,
        string s => s.Length > 500 ? s[..500] + "…" : s,
        bool b => b ? "true" : "false",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        byte[] bytes => $"binary ({bytes.Length} bytes)",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
