namespace BirkNext.Api.Services.Parquet;

/// <summary>
/// Minimal Thrift compact-protocol reader for Parquet metadata (file footer and page headers). Reads into a generic field tree; unknown fields are
/// skipped. Bounded: nesting depth, collection sizes and string lengths are capped, so a malformed or hostile footer fails with a reason instead of
/// exhausting memory.
/// </summary>
public sealed class ThriftCompact(byte[] data, int offset, int end)
{
    public const int MaxDepth = 32, MaxCollection = 1_000_000, MaxBinary = 16 * 1024 * 1024;
    private int _pos = offset;
    public int Position => _pos;

    public sealed class Fields : Dictionary<short, object?>
    {
        public int? I32(short id) => TryGetValue(id, out var v) && v is long l ? checked((int)l) : null;
        public long? I64(short id) => TryGetValue(id, out var v) && v is long l ? l : null;
        public bool? Bool(short id) => TryGetValue(id, out var v) && v is bool b ? b : null;
        public byte[]? Bin(short id) => TryGetValue(id, out var v) ? v as byte[] : null;
        public string? Str(short id) => Bin(id) is { } b ? System.Text.Encoding.UTF8.GetString(b) : null;
        public Fields? Struct(short id) => TryGetValue(id, out var v) ? v as Fields : null;
        public List<object?> List(short id) => TryGetValue(id, out var v) && v is List<object?> l ? l : [];
        public IEnumerable<Fields> Structs(short id) => List(id).OfType<Fields>();
    }

    public Fields ReadStruct(int depth = 0)
    {
        if (depth > MaxDepth) throw new ParquetFormatException("The metadata nests too deeply.");
        var fields = new Fields();
        short last = 0;
        while (true)
        {
            var header = Byte();
            if (header == 0) return fields;
            var type = header & 0x0F;
            var delta = header >> 4;
            var id = delta == 0 ? checked((short)ZigZag(Varint())) : (short)(last + delta);
            last = id;
            fields[id] = ReadValue(type, depth);
        }
    }

    private object? ReadValue(int type, int depth) => type switch
    {
        1 => true,
        2 => false,
        3 => (long)(sbyte)Byte(),
        4 or 5 or 6 => ZigZag(Varint()),
        7 => ReadDouble(),
        8 => Binary(),
        9 or 10 => ReadList(depth),
        11 => ReadMap(depth),
        12 => ReadStruct(depth + 1),
        _ => throw new ParquetFormatException($"Unknown Thrift type {type} in the metadata."),
    };

    private List<object?> ReadList(int depth)
    {
        var header = Byte();
        long size = header >> 4;
        var elem = header & 0x0F;
        if (size == 15) size = Varint();
        if (size > MaxCollection) throw new ParquetFormatException("A metadata list is too long.");
        var list = new List<object?>((int)Math.Min(size, 1024));
        for (var i = 0; i < size; i++) list.Add(elem is 1 or 2 ? Byte() == 1 : ReadValue(elem, depth + 1));
        return list;
    }

    private object? ReadMap(int depth)
    {
        var size = Varint();
        if (size == 0) return null;
        if (size > MaxCollection) throw new ParquetFormatException("A metadata map is too large.");
        var kinds = Byte();
        for (var i = 0; i < size; i++) { ReadValue(kinds >> 4, depth + 1); ReadValue(kinds & 0x0F, depth + 1); }
        return null;
    }

    private byte Byte()
    {
        if (_pos >= end) throw new ParquetFormatException("The metadata ends unexpectedly (truncated file).");
        return data[_pos++];
    }

    private long Varint()
    {
        long result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = Byte();
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new ParquetFormatException("A malformed number in the metadata.");
    }

    private static long ZigZag(long n) => (long)((ulong)n >> 1) ^ -(n & 1);

    private double ReadDouble()
    {
        if (_pos + 8 > end) throw new ParquetFormatException("The metadata ends unexpectedly (truncated file).");
        var v = BitConverter.ToDouble(data, _pos);
        _pos += 8;
        return v;
    }

    private byte[] Binary()
    {
        var len = Varint();
        if (len < 0 || len > MaxBinary || _pos + len > end) throw new ParquetFormatException("A metadata string is longer than the file.");
        var b = data.AsSpan(_pos, (int)len).ToArray();
        _pos += (int)len;
        return b;
    }
}

public sealed class ParquetFormatException(string message) : Exception(message);

/// <summary>Snappy block decompression (raw format used by Parquet pages). The output size is declared up front and enforced.</summary>
internal static class Snappy
{
    public static byte[] Decompress(ReadOnlySpan<byte> input, int expectedLength)
    {
        var pos = 0;
        long declared = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (pos >= input.Length || shift > 28) throw new ParquetFormatException("Malformed Snappy data.");
            var b = input[pos++];
            declared |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
        }
        if (declared != expectedLength) throw new ParquetFormatException("Snappy data does not match the page's declared size.");
        var output = new byte[declared];
        var o = 0;
        while (pos < input.Length)
        {
            var tag = input[pos++];
            switch (tag & 3)
            {
                case 0:
                    var len = tag >> 2;
                    if (len >= 60)
                    {
                        var bytes = len - 59;
                        if (pos + bytes > input.Length) throw new ParquetFormatException("Malformed Snappy literal.");
                        len = 0;
                        for (var i = 0; i < bytes; i++) len |= input[pos++] << (8 * i);
                    }
                    len += 1;
                    if (pos + len > input.Length || o + len > output.Length) throw new ParquetFormatException("Malformed Snappy literal.");
                    input.Slice(pos, len).CopyTo(output.AsSpan(o));
                    pos += len; o += len;
                    break;
                default:
                    int length, offset;
                    if ((tag & 3) == 1)
                    {
                        if (pos >= input.Length) throw new ParquetFormatException("Malformed Snappy copy.");
                        length = 4 + ((tag >> 2) & 7);
                        offset = ((tag >> 5) << 8) | input[pos++];
                    }
                    else if ((tag & 3) == 2)
                    {
                        if (pos + 2 > input.Length) throw new ParquetFormatException("Malformed Snappy copy.");
                        length = (tag >> 2) + 1;
                        offset = input[pos] | input[pos + 1] << 8;
                        pos += 2;
                    }
                    else
                    {
                        if (pos + 4 > input.Length) throw new ParquetFormatException("Malformed Snappy copy.");
                        length = (tag >> 2) + 1;
                        offset = input[pos] | input[pos + 1] << 8 | input[pos + 2] << 16 | input[pos + 3] << 24;
                        pos += 4;
                    }
                    if (offset <= 0 || offset > o || o + length > output.Length) throw new ParquetFormatException("Malformed Snappy copy.");
                    for (var i = 0; i < length; i++, o++) output[o] = output[o - offset];
                    break;
            }
        }
        if (o != output.Length) throw new ParquetFormatException("Snappy data is shorter than declared.");
        return output;
    }
}
