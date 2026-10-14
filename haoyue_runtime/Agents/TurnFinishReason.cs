namespace Haoyue.Runtime.Agents;

/// <summary>
/// 回合终态词汇表。此前所有回合都以 <c>done</c>/「任务完成」收尾，客户端无法区分
/// 「模型真的答完了」与「连接断了只留半截」「步数烧光」「连续两次空回答」——半截回答
/// 看起来和完整回答一模一样，用户只能自己猜。值本身是线协议 token（ASCII，跨端稳定），
/// 中文文案由各界面自行渲染。
/// </summary>
public static class TurnFinishReason
{
    public const string Stop = "stop";
    /// <summary>模型没有作答而是发起工具调用；只出现在历史消息上，不会是回合终态。</summary>
    public const string ToolCalls = "tool_calls";
    public const string Length = "length";
    public const string Interrupted = "interrupted";
    public const string Incomplete = "incomplete";
    public const string Refusal = "refusal";
    public const string Empty = "empty";
    public const string MaxSteps = "max_steps";
    public const string Budget = "budget";
    public const string Cancelled = "cancelled";
    public const string Error = "error";

    /// <summary>
    /// 把各家 provider 的 finish_reason 收敛到本词汇表。未知原值原样保留——客户端仍能把
    /// 「没答完」与「答完了」区分开，同时不丢掉上游给出的具体原因。
    /// </summary>
    public static string Normalize(string? providerReason) => providerReason switch
    {
        null or "" or "stop" or "end_turn" => Stop,
        "tool_calls" or "function_call" => ToolCalls,
        "length" or "max_tokens" => Length,
        "refusal" or "content_filter" or "safety" => Refusal,
        "interrupted" => Interrupted,
        "incomplete" => Incomplete,
        "cancelled" => Cancelled,
        var other => other,
    };

    /// <summary>True unless the model actually finished its answer (tool-call steps don't count).</summary>
    public static bool IsIncomplete(string? reason) => reason is not null and not Stop and not ToolCalls;

    /// <summary>面向用户的中文终态文案（CLI 与日志使用；桌面自行渲染同义文案）。</summary>
    public static string? ChineseLabel(string? reason) => reason switch
    {
        Length => "输出达到长度上限，回答可能被截断",
        Interrupted => "模型连接中断，保留了中断前的部分输出",
        Incomplete => "未取得模型终态，回答可能不完整",
        Refusal => "模型拒绝回答该请求",
        Empty => "模型连续两次返回空回答",
        MaxSteps => "已达到本回合步数上限，任务可能未完成",
        Budget => "已达到本回合预算上限，任务可能未完成",
        Cancelled => "回合被中断",
        Error => "回合因错误终止",
        _ => null,
    };
}
