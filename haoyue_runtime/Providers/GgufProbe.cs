using System.Text;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// Minimal GGUF key/value header reader. It stops before the tensor data, so a models
/// directory can be listed with real display names and trained context sizes without
/// loading any weights. Malformed or truncated headers yield the fields found so far
/// instead of throwing — a broken file must not hide the healthy ones.
/// </summary>
public static class GgufProbe
{
    private const uint Magic = 0x46554747; // "GGUF"
    private const uint TypeUint8 = 0,
        TypeInt8 = 1,
        TypeUint16 = 2,
        TypeInt16 = 3,
        TypeUint32 = 4,
        TypeInt32 = 5,
        TypeFloat32 = 6,
        TypeBool = 7,
        TypeString = 8,
        TypeArray = 9,
        TypeUint64 = 10,
        TypeInt64 = 11,
        TypeFloat64 = 12;

    private const long MaxStringBytes = 32L * 1024 * 1024;
    private const long MaxArrayItems = 1_000_000;

    /// <summary>The three header fields Haoyue needs for local model registration.</summary>
    public sealed record Header(string? Architecture, string? Name, long? ContextLength);

    public static Header Read(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt32() < 2)
                return new(null, null, null);

            reader.ReadUInt64(); // tensor count
            var pairs = reader.ReadInt64();
            if (pairs is < 0 or > 100_000)
                return new(null, null, null);

            string? architecture = null;
            string? name = null;
            long? contextLength = null;

            for (long index = 0; index < pairs && stream.Position < stream.Length; index++)
            {
                var key = ReadString(reader);
                var type = reader.ReadUInt32();
                if (type == TypeString && key.Equals("general.architecture", StringComparison.Ordinal))
                    architecture = ReadString(reader);
                else if (type == TypeString && key.Equals("general.name", StringComparison.Ordinal))
                    name = ReadString(reader);
                else if (key.EndsWith(".context_length", StringComparison.Ordinal)
                         && type is TypeUint32 or TypeInt32 or TypeUint64)
                    contextLength = type == TypeUint64 ? (long)reader.ReadUInt64() : reader.ReadUInt32();
                else if (!Skip(reader, type))
                    break;
            }

            return new(architecture, name, contextLength is > 0 ? contextLength : null);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or DecoderFallbackException)
        {
            return new(null, null, null);
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > MaxStringBytes) throw new InvalidDataException($"GGUF string length out of range: {length}.");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static bool Skip(BinaryReader reader, uint type)
    {
        switch (type)
        {
            case TypeUint8:
            case TypeInt8:
            case TypeBool:
                reader.BaseStream.Position += 1;
                return true;
            case TypeUint16:
            case TypeInt16:
                reader.BaseStream.Position += 2;
                return true;
            case TypeUint32:
            case TypeInt32:
            case TypeFloat32:
                reader.BaseStream.Position += 4;
                return true;
            case TypeUint64:
            case TypeInt64:
            case TypeFloat64:
                reader.BaseStream.Position += 8;
                return true;
            case TypeString:
                ReadString(reader);
                return true;
            case TypeArray:
            {
                var element = reader.ReadUInt32();
                var count = reader.ReadInt64();
                if (count is < 0 or > MaxArrayItems) return false;
                if (element == TypeString)
                {
                    for (long index = 0; index < count; index++) ReadString(reader);
                    return true;
                }

                var size = ScalarSize(element);
                if (size == 0) return false;
                reader.BaseStream.Position += count * size;
                return true;
            }
            default:
                return false;
        }
    }

    private static int ScalarSize(uint type) => type switch
    {
        TypeUint8 or TypeInt8 or TypeBool => 1,
        TypeUint16 or TypeInt16 => 2,
        TypeUint32 or TypeInt32 or TypeFloat32 => 4,
        TypeUint64 or TypeInt64 or TypeFloat64 => 8,
        _ => 0,
    };
}
