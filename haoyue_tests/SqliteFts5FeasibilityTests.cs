using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Haoyue.Tests;

/// <summary>
/// 4.5 检索两层化（触发式）步骤②：Microsoft.Data.Sqlite 的 FTS5 可用性实测。
/// 卡片要求「实测 FTS5（trigram tokenizer 适配 CJK）可用性」后再决定动不动刀——
/// 本测试就是那个决策输入：FTS5/trigram 任一不可用 → 退化为按 notebook/source
/// 分页加载方案（卡片明确的 fallback）。2000 条合成语料的词法基线数字输出到
/// 测试输出，供后续与「1 万条 p95 &lt; 100ms」的验收目标对照。
/// </summary>
public sealed class SqliteFts5FeasibilityTests(ITestOutputHelper output)
{
    [Fact]
    public void Fts5_Module_IsAvailable()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_compileoption_used('ENABLE_FTS5');";
        var enabled = Convert.ToInt64(command.ExecuteScalar()!);
        Assert.Equal(1, enabled); // FTS5 编译进 e_sqlite3：L1 预筛方案可行
    }

    [Fact]
    public void Trigram_Tokenizer_HandlesCjkSubstringQueries()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS fts USING fts5(title, tokenize='trigram');";
        create.ExecuteNonQuery(); // trigram tokenizer 不可用会在此抛错

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO fts (title) VALUES
              ('知识库检索两层化执行方案'),
              ('daemon 崩溃与桌面卡住排查 runbook'),
              ('quantization advisor for GGUF files');
            """;
        insert.ExecuteNonQuery();

        foreach (var (query, expectCount) in new[] { ("两层化", 1), ("排查 runbook", 1), ("GGUF", 1) })
        {
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT count(*) FROM fts WHERE fts MATCH $q";
            select.Parameters.AddWithValue("$q", $"\"{query}\"");
            var count = Convert.ToInt64(select.ExecuteScalar()!);
            Assert.True(count == expectCount, $"CJK/英文子串查询「{query}」命中 {count} 条（期望 {expectCount}）");
        }
    }

    [Fact]
    public void Baseline_2000SyntheticEntries_LexicalQueryLatency()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE corpus USING fts5(title, body, tokenize='trigram');";
            create.ExecuteNonQuery();
        }

        var random = new Random(2026); // 固定种子：基线可复现
        using (var transaction = connection.BeginTransaction())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO corpus (title, body) VALUES ($t, $b)";
            var title = insert.Parameters.Add("$t", SqliteType.Text);
            var body = insert.Parameters.Add("$b", SqliteType.Text);
            for (var index = 0; index < 2000; index++)
            {
                title.Value = $"条目 {index}：{ChinesePhrase(random)}{EnglishWord(random)}";
                body.Value = string.Concat(Enumerable.Range(0, 8).Select(_ => ChinesePhrase(random) + EnglishWord(random)));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        // 混合 CJK/英文查询 50 轮，报 p50/p95——与「1 万条 p95 <100ms」验收目标对照。
        var queries = Enumerable.Range(0, 50)
            .Select(_ => $"\"{ChinesePhrase(random)}{EnglishWord(random)}\"")
            .ToList();
        var latencies = new List<double>();
        foreach (var query in queries)
        {
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT count(*) FROM corpus WHERE corpus MATCH $q";
            select.Parameters.AddWithValue("$q", query);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            select.ExecuteScalar();
            stopwatch.Stop();
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        latencies.Sort();
        var p50 = latencies[latencies.Count / 2];
        var p95 = latencies[(int)(latencies.Count * 0.95) - 1];
        output.WriteLine($"2000 条语料词法基线：p50 = {p50:F2} ms，p95 = {p95:F2} ms（50 轮混合 CJK/英文查询）");
        // 宽松护栏：不是验收目标本身，只抓病态回退（如 tokenizer 回退成 LIKE 全表扫）。
        Assert.True(p95 < 200, $"2000 条基线 p95 = {p95:F2} ms 异常（阈值 200ms）——FTS5 路径可能退化");
    }

    private static string ChinesePhrase(Random random)
    {
        const string characters = "知识库检索两层化执行方案崩溃桌面排查量化解导故障转移黑板委派配额";
        return new string(Enumerable.Range(0, 4).Select(_ => characters[random.Next(characters.Length)]).ToArray());
    }

    private static string EnglishWord(Random random)
    {
        const string words = "knowledge retrieval failover blackboard delegate quantization watcher runtime";
        var options = words.Split(' ');
        return options[random.Next(options.Length)];
    }
}
