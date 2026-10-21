using Haoyue.Runtime.Data;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 结构感知分块器（4.1）：代码围栏/表格/列表组不可断，标题开新块，目标 3000-4000
/// 区间 + 200 重叠，超长无结构块才硬切。变异覆盖：空段 / 6000 边界 / 硬切 / CJK /
/// 表格跨段 / 列表组 / 200 上限。
/// </summary>
public sealed class KnowledgeChunkingTests
{
    private static string Repeat(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    [Fact]
    public void EmptyInput_ReturnsNoChunks()
    {
        Assert.Empty(KnowledgeImport.SplitChunks(""));
        Assert.Empty(KnowledgeImport.SplitChunks("   \n\n  \n"));
    }

    [Fact]
    public void PlainParagraphs_MergeIntoTargetRange()
    {
        // 每段约 500 字符：目标 3500 → 每 chunk 约 7 段（3000-4000 区间），而不是旧
        // 口径的顶满 6000。
        var text = string.Join("\n\n", Enumerable.Range(0, 20)
            .Select(i => $"第{i}段。" + Repeat("内容", 240)));
        var chunks = KnowledgeImport.SplitChunks(text);

        Assert.True(chunks.Count >= 3, $"expected multiple chunks, got {chunks.Count}");
        // 除最后一块外都应落在目标区间附近（+ 重叠余量），远小于硬上限。
        Assert.All(chunks.Take(chunks.Count - 1), chunk =>
            Assert.InRange(chunk.Length, 3000, KnowledgeImport.TargetChunkSize + KnowledgeImport.ChunkOverlap + 200));
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= KnowledgeImport.ChunkSize, $"chunk too large: {chunk.Length}"));
    }

    [Fact]
    public void CodeFence_StaysIntactWithinOneChunk()
    {
        // 代码围栏必须整体进同一个 chunk，开闭标记不跨块。
        var fence = "```csharp\n" + string.Join('\n', Enumerable.Range(0, 40).Select(i => $"var v{i} = {i};")) + "\n```";
        var text = "介绍段落。\n\n" + fence + "\n\n结尾段落。";
        var chunks = KnowledgeImport.SplitChunks(text);

        var owner = chunks.Single(chunk => chunk.Contains("var v20", StringComparison.Ordinal));
        Assert.Contains("```csharp", owner, StringComparison.Ordinal);
        Assert.Contains("var v0 =", owner, StringComparison.Ordinal);
        Assert.Contains("var v39 =", owner, StringComparison.Ordinal);
        Assert.Contains("```", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void UnclosedFence_IsKeptWholeInsteadOfBroken()
    {
        // 未闭合围栏收到文末：绝不能把代码行当普通段落打散。
        var fence = "```python\nprint('a')\nprint('b')\nprint('c')\n";
        var chunks = KnowledgeImport.SplitChunks("前置说明。\n\n" + fence);

        var owner = chunks.Single(chunk => chunk.Contains("print('a')", StringComparison.Ordinal));
        Assert.Contains("print('c')", owner, StringComparison.Ordinal);
    }

    [Fact]
    public void TableRows_StayTogether()
    {
        var table = string.Join('\n', new[]
        {
            "| 列A | 列B |",
            "|---|---|",
            "| a1 | b1 |",
            "| a2 | b2 |",
            "| a3 | b3 |",
        });
        var text = "表格前说明。\n\n" + table + "\n\n表格后说明。";
        var chunks = KnowledgeImport.SplitChunks(text);

        var owner = chunks.Single(chunk => chunk.Contains("| a2 | b2 |", StringComparison.Ordinal));
        Assert.All(new[] { "| 列A | 列B |", "| a1 | b1 |", "| a3 | b3 |" },
            row => Assert.Contains(row, owner, StringComparison.Ordinal));
    }

    [Fact]
    public void ListGroup_StaysTogether()
    {
        var list = string.Join('\n', Enumerable.Range(1, 8).Select(i => $"- 条目{i}：" + Repeat("细节", 30)));
        var chunks = KnowledgeImport.SplitChunks("列表前的长说明。" + Repeat("说明", 400) + "\n\n" + list);

        var owner = chunks.Single(chunk => chunk.Contains("- 条目1：", StringComparison.Ordinal));
        Assert.All(Enumerable.Range(1, 8), i => Assert.Contains($"- 条目{i}：", owner, StringComparison.Ordinal));
        // 有序列表同样成组。
        var ordered = "1. 第一步\n2. 第二步\n3. 第三步";
        var chunks2 = KnowledgeImport.SplitChunks(ordered);
        var owner2 = chunks2.Single();
        Assert.Contains("3. 第三步", owner2, StringComparison.Ordinal);
    }

    [Fact]
    public void Heading_StartsFreshChunk_WithoutOverlapPrefix()
    {
        // 标题开启新主题：新 chunk 不带上一块的重叠尾巴，且标题与其后内容同块。
        var text = "## 第一章\n\n" + Repeat("甲", 3300) + "\n\n## 第二章\n\n" + Repeat("乙", 500);
        var chunks = KnowledgeImport.SplitChunks(text);

        Assert.True(chunks.Count >= 2);
        var second = chunks[1];
        Assert.StartsWith("## 第二章", second, StringComparison.Ordinal);
        Assert.DoesNotContain("甲", second, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedParagraph_IsHardSplit_WithOverlap()
    {
        // 单段 15000 字符（无结构边界）→ 硬切：每片 ≤ 6000，相邻切片重叠 200。
        var text = Repeat("字", 15000);
        var chunks = KnowledgeImport.SplitChunks(text);

        Assert.True(chunks.Count >= 3, $"expected hard-split pieces, got {chunks.Count}");
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= KnowledgeImport.ChunkSize, $"piece too large: {chunk.Length}"));
        // 相邻切片重叠：第 N 片尾部 200 字符出现在第 N+1 片头部。
        for (var i = 1; i < chunks.Count; i++)
        {
            var tail = chunks[i - 1][^KnowledgeImport.ChunkOverlap..];
            Assert.StartsWith(tail, chunks[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExactlyChunkSize_Paragraph_BecomesSingleChunk()
    {
        // 恰好 6000 的单段：不触发硬切（> 判断），也不被拆散。
        var text = Repeat("字", KnowledgeImport.ChunkSize);
        var chunks = KnowledgeImport.SplitChunks(text);
        var chunk = Assert.Single(chunks);
        Assert.Equal(KnowledgeImport.ChunkSize, chunk.Length);
    }

    [Fact]
    public void CjkContent_ChunksPreserveTextAndOverlap()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 10)
            .Select(i => $"小节{i}。" + Repeat("中文内容测试", 90)));
        var chunks = KnowledgeImport.SplitChunks(text);

        Assert.True(chunks.Count >= 2);
        // 全文按序拼接后内容不丢失（重叠允许重复，但不允许缺）。
        var joined = string.Join("", chunks.Select((chunk, index) =>
            index == 0 ? chunk : chunk.Replace(chunks[index - 1][^40..], "", StringComparison.Ordinal)));
        Assert.Contains("小节0。", joined, StringComparison.Ordinal);
        Assert.Contains("小节9。", joined, StringComparison.Ordinal);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= KnowledgeImport.ChunkSize));
    }

    [Fact]
    public void ChunkCount_IsCappedAtLimit()
    {
        // 200 chunk 上限仍然生效：超长文本不会被切成无限块。
        var text = string.Join("\n\n", Enumerable.Range(0, 4000)
            .Select(i => $"段{i}。" + Repeat("内容", 240)));
        var chunks = KnowledgeImport.SplitChunks(text);

        Assert.True(chunks.Count <= KnowledgeImport.MaxChunksPerFile);
    }

    [Fact]
    public void AdjacentChunks_CarryOverlap()
    {
        // 相邻普通续块共享重叠：后一块的开头片段必须出现在前一块中（行对齐重叠的
        // 弱断言，不重复实现私有截取逻辑）。
        var text = string.Join("\n\n", Enumerable.Range(0, 12)
            .Select(i => Repeat($"段{i}", 300)));
        var chunks = KnowledgeImport.SplitChunks(text);

        Assert.True(chunks.Count >= 3, $"expected multiple chunks, got {chunks.Count}");
        for (var i = 1; i < chunks.Count; i++)
        {
            var head = chunks[i].Length > 40 ? chunks[i][..40] : chunks[i];
            Assert.Contains(head, chunks[i - 1], StringComparison.Ordinal);
        }
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= KnowledgeImport.ChunkSize));
    }
}
