namespace Haoyue.Runtime.Tools.HeadlessBrowser;

/// <summary>
/// 定位系统已有的 Chromium 系浏览器（Edge → Chrome → Chromium 优先级）。browser 工具
/// 采用"内置无头内核复用系统浏览器"的策略：不随包分发 Chromium（数百 MB），而是驱动
/// Windows 自带的 Edge / 用户安装的 Chrome，以 --headless=new 启动专用实例。
/// 探测路径集中在此，支持注入 exists/env 以便单元测试。
/// </summary>
internal static class BrowserLocator
{
    public static string? Locate() =>
        Locate(File.Exists, name => Environment.GetEnvironmentVariable(name),
            OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("PATH"));

    internal static string? Locate(Func<string, bool> exists, Func<string, string?> env, bool windows, string? pathValue)
    {
        var candidates = windows ? WindowsCandidates(env) : UnixCandidates();
        foreach (var candidate in candidates)
            if (exists(candidate)) return candidate;

        if (pathValue is null) return null;
        var names = windows
            ? new[] { "msedge.exe", "chrome.exe", "chromium.exe" }
            : new[] { "google-chrome-stable", "google-chrome", "microsoft-edge", "chromium-browser", "chromium" };
        foreach (var dir in pathValue.Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (dir.Trim() is { Length: 0 }) continue;
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static IEnumerable<string> WindowsCandidates(Func<string, string?> env)
    {
        var programFiles = env("ProgramFiles");
        var programFilesX86 = env("ProgramFiles(x86)");
        var localAppData = env("LocalAppData");

        foreach (var root in new[] { programFiles, programFilesX86, localAppData })
        {
            if (root is not { Length: > 0 }) continue;
            yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
        }
        foreach (var root in new[] { programFiles, programFilesX86, localAppData })
        {
            if (root is not { Length: > 0 }) continue;
            yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
        }
    }

    private static IEnumerable<string> UnixCandidates()
    {
        foreach (var name in new[] { "google-chrome-stable", "google-chrome", "microsoft-edge", "chromium-browser", "chromium" })
            yield return $"/usr/bin/{name}";

        yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
    }
}
