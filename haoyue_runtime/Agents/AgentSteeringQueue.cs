using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Agents;

/// <summary>
/// Messages sent with "steer" while an agent turn is running. The agent drains this
/// queue between model/tool steps so guidance never cancels an in-flight request.
/// </summary>
public sealed class AgentSteeringQueue
{
    private readonly Lock _gate = new();
    private readonly Queue<ChatMessage> _messages = new();
    private bool _accepting = true;
    private bool _abortRequested;

    public bool TryEnqueue(ChatMessage message)
    {
        lock (_gate)
        {
            if (!_accepting) return false;
            _messages.Enqueue(message);
            return true;
        }
    }

    /// <summary>
    /// M-B: steering abort（显式 opt-in）。默认语义下引导消息在下一步边界才生效；
    /// abort 是硬动作——agent 停止执行在途工具批次（pending 的 mutating 调用也不再
    /// 执行）并以"被用户中止"收尾。回合已结束时返回 false（无事可中止）。
    /// </summary>
    public bool RequestAbort()
    {
        lock (_gate)
        {
            _abortRequested = true;
            return _accepting;
        }
    }

    /// <summary>True when an abort was requested and the turn has not been closed yet.</summary>
    public bool IsAbortRequested
    {
        get { lock (_gate) return _abortRequested && _accepting; }
    }

    public IReadOnlyList<ChatMessage> Drain()
    {
        lock (_gate)
        {
            var messages = new List<ChatMessage>(_messages.Count);
            while (_messages.Count > 0)
            {
                // Single choke point for steer text from every entry path (daemon RPC,
                // CLI REPL): tag it as a runtime steering notice — so the model can tell
                // guidance from ordinary user turns, uniformly across both paths — and
                // neutralize a spoofed ">>> [" runtime-notice prefix inside the payload.
                var message = _messages.Dequeue();
                message.Text = $">>> [steering] {Agent.SanitizeRuntimeNoticePrefix(message.Text)}";
                messages.Add(message);
            }
            return messages;
        }
    }

    /// <summary>
    /// Atomically closes an empty queue. When a message raced with completion the queue
    /// stays open so the Agent can run one more step and drain it.
    /// </summary>
    public bool TryCompleteIfEmpty()
    {
        lock (_gate)
        {
            if (_messages.Count > 0) return false;
            _accepting = false;
            return true;
        }
    }

    public void Complete()
    {
        lock (_gate) _accepting = false;
    }
}
