using System.Text.Json.Nodes;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 智能体团体配置与运行控制面。配置按 workspace 落盘（<c>.haoyue/teams.json</c>），
/// 轨迹落在 <c>.haoyue/team-runs.json</c>；实现放在独立类，避免向
/// <see cref="DaemonAdminApi"/> 继续堆叠行数（ADR-20261015-giant-class-budget.md 决策 4）。
/// </summary>
internal static class TeamApi
{
    private static readonly Teams.TeamStore Store = new();
    private static readonly Teams.TeamRunStore Runs = new();

    public static string List(WorkspaceInfo workspace) =>
        new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["teams"] = TeamsArray(Store.List(workspace)),
        }.ToJsonString();

    public static string Save(WorkspaceInfo workspace, JsonObject parameters)
    {
        var teams = Store.Save(workspace, ParseTeam(parameters));
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["teams"] = TeamsArray(teams),
        }.ToJsonString();
    }

    public static string Delete(WorkspaceInfo workspace, JsonObject parameters)
    {
        var teams = Store.Delete(workspace, RequiredString(parameters, "id"));
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["teams"] = TeamsArray(teams),
        }.ToJsonString();
    }

    public static string Copy(WorkspaceInfo workspace, JsonObject parameters)
    {
        var (team, teams) = Store.Copy(
            workspace, RequiredString(parameters, "id"), OptionalString(parameters, "name"));
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["team"] = TeamNode(team),
            ["teams"] = TeamsArray(teams),
        }.ToJsonString();
    }

    /// <summary>受理即返回 running 快照；实际编排在后台执行，结果经事件与 team.runs 可查。</summary>
    public static string Run(WorkspaceInfo workspace, JsonObject parameters, HaoyueRuntime runtime)
    {
        var id = RequiredString(parameters, "id");
        var task = RequiredString(parameters, "task");
        var sessionId = OptionalString(parameters, "sessionId");
        var team = Store.List(workspace).FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal))
            ?? throw new DaemonRequestException($"No team with id \"{id}\" in this workspace.");
        if (!team.Enabled)
            throw new DaemonRequestException($"团体「{team.Name}」已停用，请先在设置中启用。");
        var runner = new Teams.TeamRunner(runtime.Delegator, runtime.Sessions, runtime.Events);
        var run = runner.Start(workspace, team, task, sessionId, parentScope: null);
        return RunNode(run).ToJsonString();
    }

    public static string Cancel(string runId)
    {
        var cancelled = Teams.TeamRunRegistry.Cancel(runId);
        return new JsonObject
        {
            ["runId"] = runId,
            ["cancelled"] = cancelled,
        }.ToJsonString();
    }

    public static string RunList(WorkspaceInfo workspace, JsonObject parameters)
    {
        var limit = Math.Clamp(Int(parameters, "limit") ?? 20, 1, Teams.TeamRunStore.MaxRecords);
        var teamId = OptionalString(parameters, "id");
        var runs = Runs.List(workspace, teamId, limit);
        // 惰性对账：进程内已无对应取消登记，说明运行上下文早已消失（例如上次崩溃）。
        if (runs.Any(run => run.Status == "running" && !Teams.TeamRunRegistry.IsRunning(run.RunId)))
        {
            Teams.TeamRunStore.Reconcile(workspace, Teams.TeamRunReconciler.InterruptedReason);
            runs = Runs.List(workspace, teamId, limit);
        }
        var array = new JsonArray();
        foreach (var run in runs) array.Add((JsonNode)RunNode(run));
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["runs"] = array,
        }.ToJsonString();
    }

    private static Teams.TeamDefinition ParseTeam(JsonObject parameters)
    {
        var team = new Teams.TeamDefinition
        {
            Id = OptionalString(parameters, "id") ?? "",
            Name = RequiredString(parameters, "name"),
            Description = OptionalString(parameters, "description") ?? "",
            Mode = OptionalString(parameters, "mode") ?? "sequential",
            CoordinatorId = OptionalString(parameters, "coordinatorId"),
            Enabled = Bool(parameters, "enabled") ?? true,
            MaxRounds = Int(parameters, "maxRounds") ?? 3,
            TimeoutSeconds = Int(parameters, "timeoutSeconds") ?? 300,
            MaxRetries = Int(parameters, "maxRetries") ?? 1,
            FailureStrategy = OptionalString(parameters, "failureStrategy") ?? "stop",
        };
        if (parameters["members"] is JsonArray members)
        {
            foreach (var node in members)
            {
                if (node is not JsonObject item) continue;
                team.Members.Add(new Teams.TeamMember
                {
                    Id = OptionalString(item, "id") ?? "",
                    Name = RequiredString(item, "name"),
                    Role = OptionalString(item, "role") ?? "",
                    Responsibility = OptionalString(item, "responsibility") ?? "",
                    Model = OptionalString(item, "model"),
                    ExpertId = OptionalString(item, "expertId"),
                    Enabled = Bool(item, "enabled") ?? true,
                    Tools = StringList(item, "tools"),
                });
            }
        }
        return team;
    }

    private static JsonArray TeamsArray(IEnumerable<Teams.TeamDefinition> teams)
    {
        var array = new JsonArray();
        foreach (var team in teams) array.Add((JsonNode)TeamNode(team));
        return array;
    }

    private static JsonObject TeamNode(Teams.TeamDefinition team)
    {
        var members = new JsonArray();
        foreach (var member in team.Members)
        {
            members.Add((JsonNode)new JsonObject
            {
                ["id"] = member.Id,
                ["name"] = member.Name,
                ["role"] = member.Role,
                ["responsibility"] = member.Responsibility,
                ["model"] = member.Model,
                ["expertId"] = member.ExpertId,
                ["enabled"] = member.Enabled,
                ["tools"] = StringArray(member.Tools),
            });
        }
        return new JsonObject
        {
            ["id"] = team.Id,
            ["name"] = team.Name,
            ["description"] = team.Description,
            ["mode"] = team.Mode,
            ["coordinatorId"] = team.CoordinatorId,
            ["enabled"] = team.Enabled,
            ["maxRounds"] = team.MaxRounds,
            ["timeoutSeconds"] = team.TimeoutSeconds,
            ["maxRetries"] = team.MaxRetries,
            ["failureStrategy"] = team.FailureStrategy,
            ["createdAt"] = team.CreatedAt,
            ["updatedAt"] = team.UpdatedAt,
            ["members"] = members,
        };
    }

    private static JsonObject RunNode(Teams.TeamRun run)
    {
        var members = new JsonArray();
        foreach (var member in run.Members)
        {
            members.Add((JsonNode)new JsonObject
            {
                ["memberId"] = member.MemberId,
                ["role"] = member.Role,
                ["status"] = member.Status,
                ["durationMs"] = member.DurationMs,
                ["toolCalls"] = member.ToolCalls,
                ["error"] = member.Error,
                ["outputPreview"] = member.OutputPreview,
            });
        }
        return new JsonObject
        {
            ["runId"] = run.RunId,
            ["teamId"] = run.TeamId,
            ["sessionId"] = run.SessionId,
            ["status"] = run.Status,
            ["startedAt"] = run.StartedAt,
            ["endedAt"] = run.EndedAt,
            ["rounds"] = run.Rounds,
            ["result"] = run.Result,
            ["error"] = run.Error,
            ["workspace"] = run.WorkspaceRoot,
            ["members"] = members,
        };
    }

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add((JsonNode)JsonValue.Create(value)!);
        return array;
    }

    private static List<string> StringList(JsonObject parameters, string name)
    {
        if (parameters[name] is not JsonArray array) return [];
        return array
            .Select(node => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "")
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string RequiredString(JsonObject parameters, string name) =>
        OptionalString(parameters, name)
        ?? throw new DaemonRequestException($"params.{name} is required");

    private static string? OptionalString(JsonObject parameters, string name) =>
        parameters[name] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool? Bool(JsonObject parameters, string name) =>
        parameters[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static int? Int(JsonObject parameters, string name) =>
        parameters[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
}
