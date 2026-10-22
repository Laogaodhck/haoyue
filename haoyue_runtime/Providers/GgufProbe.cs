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

    /// <summary>
    /// GGUF header 中 Haoyue 需要的字段。除注册用的三项外，含自适应装载档位（P3）做
    /// KV cache 显存估算所需的张量结构：block_count（层数）、head_count（注意力头数）、
    /// head_count_kv（KV 头数，GQA 模型远小于头数）、key/value_length（每头维度）、
    /// embedding_length（隐藏维度，head_count_kv 缺失时按 GQA 经验值估算 KV 宽度）。
    /// Version 为 GGUF 格式版本（2/3 已支持；更高版本由 ModelCompatibility 早期拒绝，
    /// 而不是让 llama.cpp 在加载期给出难懂的原生错误）。
    /// <see cref="AttentionLayerCount"/> 仅当 head_count_kv 以逐层数组形式给出
    /// （hybrid 架构，如 nemotron_h：Mamba 层记 0）时有值。
    /// </summary>
    public sealed record Header(
        string? Architecture = null,
        string? Name = null,
        long? ContextLength = null,
        long? BlockCount = null,
        long? HeadCount = null,
        long? HeadCountKv = null,
        long? KeyLength = null,
        long? ValueLength = null,
        long? EmbeddingLength = null,
        int? Version = null,
        long? AttentionLayerCount = null);

    public static Header Read(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream);
            var magic = reader.ReadUInt32();
            var version = reader.ReadUInt32();
            if (magic != Magic || version < 2)
                return new(null, null, null);

            reader.ReadUInt64(); // tensor count
            var pairs = reader.ReadInt64();
            if (pairs is < 0 or > 100_000)
                return new(null, null, null);

            string? architecture = null;
            string? name = null;
            long? contextLength = null;
            long? blockCount = null;
            long? headCount = null;
            long? headCountKv = null;
            long? attentionLayerCount = null;
            long? keyLength = null;
            long? valueLength = null;
            long? embeddingLength = null;

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
                else if (IsNumericTensorField(key, type, ".block_count"))
                    blockCount = ReadNumeric(reader, type);
                // 逐层 KV 头数组（hybrid 架构：nemotron_h 等按层给出 KV 头数，Mamba 层记 0）。
                // 必须先于标量分支：IsNumericTensorField 不收数组类型，顺序颠倒会漏解析。
                else if (type == TypeArray
                         && key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal))
                {
                    var perLayer = ReadNumericArray(reader);
                    if (perLayer is null) break; // 元素类型非数值：流位置不可信，停止解析
                    headCountKv = perLayer.Value.Max;
                    attentionLayerCount = perLayer.Value.NonZeroCount;
                }
                // head_count_kv 必须先于 head_count 匹配：后者是前者的后缀，
                // 顺序颠倒会把 GQA 的 KV 头数错记为全量头数。
                else if (IsNumericTensorField(key, type, ".attention.head_count_kv"))
                    headCountKv = ReadNumeric(reader, type);
                else if (IsNumericTensorField(key, type, ".attention.head_count"))
                    headCount = ReadNumeric(reader, type);
                else if (IsNumericTensorField(key, type, ".attention.key_length"))
                    keyLength = ReadNumeric(reader, type);
                else if (IsNumericTensorField(key, type, ".attention.value_length"))
                    valueLength = ReadNumeric(reader, type);
                else if (IsNumericTensorField(key, type, ".embedding_length"))
                    embeddingLength = ReadNumeric(reader, type);
                else if (!Skip(reader, type))
                    break;
            }

            return new(architecture, name, contextLength is > 0 ? contextLength : null,
                blockCount, headCount, headCountKv, keyLength, valueLength, embeddingLength,
                version > int.MaxValue ? null : (int)version, attentionLayerCount);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or DecoderFallbackException)
        {
            return new(null, null, null);
        }
    }

    private static bool IsNumericTensorField(string key, uint type, string suffix) =>
        type is TypeUint32 or TypeInt32 or TypeUint64 && key.EndsWith(suffix, StringComparison.Ordinal);

    private static long ReadNumeric(BinaryReader reader, uint type) => type switch
    {
        TypeUint64 => (long)reader.ReadUInt64(),
        TypeUint32 => reader.ReadUInt32(),
        TypeInt32 => reader.ReadInt32(),
        _ => 0,
    };

    /// <summary>
    /// 读取数值数组元数据（当前唯一用途：.attention.head_count_kv 的逐层形式）。
    /// 返回（最大值, 非零条目数）：hybrid 架构（nemotron_h / jamba 等）按层记录 KV
    /// 头数，无注意力的层（Mamba/SSM）记 0——非零条目数即真实承担 KV 的层数，
    /// 直接决定 KV cache 显存估算；用 block_count 全量估算会把 4 层注意力高估成
    /// 42 层，档位决策从「KV 驻显存」误降到「KV 留系统内存」。
    /// 元素类型非数值或数量非法时返回 null（流位置不可信，调用方须停止解析）。
    /// </summary>
    private static (long Max, long NonZeroCount)? ReadNumericArray(BinaryReader reader)
    {
        var element = reader.ReadUInt32();
        var count = reader.ReadInt64();
        if (count is < 0 or > MaxArrayItems) return null;
        long max = 0;
        long nonZero = 0;
        for (long index = 0; index < count; index++)
        {
            long value;
            switch (element)
            {
                case TypeUint8 or TypeInt8 or TypeBool:
                    value = reader.ReadByte();
                    break;
                case TypeUint16 or TypeInt16:
                    value = reader.ReadUInt16();
                    break;
                case TypeUint32 or TypeInt32:
                    value = reader.ReadUInt32();
                    break;
                case TypeUint64 or TypeInt64:
                    value = (long)reader.ReadUInt64();
                    break;
                default:
                    return null; // 字符串/浮点/未知元素类型：无数值语义
            }
            if (value > max) max = value;
            if (value > 0) nonZero++;
        }
        return (max, nonZero);
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
