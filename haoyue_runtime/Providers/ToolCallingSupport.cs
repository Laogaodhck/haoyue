namespace Haoyue.Runtime.Providers;

/// <summary>
/// 工具调用能力探测的三态结果。
/// </summary>
public enum ToolCallingSupport
{
    /// <summary>模型/提供方确实接受 tools 参数（探测到 tool call 或正常完成）。</summary>
    Supported,

    /// <summary>明确不支持：本地客户端（忽略 request.Tools）或提供方拒绝 tools 参数。</summary>
    Unsupported,

    /// <summary>探测失败（网络/超时/未知错误）——按未知处理，保持注入工具的旧行为，不误伤。</summary>
    Unknown,
}
