namespace Haoyue.Runtime.Providers;

/// <summary>
/// 提供商的接口协议标识。国内云厂商都提供 OpenAI 兼容端点，单独保留 kind 是为了让
/// Desktop 能按厂商预填地址、提示模型示例并展示厂商标签；线上请求仍统一由
/// <see cref="OpenAiCompatibleClient"/> 实现，不额外维护一套序列化路径。
/// </summary>
public static class ProviderKinds
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string Aliyun = "aliyun";
    public const string Volcengine = "volcengine";
    public const string Tencent = "tencent";
    public const string Baidu = "baidu";
    public const string Zhipu = "zhipu";
    public const string DeepSeek = "deepseek";
    public const string Local = "local";

    public static readonly string[] All =
        [OpenAi, Anthropic, Aliyun, Volcengine, Tencent, Baidu, Zhipu, DeepSeek, Local];

    public static bool IsKnown(string? kind) =>
        kind is not null && All.Contains(kind, StringComparer.OrdinalIgnoreCase);

    public static bool IsAnthropic(string? kind) =>
        string.Equals(kind, Anthropic, StringComparison.OrdinalIgnoreCase);

    public static bool IsLocal(string? kind) =>
        string.Equals(kind, Local, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 映射到实际客户端实现：Anthropic 与本地各有专用客户端，其余（OpenAI 及各家云
    /// 厂商的兼容端点）共用 OpenAI 兼容客户端。未知 kind 原样返回，让客户端工厂报出
    /// 「没有注册对应客户端」的明确错误，而不是静默降级成 OpenAI。
    /// </summary>
    public static string ToClientKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        null or "" or OpenAi or Aliyun or Volcengine or Tencent or Baidu or Zhipu or DeepSeek => OpenAi,
        Anthropic => Anthropic,
        Local => Local,
        _ => kind!,
    };
}
