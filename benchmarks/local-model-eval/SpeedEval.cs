using System.Text;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Providers;

namespace Haoyue.Benchmarks.LocalModelEval;

/// <summary>
/// 任务二：响应速度测试。不同输入长度下的预填充速度、首字延迟（TTFT）、解码吞吐；
/// 另测 KV 前缀复用（localPrefixReuse）开启后多轮场景的 TTFT 收益。
/// 速度测试关闭 KV 复用以测量完整管线，复用收益单独成组对比。
/// </summary>
public static class SpeedEval
{
    public sealed record SpeedRecord(
        string Case,
        long PromptTokens,
        double TtftMs,
        double PrefillTps,
        long OutputTokens,
        double DecodeTps,
        double TotalMs,
        long CachedTokens,
        string FinishReason);

    /// <summary>构造指定汉字规模的填充文本（旅游攻略语料，语义真实）。</summary>
    private static string Filler(int targetChars, string seedTopic)
    {
        var paragraphs = new StringBuilder();
        var themes = new[]
        {
            "杭州西湖的晨间游船路线与最佳拍照时段",
            "苏州园林的移步换景布局与季相变化",
            "黄山云海形成的气象条件与观赏窗口期",
            "古镇民宿的选择标准：隔音、供暖与早餐饮食",
            "高铁与自驾在不同行程半径下的时间成本对比",
        };
        var index = 0;
        while (paragraphs.Length < targetChars)
        {
            paragraphs.Append($"【{themes[index % themes.Length]}】");
            paragraphs.Append($"关于{themes[index % themes.Length]}，常见的行程安排是清晨出发以避开人流高峰，");
            paragraphs.Append("上午完成主要景点游览，午后安排茶歇与自由活动，傍晚返回住宿地休整。");
            paragraphs.Append($"这条路线尤其适合首次到访的游客：交通接驳稳定、体力消耗可控，");
            paragraphs.Append("预算方面以人均每日四百元为参考区间，可覆盖门票、餐饮与市内交通。");
            paragraphs.Append($"。本段主题关键词：{seedTopic}。\n");
            index++;
        }
        return paragraphs.ToString(0, Math.Min(targetChars, paragraphs.Length));
    }

    /// <summary>三档输入长度：短（~50 tok）、中（~600 tok）、长（~2200 tok）。</summary>
    public static async Task<IReadOnlyList<SpeedRecord>> RunLengthsAsync(
        LocalLlmClient client, CancellationToken ct)
    {
        var records = new List<SpeedRecord>();
        var cases = new (string Name, int Chars)[]
        {
            ("短输入(~50 tok)", 60),
            ("中输入(~600 tok)", 800),
            ("长输入(~2200 tok)", 3200),
        };

        foreach (var (name, chars) in cases)
        {
            var prompt = Filler(chars, "旅行规划")
                         + "\n\n请用不超过两句话总结以上内容的整体主题，不要展开细节。";
            var gen = await EvalCore.GenerateAsync(
                client,
                [ChatMessage.User(prompt)],
                system: "你是简洁的中文助手，只输出被要求的内容。",
                maxTokens: 96,
                temperature: 0.3,
                ct: ct).ConfigureAwait(false);

            records.Add(new SpeedRecord(
                name, gen.PromptTokens, gen.TtftMs, gen.PrefillTps,
                gen.OutputTokens, gen.DecodeTps, gen.TotalMs, gen.CachedTokens, gen.FinishReason));
            Console.WriteLine($"    [速度] {name}: prompt={gen.PromptTokens} tok, " +
                              $"TTFT={gen.TtftMs:F0}ms, decode={gen.DecodeTps:F1} tok/s");
        }
        return records;
    }

    /// <summary>
    /// KV 前缀复用对比（三段式，避免冷跑污染后续缓存）：
    /// 1) 首问——完整预填充（冷基线）；2) 同对话第二问——命中共享前缀（复用收益）；
    /// 3) 对照组——给共享前缀加随机标记破坏复用，同题强制重新预填充。
    /// </summary>
    public static async Task<IReadOnlyList<SpeedRecord>> RunReuseComparisonAsync(
        LocalLlmClient client, CancellationToken ct)
    {
        var records = new List<SpeedRecord>();
        var sharedContext = Filler(1600, "养生馆运营") +
            "\n\n以上是一份养生馆的运营手册摘要，请基于它回答我接下来的问题。";
        const string question = "手册里提到的客流策略，适合新店吗？一句话回答。";

        var history = new List<ChatMessage> { ChatMessage.User(sharedContext) };

        // 1) 首问：完整预填充
        history.Add(ChatMessage.User("这份手册的核心目标是什么？一句话回答。"));
        var first = await EvalCore.GenerateAsync(
            client, history, null, maxTokens: 64, temperature: 0.3, ct: ct).ConfigureAwait(false);
        records.Add(new SpeedRecord(
            "冷基线·首问完整预填充", first.PromptTokens, first.TtftMs, first.PrefillTps,
            first.OutputTokens, first.DecodeTps, first.TotalMs, first.CachedTokens, first.FinishReason));
        history.Add(ChatMessage.Assistant(first.Answer));
        Console.WriteLine($"    [KV复用] 冷基线: TTFT={first.TtftMs:F0}ms (cached={first.CachedTokens})");

        // 2) 第二问：命中共享前缀
        history.Add(ChatMessage.User(question));
        var warm = await EvalCore.GenerateAsync(
            client, history, null, maxTokens: 64, temperature: 0.3, ct: ct).ConfigureAwait(false);
        records.Add(new SpeedRecord(
            "复用命中·第二问", warm.PromptTokens, warm.TtftMs, warm.PrefillTps,
            warm.OutputTokens, warm.DecodeTps, warm.TotalMs, warm.CachedTokens, warm.FinishReason));
        history.RemoveAt(history.Count - 1);
        Console.WriteLine($"    [KV复用] 复用命中: TTFT={warm.TtftMs:F0}ms (cached={warm.CachedTokens} tok)");

        // 3) 对照组：随机标记破坏复用，同题重新预填充
        var marker = $"[cold-{Guid.NewGuid():N}] ";
        var coldHistory = new List<ChatMessage>
        {
            ChatMessage.User(marker + sharedContext),
            ChatMessage.Assistant(first.Answer),
            ChatMessage.User(question),
        };
        var cold = await EvalCore.GenerateAsync(
            client, coldHistory, null, maxTokens: 64, temperature: 0.3, ct: ct).ConfigureAwait(false);
        records.Add(new SpeedRecord(
            "冷对照·标记破坏复用", cold.PromptTokens, cold.TtftMs, cold.PrefillTps,
            cold.OutputTokens, cold.DecodeTps, cold.TotalMs, cold.CachedTokens, cold.FinishReason));
        Console.WriteLine($"    [KV复用] 冷对照: TTFT={cold.TtftMs:F0}ms (cached={cold.CachedTokens})");

        return records;
    }
}
