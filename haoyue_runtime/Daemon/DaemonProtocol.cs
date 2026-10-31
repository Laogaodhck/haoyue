using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// protocol.info 的唯一投影。方法清单来自 <see cref="DaemonContract.MethodNames"/>（Contract First），
/// 本机管道 / socket 与局域网连接共用同一份应答——两条接入面不得报出不同的能力集。
/// </summary>
internal static class DaemonProtocol
{
    internal static string Info(string? versionWarning = null)
    {
        var info = new JsonObject
        {
            ["version"] = DaemonServer.ProtocolVersion,
            ["transport"] = "jsonl",
            ["capabilities"] = new JsonArray(
                "chat", "image-input", "concurrent-turns", "reasoning-level", "agent.steer", "agent.cancel", "agent.mode", "workspace", "provider",
                "model", "mcp", "skill", "usage", "project", "session", "global-session", "doctor", "file-locks", "routing", "schedule", "factory-reset", "prompt-optimize", "config-status", "config-rebuild", "contract", "lan-access"),
            ["methods"] = new JsonArray(DaemonContract.MethodNames.Select(name => (JsonNode)name).ToArray()),
        };
        if (versionWarning is not null) info["versionWarning"] = versionWarning;
        return info.ToJsonString();
    }
}
