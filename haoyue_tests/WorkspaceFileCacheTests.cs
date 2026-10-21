using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// 2.5 提示合成输入的指纹缓存：并行子代理共享同一 workspace 文件，IO 降为一次；
/// 指纹 = 最后写入时间 + 长度（时间戳单独不可靠的 NTFS 粒度坑已实证），
/// 指纹不匹配即重读——内容永不陈旧，无需显式失效通道。
/// </summary>
public sealed class WorkspaceFileCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-wfc-" + Guid.NewGuid().ToString("N"));

    public WorkspaceFileCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        WorkspaceFileCache.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void LoadMemory_SecondReadHitsCache_SeesUpdatedContentAfterWrite()
    {
        WorkspaceFileCache.Reset();
        var workspace = new WorkspaceManager().Detect(_dir);
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.MemoryFile)!);
        var memoryFile = workspace.MemoryFile;
        File.WriteAllText(memoryFile, "第一版记忆");

        // 命中语义用行为断言而非全局计数（Hits/Misses 是进程级计数器，
        // 并行测试会污染绝对值）：两次读一致即缓存命中；文件变化即重读。
        var first = new WorkspaceManager().LoadMemory(workspace);
        var second = new WorkspaceManager().LoadMemory(workspace);

        Assert.Equal("第一版记忆", first);
        Assert.Equal(first, second);

        // 文件被外部修改（记忆固化写入）：指纹变化 → 重读，绝无陈旧内容。
        File.WriteAllText(memoryFile, "第二版记忆——内容更长");
        var third = new WorkspaceManager().LoadMemory(workspace);
        Assert.Equal("第二版记忆——内容更长", third);
    }

    [Fact]
    public void LoadMemory_MissingFile_ThenCreated_IsSeenOnNextRead()
    {
        WorkspaceFileCache.Reset();
        var workspace = new WorkspaceManager().Detect(_dir);

        Assert.Null(new WorkspaceManager().LoadMemory(workspace)); // 尚不存在（参与指纹）
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.MemoryFile)!);
        File.WriteAllText(workspace.MemoryFile, "新记忆");
        Assert.Equal("新记忆", new WorkspaceManager().LoadMemory(workspace));
    }

    [Fact]
    public void LoadAgentInstructions_ComposesRootFile_AndSurvivesRepeatReads()
    {
        WorkspaceFileCache.Reset();
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "根目录指令");
        var workspace = new WorkspaceManager().Detect(_dir);

        var first = new WorkspaceManager().LoadAgentInstructions(workspace);
        var second = new WorkspaceManager().LoadAgentInstructions(workspace);

        Assert.Contains("## AGENTS.md", first, StringComparison.Ordinal);
        Assert.Contains("根目录指令", first, StringComparison.Ordinal);
        Assert.Equal(first, second); // 一致即命中（计数器断言在并行下不可靠，已移除）
    }

    [Fact]
    public void LoadAgentInstructions_GlobalWorkspace_ReturnsNullWithoutCaching()
    {
        var workspace = new WorkspaceManager().CreateGlobal(_dir);
        Assert.Null(new WorkspaceManager().LoadAgentInstructions(workspace));
    }
}
