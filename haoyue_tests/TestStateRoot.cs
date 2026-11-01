using System.Runtime.CompilerServices;

namespace Haoyue.Tests;

/// <summary>
/// 状态根隔离：<c>HaoyuePaths</c> 首次访问时读取 HAOYUE_HOME，而这里在模块加载阶段
/// （任何测试代码之前）把它指向进程专属的临时目录。否则运行测试会往开发者真实的
/// ~/.haoyue 写入用量日志、会话库与凭证条目，污染本机数据。
/// </summary>
internal static class TestStateRoot
{
    internal static string Root { get; } =
        Path.Combine(Path.GetTempPath(), $"haoyue-tests-state-{Environment.ProcessId}-{Guid.NewGuid():N}");

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("HAOYUE_HOME", Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    private static void Cleanup()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
