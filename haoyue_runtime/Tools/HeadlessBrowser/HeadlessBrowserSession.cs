using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Haoyue.Runtime.Tools.HeadlessBrowser;

/// <summary>
/// 一个专用无头浏览器实例（--headless=new + 独立临时 user-data-dir），通过 CDP
/// 驱动：创建 about:blank 页 → attach（flatten）→ Page.navigate / Runtime.evaluate。
/// 进程终止用与 bash/terminal 同构的进程树治理（Job / setsid / 树遍历兜底）。
/// 生命周期由 BrowserTool 以单例管理：同一时刻至多一个内核、操作串行。
/// </summary>
internal sealed class HeadlessBrowserSession : IAsyncDisposable
{
    private static readonly Regex DevToolsEndpointRegex = new(
        @"DevTools listening on (?<endpoint>ws://\S+)", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private readonly Process _process;
    private readonly CdpClient _cdp;
    private readonly string _userDataDir;
    private readonly string _pageSessionId;

    private HeadlessBrowserSession(Process process, CdpClient cdp, string userDataDir, string pageSessionId)
    {
        _process = process;
        _cdp = cdp;
        _userDataDir = userDataDir;
        _pageSessionId = pageSessionId;
    }

    public string ExecutablePath => _process.StartInfo.FileName;

    /// <summary>False once the headless process has exited (crashed or was killed externally).</summary>
    public bool IsAlive
    {
        get
        {
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>Launches a headless instance and attaches to a page; throws with user-facing guidance on failure.</summary>
    public static async Task<HeadlessBrowserSession> StartAsync(TimeSpan launchTimeout, CancellationToken ct)
    {
        var executable = BrowserLocator.Locate();
        if (executable is null)
            throw new InvalidOperationException(
                "未找到可用的 Chromium 系浏览器。browser 工具驱动系统安装的 Edge 或 Chrome 以无头模式运行，" +
                "请安装 Microsoft Edge 或 Google Chrome 后重试（也可先用 web_fetch 抓取纯文本）。");

        var userDataDir = Path.Combine(Path.GetTempPath(), "haoyue-browser-" + Guid.NewGuid().ToString("N")[..12]);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true, // DevTools endpoint 打印在 stderr
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in new[]
                 {
                     "--headless=new",
                     "--remote-debugging-port=0",
                     $"--user-data-dir={userDataDir}",
                     "--no-first-run",
                     "--no-default-browser-check",
                     "--disable-extensions",
                     "--disable-gpu",
                     "--hide-scrollbars",
                     "--mute-audio",
                     "--disable-crash-reporter",
                     "--window-size=1280,900",
                     "about:blank",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"无法启动浏览器进程：{executable}");

        var killJob = ShellRunner.TryCreateKillJob(process);
        try
        {
            var endpoint = await ReadEndpointAsync(process, launchTimeout, ct).ConfigureAwait(false);
            if (endpoint is null)
                throw new TimeoutException($"浏览器在 {launchTimeout.TotalSeconds:0}s 内未报告 DevTools 端点（{executable}）。");

            var cdp = new CdpClient();
            try
            {
                await cdp.ConnectAsync(endpoint, ct).ConfigureAwait(false);

                var created = await cdp.CallAsync("Target.createTarget",
                    new JsonObject { ["url"] = "about:blank" }, sessionId: null, launchTimeout, ct).ConfigureAwait(false);
                var targetId = created?["targetId"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Target.createTarget 未返回 targetId。");

                var attached = await cdp.CallAsync("Target.attachToTarget",
                    new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, sessionId: null, launchTimeout, ct).ConfigureAwait(false);
                var pageSessionId = attached?["sessionId"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Target.attachToTarget 未返回 sessionId。");

                await cdp.CallAsync("Page.enable", null, pageSessionId, launchTimeout, ct).ConfigureAwait(false);
                return new HeadlessBrowserSession(process, cdp, userDataDir, pageSessionId);
            }
            catch
            {
                await cdp.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            ShellRunner.KillProcessTree(process, killJob);
            process.Dispose();
            TryDeleteUserDataDir(userDataDir);
            throw;
        }
    }

    /// <summary>
    /// Reads stderr until the DevTools endpoint appears. stdout/stderr are BOTH drained
    /// in the background for the process lifetime — with redirected pipes nobody reads,
    /// the OS pipe buffer (~4KB) fills up and the browser blocks on its next log write,
    /// freezing CDP responses (observed as Runtime.evaluate timeouts).
    /// </summary>
    private static async Task<string?> ReadEndpointAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        var endpoint = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    var match = DevToolsEndpointRegex.Match(line);
                    if (match.Success) endpoint.TrySetResult(match.Groups["endpoint"].Value);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }, CancellationToken.None);
        _ = Task.Run(async () =>
        {
            try { await process.StandardOutput.ReadToEndAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }, CancellationToken.None);

        var completed = await Task.WhenAny(endpoint.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return completed == endpoint.Task ? endpoint.Task.Result : null;
    }

    /// <summary>Navigates the page, waits for the load event (best effort) plus a settle delay, and returns url/title.</summary>
    public async Task<JsonObject> NavigateAsync(string url, TimeSpan settle, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var navigated = await _cdp.CallAsync("Page.navigate",
            new JsonObject { ["url"] = url }, _pageSessionId, timeout, timeoutCts.Token).ConfigureAwait(false);
        var errorText = navigated?["errorText"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(errorText))
            throw new InvalidOperationException($"页面导航失败：{errorText}（{url}）");

        // loadEventFired 尽力等待：单页应用或慢资源超时不致命——页面文本随后照常提取。
        try
        {
            await _cdp.WaitForEventAsync("Page.loadEventFired", _pageSessionId, timeout, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        if (settle > TimeSpan.Zero)
        {
            try { await Task.Delay(settle, timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }

        var location = await _cdp.CallAsync("Runtime.evaluate",
            EvalParams("location.href"), _pageSessionId, timeout, timeoutCts.Token).ConfigureAwait(false);
        var title = await _cdp.CallAsync("Runtime.evaluate",
            EvalParams("document.title"), _pageSessionId, timeout, timeoutCts.Token).ConfigureAwait(false);

        return new JsonObject
        {
            ["url"] = ResultValue(location) ?? url,
            ["title"] = ResultValue(title) ?? "",
        };
    }

    /// <summary>Evaluates a script and returns its JSON value; throws when the script throws.</summary>
    public async Task<JsonNode?> EvalAsync(string script, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var response = await _cdp.CallAsync("Runtime.evaluate",
            EvalParams(script), _pageSessionId, timeout, timeoutCts.Token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Runtime.evaluate 未返回结果。");

        if (response["exceptionDetails"] is { } exception)
        {
            var text = exception["exception"]?["description"]?.GetValue<string>()
                ?? exception["text"]?.GetValue<string>()
                ?? "script threw";
            throw new InvalidOperationException($"脚本执行出错：{text}");
        }
        return response["result"];
    }

    /// <summary>Extracts the primitive value produced by a Runtime.evaluate response, or null.</summary>
    internal static string? ResultValue(JsonNode? evaluateResponse) =>
        evaluateResponse?["result"]?["value"] is { } value
            ? value.ToJsonString(System.Text.Json.JsonSerializerOptions.Default).Trim('"')
            : null;

    private static JsonObject EvalParams(string expression) => new()
    {
        ["expression"] = expression,
        ["returnByValue"] = true,
        ["awaitPromise"] = true,
    };

    public async ValueTask DisposeAsync()
    {
        await _cdp.DisposeAsync().ConfigureAwait(false);
        var killJob = ShellRunner.TryCreateKillJob(_process);
        try
        {
            if (!_process.HasExited)
                ShellRunner.KillProcessTree(_process, killJob);
        }
        catch (InvalidOperationException) { } // already exited
        catch (System.ComponentModel.Win32Exception) { }
        finally
        {
            killJob?.Dispose();
        }
        _process.Dispose();
        TryDeleteUserDataDir(_userDataDir);
    }

    private static void TryDeleteUserDataDir(string userDataDir)
    {
        try
        {
            if (Directory.Exists(userDataDir)) Directory.Delete(userDataDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
