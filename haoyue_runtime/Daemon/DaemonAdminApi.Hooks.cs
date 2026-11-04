using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 钩子面（hooks surface）：事件钩子（task.completed / schedule.completed →
/// Webhook / 本地命令）的增删改查与试运行。
///
/// 独立 partial 文件以维持 DaemonAdminApi 的类级行数预算
/// （ADR-20261015-giant-class-budget.md）。「查看钩子配置和问题」= 配置校验
/// （HookRunner.Validate）+ 进程内最近执行状态（HookRunner.Snapshot）。
/// </summary>
internal sealed partial class DaemonAdminApi
{
    private string HooksListJson()
    {
        var configured = runtime.ConfigStore.Config.Hooks.Hooks;
        var hooks = new JsonArray();
        var problems = new JsonArray();
        foreach (var hook in configured)
        {
            var node = new JsonObject
            {
                ["name"] = hook.Name,
                ["trigger"] = hook.Trigger,
                ["action"] = hook.Action,
                ["enabled"] = hook.Enabled,
                ["webhookUrl"] = hook.WebhookUrl ?? "",
                // 密钥不回传明文：只给「是否已配置」标记，避免把凭据泄进设置页与日志。
                ["hasSecret"] = !string.IsNullOrWhiteSpace(hook.WebhookSecret),
                ["command"] = hook.Command ?? "",
            };
            hooks.Add((JsonNode)node);

            var problem = HookRunner.Validate(hook);
            if (problem is not null)
                problems.Add((JsonNode)new JsonObject { ["name"] = hook.Name, ["problem"] = problem });
        }

        var status = new JsonArray();
        foreach (var run in HookRunner.Snapshot())
            status.Add((JsonNode)new JsonObject
            {
                ["name"] = run.Name,
                ["runCount"] = run.RunCount,
                ["lastOk"] = run.LastOk,
                ["lastTriggeredAt"] = run.LastTriggeredAt?.ToString("O"),
                ["lastError"] = run.LastError,
                ["lastDurationMs"] = run.LastDurationMs,
            });

        return new JsonObject
        {
            ["hooks"] = hooks,
            ["problems"] = problems,
            ["status"] = status,
        }.ToJsonString();
    }

    /// <summary>hooks.list：已配置钩子 + 配置问题 + 最近执行状态。</summary>
    public string ListHooks() => HooksListJson();

    /// <summary>hooks.save：按名称新增或更新一条钩子；密钥语义见下。返回完整读面。</summary>
    public string SaveHook(JsonObject parameters)
    {
        var name = RequiredString(parameters, "name");
        if (string.IsNullOrWhiteSpace(name))
            throw new DaemonRequestException("params.name 不能为空");

        var configured = runtime.ConfigStore.Config.Hooks.Hooks;
        var existing = configured.FirstOrDefault(
            h => string.Equals(h.Name, name, StringComparison.Ordinal));

        // 名称冲突保护：更新已有钩子只按精确名称定位；同名新增走更新路径。
        var hook = existing ?? new HookConfig { Name = name.Trim() };
        if (existing is null) configured.Add(hook);

        if (parameters["trigger"] is JsonValue triggerValue && triggerValue.TryGetValue<string>(out var trigger))
            hook.Trigger = trigger;
        if (parameters["action"] is JsonValue actionValue && actionValue.TryGetValue<string>(out var action))
            hook.Action = action;
        if (parameters["webhookUrl"] is JsonValue urlValue && urlValue.TryGetValue<string>(out var url))
            hook.WebhookUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        if (parameters["command"] is JsonValue commandValue && commandValue.TryGetValue<string>(out var command))
            hook.Command = string.IsNullOrWhiteSpace(command) ? null : command;

        // Webhook 密钥：缺省 = 保持已存值；非空 = 覆盖；显式空串 = 清除。
        if (parameters["webhookSecret"] is JsonValue secretValue && secretValue.TryGetValue<string>(out var secret))
            hook.WebhookSecret = string.IsNullOrEmpty(secret) ? null : secret;

        if (parameters["enabled"] is JsonValue enabledValue && enabledValue.TryGetValue<bool>(out var enabled))
            hook.Enabled = enabled;

        var problem = HookRunner.Validate(hook);
        if (problem is not null)
        {
            if (existing is null) configured.Remove(hook); // 校验失败不入库
            throw new DaemonRequestException(problem);
        }

        runtime.ConfigStore.Save();
        return HooksListJson();
    }

    /// <summary>hooks.delete：按名称删除；不存在的名称报 NotFound。</summary>
    public string DeleteHook(JsonObject parameters)
    {
        var name = RequiredString(parameters, "name");
        var configured = runtime.ConfigStore.Config.Hooks.Hooks;
        var existing = configured.FirstOrDefault(
            h => string.Equals(h.Name, name, StringComparison.Ordinal));
        if (existing is null)
            throw new DaemonRequestException($"钩子「{name}」不存在", DaemonErrorCode.NotFound);

        configured.Remove(existing);
        runtime.ConfigStore.Save();
        return HooksListJson();
    }

    /// <summary>hooks.test：立即触发一次指定钩子并返回执行结果（不要求 enabled）。</summary>
    public async Task<string> TestHookAsync(JsonObject parameters, CancellationToken ct)
    {
        var name = RequiredString(parameters, "name");
        var outcome = await HookRunner.TestAsync(name, runtime.ConfigStore, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["ok"] = outcome.Ok,
            ["error"] = outcome.Error,
            ["durationMs"] = outcome.DurationMs,
            ["output"] = outcome.Output,
        }.ToJsonString();
    }
}
