using Microsoft.Data.Sqlite;

namespace Haoyue.Runtime.Data;

/// <summary>
/// H-E：per-model token 估算实测校准。ContextPlanner 的字符比启发式（Latin 4.0 /
/// CJK 1.5）对代码密集文本偏差可达 ±50-100%：低估→上下文超限 400，高估→窗口浪费、
/// 过早压缩。真实 prompt tokens 每次请求都有（provider 回传），本 store 把
/// actual/estimated 偏差以 EWMA 平滑持久化，EstimateTokens 按模型乘系数——
/// 数据链路原本就存在，缺的只是这个反馈环。
/// </summary>
public sealed class ModelTokenStats
{
    /// <summary>EWMA 平滑因子：单次样本至多移动 25%，异常样本不会瞬间改写系数。</summary>
    private const double Alpha = 0.25;

    /// <summary>系数钳制范围：tokenizer 差异再大也不会超出 2.5 倍，防御病态样本
    /// （如 provider 把 cache 折扣计进 InputTokens 造成的假低 ratio）。</summary>
    public const double MinRatio = 0.5;
    public const double MaxRatio = 2.5;

    private readonly HaoyueDatabase _database;

    public ModelTokenStats(HaoyueDatabase database) => _database = database;

    /// <summary>该模型当前的估算校准系数；无观测记录时返回 1.0（纯启发式）。</summary>
    public double GetRatio(string providerId, string modelId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ratio FROM model_token_stats
            WHERE provider_id = $provider AND model_id = $model;
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$model", modelId);
        var value = command.ExecuteScalar();
        // 钳制后的极值（0.5 / 2.5）是合法观测，用闭区间；异常值（NaN/越界）退回 1.0。
        return value is double ratio && ratio is >= MinRatio and <= MaxRatio ? ratio : 1.0;
    }

    /// <summary>
    /// 记录一次观测：<paramref name="actualInputTokens"/> 是 provider 回传的真实
    /// prompt tokens，<paramref name="estimatedPromptTokens"/> 是发送前的本地估算。
    /// 任一侧非正（本地模型不上报 usage、空请求等）时忽略——没有信息的样本只会污染
    /// EWMA。
    /// </summary>
    public void Record(string providerId, string modelId, long actualInputTokens, int estimatedPromptTokens)
    {
        if (actualInputTokens <= 0 || estimatedPromptTokens <= 0) return;
        var sample = Math.Clamp((double)actualInputTokens / estimatedPromptTokens, MinRatio, MaxRatio);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        double updated;
        int samples;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT ratio, samples FROM model_token_stats
                WHERE provider_id = $provider AND model_id = $model;
                """;
            read.Parameters.AddWithValue("$provider", providerId);
            read.Parameters.AddWithValue("$model", modelId);
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                var previous = reader.GetDouble(0);
                var count = reader.GetInt32(1);
                updated = previous + Alpha * (sample - previous);
                samples = count + 1;
            }
            else
            {
                updated = sample;
                samples = 1;
            }
        }

        using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = """
                INSERT INTO model_token_stats(
                    provider_id, model_id, ratio, samples,
                    last_input_tokens, last_estimated_tokens, updated_at)
                VALUES($provider, $model, $ratio, $samples, $actual, $estimated, $updatedAt)
                ON CONFLICT(provider_id, model_id) DO UPDATE SET
                    ratio = excluded.ratio,
                    samples = excluded.samples,
                    last_input_tokens = excluded.last_input_tokens,
                    last_estimated_tokens = excluded.last_estimated_tokens,
                    updated_at = excluded.updated_at;
                """;
            write.Parameters.AddWithValue("$provider", providerId);
            write.Parameters.AddWithValue("$model", modelId);
            write.Parameters.AddWithValue("$ratio", updated);
            write.Parameters.AddWithValue("$samples", samples);
            write.Parameters.AddWithValue("$actual", actualInputTokens);
            write.Parameters.AddWithValue("$estimated", estimatedPromptTokens);
            write.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            write.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Test/inspection hook: drops all calibrated ratios.</summary>
    public void Reset()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM model_token_stats;";
        command.ExecuteNonQuery();
    }
}
