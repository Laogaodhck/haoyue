namespace Haoyue.Runtime.Providers;

/// <summary>
/// 版本契约（Version Contract）登记表：所有对外依赖的 API 版本在此集中声明。
/// 上游 API 变更/退役时，通过 <see cref="ClassifyBreakingChange"/> 发出结构化的
/// Breaking Change 告警（可读文案），而不是让调用链裸崩溃。
/// </summary>
public static class ApiVersionContract
{
    /// <summary>Anthropic Messages API 版本（anthropic-version header）。</summary>
    public const string AnthropicApiVersion = "2023-06-01";

    /// <summary>Daemon JSONL 协议版本（与 DaemonServer.ProtocolVersion 同源）。</summary>
    public const string DaemonProtocolVersion = "2.2";

    /// <summary>契约快照格式版本（contracts/daemon-contract.json 的 contractVersion）。</summary>
    public const string ContractFormatVersion = "1.0";

    /// <summary>
    /// 判定上游 API 失败是否属于「版本变更/退役」类 Breaking Change。
    /// 返回告警文案；null 表示普通失败，按常规错误处理。
    /// 判定规则：410 Gone 一律告警；404 且错误详情指向模型/端点退役关键词时告警。
    /// </summary>
    public static string? ClassifyBreakingChange(int status, string detail)
    {
        if (status == 410)
        {
            return "⚠️ [Breaking Change] 上游 API 返回 410（已退役）：该模型或端点可能已停止服务。" +
                   "请更新 provider 的 BaseUrl/模型名，或升级 haoyue 以适配新的 API 版本。";
        }
        if (status == 404 && IsRetirementDetail(detail))
        {
            return "⚠️ [Breaking Change] 上游 API 返回 404 且错误指向模型/端点退役：API 版本可能已变更。" +
                   "请核对 provider 配置中的模型名与 BaseUrl，或升级 haoyue。";
        }
        return null;
    }

    private static bool IsRetirementDetail(string detail)
    {
        if (string.IsNullOrEmpty(detail)) return false;
        var lower = detail.ToLowerInvariant();

        return (lower.Contains("model") && (
                    lower.Contains("decommission") || lower.Contains("retired")
                    || lower.Contains("deprecated") || lower.Contains("no longer")
                    || lower.Contains("shut down") || lower.Contains("shutted down")))
            || lower.Contains("not_found_error") && lower.Contains("endpoint")
            || lower.Contains("invalid api version")
            || lower.Contains("api version");
    }
}
