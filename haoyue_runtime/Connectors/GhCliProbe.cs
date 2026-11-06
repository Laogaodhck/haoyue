using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Haoyue.Runtime.Connectors;

/// <summary>一次 gh 子进程调用的最小结果；可注入 runner 便于测试，不依赖本机是否安装 gh。</summary>
public sealed record GhCommandResult(int ExitCode, string Output, string Error, bool TimedOut = false, bool Missing = false);

/// <summary>GitHub CLI 的可用性快照（供连接器面板展示；绝不含 token）。</summary>
public sealed record GhCliStatus(
    bool Installed,
    string? Version,
    bool Authenticated,
    string? Account,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> Scopes,
    bool Available,
    string? Error,
    string InstallUrl,
    string LoginCommand);

/// <summary>
/// 探测本机 gh：安装与否、版本、登录账号与 scopes。
/// 只读 gh 自己的登录态，不读取令牌；所有输出先脱敏再返回。
/// 探测失败永不抛出，统一折叠成可读的 <see cref="GhCliStatus.Error"/>。
/// </summary>
public static class GhCliProbe
{
    public const string InstallUrl = "https://cli.github.com/";
    public const string LoginCommand = "gh auth login";

    private const int TimeoutMilliseconds = 5000;

    private static readonly Regex s_version = new(
        @"gh version ([0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.Compiled);

    private static readonly Regex s_account = new(
        @"Logged in to \S+ (?:account|as) ([A-Za-z0-9-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_host = new(
        @"Logged in to (\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_scopes = new(
        @"Token scopes:\s*(.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_scopeItem = new(@"'([^']+)'", RegexOptions.Compiled);

    private static readonly Regex s_token = new(
        @"gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled);

    private static readonly Regex s_authFailure = new(
        @"not logged in|gh auth login|authentication|http 401|requires authentication",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>探测 gh；<paramref name="runner"/> 为 null 时执行真实子进程。</summary>
    public static GhCliStatus Describe(Func<IReadOnlyList<string>, GhCommandResult>? runner = null)
    {
        runner ??= RunGh;
        try
        {
            var version = runner(["--version"]);
            if (version.Missing) return MissingStatus();
            if (version.TimedOut)
                return Unavailable("gh --version 超过 5 秒未返回，已终止。");
            if (version.ExitCode != 0)
                return Unavailable($"gh --version 失败：{FirstLine(version)}");
            var parsedVersion = ParseVersion(version.Output);

            var auth = runner(["auth", "status"]);
            if (auth.Missing) return MissingStatus();
            if (auth.TimedOut)
                return new GhCliStatus(true, parsedVersion, false, null, [], [], false,
                    "gh auth status 超过 5 秒未返回，已终止。", InstallUrl, LoginCommand);

            var text = Redact(((auth.Output ?? "") + "\n" + (auth.Error ?? "")).Trim());
            var account = ParseAccount(text);
            var hosts = ParseHosts(text);
            var scopes = ParseScopes(text);

            if (auth.ExitCode != 0)
            {
                var reason = s_authFailure.IsMatch(text)
                    ? "GitHub CLI 未登录或凭据失效：请运行 gh auth login 后重试。"
                    : $"gh auth status 返回退出码 {auth.ExitCode}：{FirstLine(auth)}";
                return new GhCliStatus(true, parsedVersion, false, account, hosts, scopes, false,
                    reason, InstallUrl, LoginCommand);
            }

            if (account is null)
            {
                // 退出码为 0 但没有解析到账号：保守按未登录处理（fail-closed）。
                return new GhCliStatus(true, parsedVersion, false, null, hosts, scopes, false,
                    "gh auth status 未返回登录账号：请运行 gh auth login 后重试。", InstallUrl, LoginCommand);
            }

            return new GhCliStatus(true, parsedVersion, true, account, hosts, scopes, true,
                null, InstallUrl, LoginCommand);
        }
        catch (Exception ex)
        {
            return new GhCliStatus(false, null, false, null, [], [], false,
                "检测 GitHub CLI 失败：" + Redact(ex.Message), InstallUrl, LoginCommand);
        }
    }

    /// <summary>把 gh 输出里的令牌形态替换为 <c>gh***</c>（防御性，正常路径不会出现 token）。</summary>
    public static string Redact(string? text) => s_token.Replace(text ?? string.Empty, "gh***");

    /// <summary>控制台返回体：只含账号/主机/scope，不含令牌。</summary>
    public static JsonObject ToJson(GhCliStatus status) => new()
    {
        ["installed"] = status.Installed,
        ["version"] = status.Version,
        ["authenticated"] = status.Authenticated,
        ["account"] = status.Account,
        ["hosts"] = new JsonArray(status.Hosts.Select(host => (JsonNode)host).ToArray()),
        ["scopes"] = new JsonArray(status.Scopes.Select(scope => (JsonNode)scope).ToArray()),
        ["available"] = status.Available,
        ["error"] = status.Error,
        ["installUrl"] = status.InstallUrl,
        ["loginCommand"] = status.LoginCommand,
    };

    private static GhCliStatus MissingStatus() => new(
        false, null, false, null, [], [], false,
        "未检测到 GitHub CLI（gh）。安装：https://cli.github.com/ ，安装后运行 gh auth login。",
        InstallUrl, LoginCommand);

    private static GhCliStatus Unavailable(string error) => new(
        true, null, false, null, [], [], false, error, InstallUrl, LoginCommand);

    private static string? ParseVersion(string? output)
    {
        var match = s_version.Match(output ?? string.Empty);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ParseAccount(string text)
    {
        var match = s_account.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static IReadOnlyList<string> ParseHosts(string text) =>
        Distinct(s_host.Matches(text).Select(match => match.Groups[1].Value));

    private static IReadOnlyList<string> ParseScopes(string text)
    {
        var match = s_scopes.Match(text);
        if (!match.Success) return [];
        return Distinct(s_scopeItem.Matches(match.Groups[1].Value).Select(item => item.Groups[1].Value));
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> items)
    {
        var list = new List<string>();
        foreach (var item in items)
        {
            if (!list.Contains(item, StringComparer.OrdinalIgnoreCase)) list.Add(item);
        }
        return list;
    }

    private static string FirstLine(GhCommandResult result)
    {
        var text = string.IsNullOrEmpty(result.Error) ? result.Output : result.Error;
        return Redact((text ?? string.Empty).Trim().Split('\n')[0]);
    }

    private static GhCommandResult RunGh(IReadOnlyList<string> args)
    {
        var binary = Environment.GetEnvironmentVariable("GH_BIN");
        if (string.IsNullOrWhiteSpace(binary)) binary = "gh";
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = binary,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);
            startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
            startInfo.Environment["GH_PAGER"] = "cat";
            startInfo.Environment["PAGER"] = "cat";
            startInfo.Environment["NO_COLOR"] = "1";
            startInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new GhCommandResult(-1, string.Empty, "无法启动 gh", Missing: true);

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return new GhCommandResult(-1, string.Empty, "timeout", TimedOut: true);
            }
            Task.WaitAll(stdout, stderr);
            return new GhCommandResult(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Win32Exception)
        {
            return new GhCommandResult(-1, string.Empty, "not found", Missing: true);
        }
        catch (FileNotFoundException)
        {
            return new GhCommandResult(-1, string.Empty, "not found", Missing: true);
        }
    }
}
