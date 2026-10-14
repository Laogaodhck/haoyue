using System.Text.Json.Nodes;
using Haoyue.Runtime;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Haoyue.Tests;

/// <summary>
/// 跨会话记忆/检索底座：agent-eval 的 memory-recall 与 knowledge-retrieval 任务断言
/// 「上一回合沉淀、下一回合（全新会话 + 全新运行时实例）才见得出得来」。
/// 那两个任务要真跑模型，CI 里跑不动；这里用同一套装配方式给出确定性证明：
/// 换一个 HaoyueRuntime 实例、同一个工作区目录，记忆文件与知识库都必须还在。
/// 否则评测任务失败时分不清是模型没召回还是底座没持久化。
/// </summary>
public class CrossSessionMemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-xmem-" + Guid.NewGuid().ToString("N"));

    public CrossSessionMemoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private WorkspaceInfo Workspace() => new WorkspaceManager().Detect(_dir);

    /// <summary>与评测 harness 一致的装配：库文件落在工作区内，实例间靠磁盘交接。</summary>
    private HaoyueRuntime OpenRuntime(string owner) => HaoyueRuntime.CreateIsolated(
        Workspace(), new FileLockCoordinator(), owner,
        services =>
        {
            services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "eval.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
        });

    [Fact]
    public async Task MemorySaveTool_WrittenByOneRuntime_IsReadableByAnother()
    {
        await using (var first = OpenRuntime("t0"))
        {
            var tool = first.Tools.Resolve("memory_save");
            Assert.NotNull(tool);
            var context = ToolContextFor(first, Workspace());
            var saved = await tool!.ExecuteAsync(new JsonObject
            {
                ["content"] = "发布门禁命令是 dotnet test Haoyue.slnx -c Release",
                ["topic"] = "发布",
            }, context, CancellationToken.None);
            Assert.True(saved.Success, saved.Output);
        }

        var memoryFile = Workspace().MemoryFile;
        Assert.True(File.Exists(memoryFile), $"memory_save 之后 MEMORY.md 应落在工作区：{memoryFile}");

        await using var second = OpenRuntime("t1");
        // 注入侧：系统提示取的是 WorkspaceManager.LoadMemory（Agent.Prompting 的 memory 来源），
        // 第二个实例读不到就等于模型「召回失败」其实是「没送到」。
        var injected = new WorkspaceManager().LoadMemory(Workspace());
        Assert.NotNull(injected);
        Assert.Contains("dotnet test Haoyue.slnx", injected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnowledgeSaveTool_WrittenByOneRuntime_IsSearchedByAnother()
    {
        await using (var first = OpenRuntime("t0"))
        {
            var save = first.Tools.Resolve("knowledge_save");
            Assert.NotNull(save);
            var saved = await save!.ExecuteAsync(new JsonObject
            {
                ["title"] = "导出接口超时口径",
                ["content"] = "导出任务超时上限 93 秒，超过即中止并标记 partial",
                ["tags"] = "导出,超时",
            }, ToolContextFor(first, Workspace()), CancellationToken.None);
            Assert.True(saved.Success, saved.Output);
        }

        await using var second = OpenRuntime("t1");
        var search = second.Tools.Resolve("knowledge_search");
        Assert.NotNull(search);
        var result = await search!.ExecuteAsync(
            new JsonObject { ["query"] = "导出任务超时" }, ToolContextFor(second, Workspace()), CancellationToken.None);

        Assert.True(result.Success, result.Output);
        Assert.Contains("93", result.Output, StringComparison.Ordinal);
    }

    private static Haoyue.Runtime.Tools.ToolContext ToolContextFor(HaoyueRuntime runtime, WorkspaceInfo workspace) => new()
    {
        Workspace = workspace,
        Events = runtime.Events,
        Agent = runtime.ConfigStore.Config.Agent,
    };
}
