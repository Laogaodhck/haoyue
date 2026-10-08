using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Haoyue.Runtime.Providers;

namespace Haoyue.Benchmarks.LocalModelEval;

/// <summary>
/// 任务一：对话流畅性测试。三个多轮场景，覆盖上下文记忆、话题切换回跳、干扰恢复。
/// 每轮记录量化指标（distinct-2、最长重复片段、跨轮复读、检查点命中），并保存完整对话原文。
/// </summary>
public static class FluencyEval
{
    private static readonly string SystemPrompt =
        "你是浩玥（Haoyue），一个简洁、自然的中文助手。直接回答问题，不要编造信息。";

    /// <summary>每轮的检查点：多组关键词，每组内任一命中，全部组命中才算通过。</summary>
    public sealed record TurnExpectation(string Prompt, string[][] CheckpointGroups);

    public sealed record Scenario(string Name, string Description, IReadOnlyList<TurnExpectation> Turns);

    public sealed record TurnRecord(
        int Turn,
        string User,
        string Assistant,
        double Distinct1,
        double Distinct2,
        int MaxRepeatRun,
        int CrossTurnEcho,
        bool CheckpointHit,
        long PromptTokens,
        long OutputTokens,
        double TtftMs,
        double TotalMs);

    public sealed record ScenarioResult(
        string Name,
        string Description,
        IReadOnlyList<TurnRecord> Turns,
        double CheckpointHitRate,
        double AvgDistinct2,
        double MinDistinct2,
        double LengthVariation,
        int TotalEchoPairs);

    public static readonly IReadOnlyList<Scenario> Scenarios =
    [
        new("场景A·信息记忆与追问", "用户提供个人信息，后续多轮追问检验上下文保持",
        [
            new("你好！我叫沈墨，在做艾灸养生馆的项目，馆名叫「泓艾堂」，开在杭州滨江区。请记住这些信息。",
                Array.Empty<string[]>()),
            new("我刚才说我叫什么名字？我的养生馆叫什么、开在哪里？",
                [["沈墨"], ["泓艾堂"], ["滨江", "杭州"]]),
            new("如果我的馆里来了一个肩颈僵硬的顾客，你建议先做什么？",
                Array.Empty<string[]>()),
            new("回到之前的问题：请用我馆里的店名造一个欢迎语，要包含店名。",
                [["泓艾堂"]]),
            new("最后确认：把今天告诉你的三件信息（姓名、店名、地址）完整复述一遍。",
                [["沈墨"], ["泓艾堂"], ["滨江", "杭州"]]),
        ]),
        new("场景B·概念解释与追问深挖", "同一话题逐层深入，检验语义连贯与术语一致",
        [
            new("请解释什么是向量数据库，两三句话即可。",
                Array.Empty<string[]>()),
            new("它和传统关系型数据库（比如 PostgreSQL）最核心的区别是什么？",
                [["向量"], ["相似", "嵌入", "embedding"]]),
            new("刚才你提到的那个区别，会在什么业务场景里真正用到？举个具体例子。",
                Array.Empty<string[]>()),
            new("用完全不懂技术的餐厅老板能听懂的话，把上面三轮的结论重新讲一遍。",
                Array.Empty<string[]>()),
        ]),
        new("场景C·干扰与话题回跳", "中途插入无关话题，再要求回到原话题，检验抗干扰与回跳能力",
        [
            new("我在策划一场 60 人规模的艾灸养生讲座，帮我列 3 个讲座主题方向。",
                Array.Empty<string[]>()),
            new("顺便问一下，杭州今天天气怎么样？",
                Array.Empty<string[]>()),
            new("帮我写一句火锅店的广告语。",
                Array.Empty<string[]>()),
            new("回到讲座的事：根据你刚才给的 3 个方向，帮我选一个最适合吸引中年客群的，并说明理由。",
                Array.Empty<string[]>()),
            new("再确认一次：你给我的 3 个主题方向分别是什么？",
                Array.Empty<string[]>()),
        ]),
    ];

    public static async Task<IReadOnlyList<ScenarioResult>> RunAsync(
        LocalLlmClient client, CancellationToken ct)
    {
        var results = new List<ScenarioResult>();
        foreach (var scenario in Scenarios)
            results.Add(await RunScenarioAsync(client, scenario, ct).ConfigureAwait(false));
        return results;
    }

    private static async Task<ScenarioResult> RunScenarioAsync(
        LocalLlmClient client, Scenario scenario, CancellationToken ct)
    {
        var turns = new List<TurnRecord>();
        var history = new List<ChatMessage>();
        string? previousAnswer = null;
        var lengths = new List<int>();
        var echoTotal = 0;

        for (var i = 0; i < scenario.Turns.Count; i++)
        {
            var expectation = scenario.Turns[i];
            history.Add(ChatMessage.User(expectation.Prompt));

            var gen = await EvalCore.GenerateAsync(
                client, history, SystemPrompt, maxTokens: 320, temperature: 0.6, ct: ct).ConfigureAwait(false);

            history.Add(ChatMessage.Assistant(gen.Answer));

            var (maxRun, _) = FluencyMetrics.LongestRepeatedRun(gen.Answer);
            var echo = previousAnswer is null ? 0 : FluencyMetrics.CrossTurnEcho(previousAnswer, gen.Answer);
            echoTotal += echo;
            var hit = expectation.CheckpointGroups.Length == 0
                || expectation.CheckpointGroups.All(group => FluencyMetrics.HitCheckpoint(gen.Answer, group));
            lengths.Add(gen.Answer.Length);

            turns.Add(new TurnRecord(
                i + 1,
                expectation.Prompt,
                gen.Answer,
                FluencyMetrics.DistinctN(gen.Answer, 1),
                FluencyMetrics.DistinctN(gen.Answer, 2),
                maxRun,
                echo,
                hit,
                gen.PromptTokens,
                gen.OutputTokens,
                gen.TtftMs,
                gen.TotalMs));

            previousAnswer = gen.Answer;
            Console.WriteLine($"    [{scenario.Name}] turn {i + 1}/{scenario.Turns.Count} 完成 " +
                              $"(distinct2={turns[^1].Distinct2:F3}, checkpoint={(hit ? "PASS" : "MISS")})");
        }

        var hitRate = (double)turns.Count(t => t.CheckpointHit) / turns.Count;
        return new ScenarioResult(
            scenario.Name,
            scenario.Description,
            turns,
            hitRate,
            turns.Average(t => t.Distinct2),
            turns.Min(t => t.Distinct2),
            FluencyMetrics.LengthVariation(lengths),
            echoTotal);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void SaveResults(IReadOnlyList<ScenarioResult> results, string fileName)
    {
        Directory.CreateDirectory(EvalCore.ResultsDir);
        File.WriteAllText(
            Path.Combine(EvalCore.ResultsDir, fileName),
            JsonSerializer.Serialize(results, JsonOptions),
            new UTF8Encoding(false));
    }
}
