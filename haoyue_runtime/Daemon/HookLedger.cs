using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Daemon;

/// <summary>一次钩子执行的台账记录（JSONL 持久化，供「失败可排查」回看）。</summary>
public sealed record HookRunRecord(
    DateTimeOffset At,
    string Trigger,
    string Hook,
    string Target,
    bool Ok,
    double DurationMs,
    string? Error,
    string? Output);

/// <summary>
/// 钩子执行台账：按工作区落 <c>&lt;workspace&gt;/.haoyue/hooks-ledger.jsonl</c>，
/// 无工作区上下文时落用户目录。每条一行 JSON，默认保留最近 2000 条（可配置）；
/// 超预算时把最旧的溢出记录按日期归档到 <c>.haoyue/hooks/archive/</c>，不直接丢弃。
/// 读取容忍坏行。手写 JSON 节点读写，避免 AOT/trimming 下的反射序列化风险。
/// </summary>
public static class HookLedger
{
    public const int DefaultMaxRecords = 2000;
    public const int MinMaxRecords = 200;
    public const int MaxMaxRecords = 10_000;

    private static readonly object Gate = new();

    public static string LedgerFile(string root) =>
        Path.Combine(root, ".haoyue", "hooks-ledger.jsonl");

    public static string ArchiveDir(string root) =>
        Path.Combine(root, ".haoyue", "hooks", "archive");

    public static string ArchiveFile(string root, string yyyyMMdd) =>
        Path.Combine(ArchiveDir(root), $"hooks-{yyyyMMdd}.jsonl");

    /// <summary>把配置容量收敛到安全区间（0/缺失按默认 2000）。</summary>
    public static int ClampMax(int maxRecords) =>
        Math.Clamp(maxRecords <= 0 ? DefaultMaxRecords : maxRecords, MinMaxRecords, MaxMaxRecords);

    /// <summary>从付载中解析工作区根；缺省为用户目录。</summary>
    public static string ResolveRoot(JsonObject? payload)
    {
        var workspace = payload?["workspace"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(workspace) ? HaoyuePaths.Home : workspace!;
    }

    public static void Append(string root, HookRunRecord record, int maxRecords = DefaultMaxRecords)
    {
        var max = ClampMax(maxRecords);
        try
        {
            var file = LedgerFile(root);
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var node = new JsonObject
                {
                    ["at"] = record.At.ToString("O"),
                    ["trigger"] = record.Trigger,
                    ["hook"] = record.Hook,
                    ["target"] = record.Target,
                    ["ok"] = record.Ok,
                    ["durationMs"] = record.DurationMs,
                    ["error"] = record.Error,
                    ["output"] = record.Output,
                };
                File.AppendAllText(file, node.ToJsonString() + Environment.NewLine);
                if (!File.ReadLines(file).Skip(max).Any()) return;

                var lines = File.ReadAllLines(file);
                var overflow = lines.Length - max;
                ArchiveOverflow(root, lines[..overflow]);
                var kept = lines[overflow..];
                var temp = file + ".tmp";
                File.WriteAllLines(temp, kept);
                File.Move(temp, file, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 台账是观测增强：写入失败不能影响主流程。
        }
    }

    /// <summary>按记录时间（at）分组追加到日期归档文件；坏行归入 unknown。</summary>
    private static void ArchiveOverflow(string root, IReadOnlyList<string> lines)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var date = RecordDate(line);
            if (!groups.TryGetValue(date, out var bucket))
            {
                bucket = [];
                groups[date] = bucket;
            }
            bucket.Add(line);
        }
        if (groups.Count == 0) return;
        Directory.CreateDirectory(ArchiveDir(root));
        foreach (var (date, bucket) in groups)
            File.AppendAllLines(ArchiveFile(root, date), bucket);
    }

    private static string RecordDate(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is JsonObject node
                && DateTimeOffset.TryParse(node["at"]?.GetValue<string>(), out var at))
                return at.ToLocalTime().ToString("yyyyMMdd");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // At 字段损坏时按 unknown 归档，仍保留原始行。
        }
        return "unknown";
    }

    public static IReadOnlyList<HookRunRecord> Read(string root, int limit = 50, int maxRecords = DefaultMaxRecords)
    {
        var file = LedgerFile(root);
        if (!File.Exists(file)) return [];
        try
        {
            var records = new List<HookRunRecord>();
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject node) continue;
                    records.Add(new HookRunRecord(
                        DateTimeOffset.TryParse(node["at"]?.GetValue<string>(), out var at) ? at : default,
                        node["trigger"]?.GetValue<string>() ?? "",
                        node["hook"]?.GetValue<string>() ?? "",
                        node["target"]?.GetValue<string>() ?? "",
                        node["ok"]?.GetValue<bool>() ?? false,
                        node["durationMs"]?.GetValue<double>() ?? 0,
                        node["error"]?.GetValue<string>(),
                        node["output"]?.GetValue<string>()));
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
                {
                    // 半行/损坏行跳过，不影响其余台账。
                }
            }
            return [.. records.OrderByDescending(record => record.At).Take(Math.Clamp(limit, 1, ClampMax(maxRecords)))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
