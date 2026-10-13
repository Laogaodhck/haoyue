using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Providers;

namespace Haoyue.Tests;

/// <summary>
/// M-C：视觉回合历史剪裁。全量历史含图重传会让延迟/费用随会话长度线性增长，
/// 过期截图还会把当前关键截图挤出窗口预算。剪裁保留 ViewedImages 引用，
/// 模型仍知道自己看过什么，只是不再重新上传 base64。
/// </summary>
public sealed class VisionHistoryPruneTests
{
    private static ChatImageAttachment Image(string id, int sizeBytes = 1_000) => new(
        id, $"{id}.png", "image/png",
        // 每张图内容唯一（id 嵌入载荷），否则内容哈希去重会正确地把它们视为同一张图。
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(id.PadRight(sizeBytes, '.'))),
        sizeBytes);

    private static ChatMessage WithImage(string text, params ChatImageAttachment[] images) =>
        ChatMessage.User(text, images);

    [Fact]
    public void TextOnlyHistory_PassesThroughUnchanged()
    {
        var messages = new[] { ChatMessage.User("a"), ChatMessage.Assistant("b") };
        var result = Agent.PruneVisionHistory(messages, imageTokenBudget: 4_000);
        Assert.Same(messages, result);
    }

    [Fact]
    public void OlderImageMessages_BeyondRecentWindow_AreStripped()
    {
        // 10 条含图消息：只保留最近 6 条的载荷，更早的 4 条剥离（ViewedImages 仍在）。
        var messages = Enumerable.Range(0, 10)
            .Select(i => WithImage($"m{i}", Image($"img-{i}")))
            .ToList();

        var result = Agent.PruneVisionHistory(messages, imageTokenBudget: 100_000);

        Assert.Equal(messages.Count, result.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (i >= 4)
                Assert.True(result[i].Images is { Count: > 0 }, $"message {i} should keep its image");
            else
                Assert.Null(result[i].Images);
        }
        // 被剥离的消息文本不丢。
        Assert.Equal("m0", result[0].Text);
    }

    [Fact]
    public void DuplicateImageContent_IsUploadedOnce()
    {
        var shared = Image("shared", 2_000);
        var messages = new[]
        {
            WithImage("first", shared),
            WithImage("second", shared),
            WithImage("third", shared),
        };

        var result = Agent.PruneVisionHistory(messages, imageTokenBudget: 100_000);

        // 最新一次出现保留载荷；更早的重复被去重剥掉。
        Assert.True(result[2].Images is { Count: > 0 });
        Assert.Null(result[1].Images);
        Assert.Null(result[0].Images);
    }

    [Fact]
    public void TokenBudget_StopsKeepingOlderImages()
    {
        // 每张图 1000 字节 base64 → 约 1002 原始字节 → EstimateImageTokens = 300 tokens。
        // 预算 500：保留最新 1 条（300）后，第二条（+300=600）超出 → 停止向更早保留。
        var messages = Enumerable.Range(0, 5)
            .Select(i => WithImage($"m{i}", Image($"img-{i}", 1_000)))
            .ToList();

        var result = Agent.PruneVisionHistory(messages, imageTokenBudget: 500);

        Assert.True(result[4].Images is { Count: > 0 });
        for (var i = 0; i < 4; i++)
            Assert.Null(result[i].Images);
    }

    [Fact]
    public void TotalImageCount_IsCapped()
    {
        // 每条消息 2 张图 × 10 条 = 20 张；单请求上限 8 张。
        var messages = Enumerable.Range(0, 10)
            .Select(i => WithImage($"m{i}", Image($"a-{i}"), Image($"b-{i}")))
            .ToList();

        var result = Agent.PruneVisionHistory(messages, imageTokenBudget: 1_000_000);

        var kept = result.Sum(m => m.Images?.Count ?? 0);
        Assert.True(kept <= Agent.MaxImagesPerRequest, $"kept {kept} images, cap is {Agent.MaxImagesPerRequest}");
        // 最新的图优先保留。
        Assert.True(result[9].Images is { Count: > 0 });
    }

    [Fact]
    public void WithoutImages_StillStripsEverythingForTextTurns()
    {
        var messages = new[] { WithImage("m0", Image("img-0")), ChatMessage.Assistant("ok") };
        var result = Agent.WithoutImages(messages);
        Assert.Null(result[0].Images);
        Assert.Equal("m0", result[0].Text);
    }
}
