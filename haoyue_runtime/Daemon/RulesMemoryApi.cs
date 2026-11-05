using System.Text.Json.Nodes;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 规则 / 记忆用户控制面的实现。按 ADR-20261015-giant-class-budget.md 决策 4
/// （新 RPC 实现不再堆进 DaemonAdminApi），这里落地事实库读写与结构化规则的
/// JSON 读面；<see cref="DaemonAdminApi"/> 只保留按 workspace 解析后的薄入口。
/// 所有数据都以 workspace 根为键，跨项目不可见。
/// </summary>
internal static class RulesMemoryApi
{
    private static readonly FactsStore Facts = new();

    /// <summary>事实列表与最近仲裁；includeRetired=false 时隐藏被取代/已过期的条目。</summary>
    public static string ListFacts(WorkspaceInfo workspace, JsonObject parameters)
    {
        var includeRetired = parameters["includeRetired"]?.GetValue<bool?>() ?? true;
        var all = Facts.ListAllForRoot(workspace.Root);
        var now = DateTimeOffset.UtcNow;
        var visible = includeRetired
            ? all
            : all.Where(fact => fact.SupersededBy is null && (fact.ExpiresAt is null || fact.ExpiresAt > now)).ToList();
        var payload = new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["capacity"] = FactsStore.CapacityPerWorkspace,
            ["activeCount"] = all.Count(fact => fact.SupersededBy is null && (fact.ExpiresAt is null || fact.ExpiresAt > now)),
            ["facts"] = FactsArray(visible),
            ["conflicts"] = FactConflicts(all),
        };
        return payload.ToJsonString();
    }

    public static string AddFact(WorkspaceInfo workspace, JsonObject parameters)
    {
        var content = RequiredString(parameters, "content");
        var topic = OptionalString(parameters, "topic");
        var confidence = parameters["confidence"]?.GetValue<double?>() ?? 0.9;
        var ttlDays = parameters["ttlDays"]?.GetValue<int?>();
        var supersedes = OptionalString(parameters, "supersedes");
        var sessionId = OptionalString(parameters, "sessionId");
        try
        {
            var result = Facts.AddDetailed(
                workspace.Root, content, topic, "user", confidence, ttlDays,
                supersedes, sessionId, "manual");
            return FactWritePayload(workspace, result.Entry, result.Superseded);
        }
        catch (ArgumentException ex)
        {
            throw new DaemonRequestException(ex.Message);
        }
    }

    public static string UpdateFact(WorkspaceInfo workspace, JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        DateTimeOffset? expiresAt = null;
        if (parameters["expiresAt"] is JsonNode node)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var raw))
            {
                if (string.IsNullOrWhiteSpace(raw)) expiresAt = null;
                else if (DateTimeOffset.TryParse(raw, out var parsed)) expiresAt = parsed;
                else throw new DaemonRequestException("params.expiresAt must be an ISO-8601 timestamp");
            }
            else
            {
                throw new DaemonRequestException("params.expiresAt must be a string");
            }
        }
        var result = Facts.UpdateForRoot(
            workspace.Root, id,
            OptionalString(parameters, "content"),
            OptionalString(parameters, "topic"),
            parameters["confidence"]?.GetValue<double?>(),
            parameters["pinned"]?.GetValue<bool?>(),
            expiresAt,
            OptionalString(parameters, "kind"),
            parameters["confirmed"]?.GetValue<bool?>());
        if (result is null)
            throw new DaemonRequestException($"No fact with id \"{id}\" in this workspace.");
        return FactWritePayload(workspace, result.Entry, result.Superseded);
    }

    public static string ConfirmFact(WorkspaceInfo workspace, JsonObject parameters)
    {
        parameters["confirmed"] = parameters["confirmed"]?.GetValue<bool?>() ?? true;
        return UpdateFact(workspace, parameters);
    }

    public static string DeleteFact(WorkspaceInfo workspace, JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        if (!Facts.ForgetForRoot(workspace.Root, id))
            throw new DaemonRequestException($"No fact with id \"{id}\" in this workspace.");
        return FactsPayload(workspace, null, []);
    }

    public static string PinFact(WorkspaceInfo workspace, JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        var pinned = parameters["pinned"]?.GetValue<bool?>() ?? true;
        var entry = Facts.PinForRoot(workspace.Root, id, pinned)
            ?? throw new DaemonRequestException($"No fact with id \"{id}\" in this workspace.");
        return FactWritePayload(workspace, entry, []);
    }

    public static string RestoreFact(WorkspaceInfo workspace, JsonObject parameters)
    {
        var id = RequiredString(parameters, "id");
        var entry = Facts.RestoreForRoot(workspace.Root, id)
            ?? throw new DaemonRequestException(
                $"Fact \"{id}\" is not superseded, or does not exist in this workspace.");
        return FactWritePayload(workspace, entry, []);
    }

    /// <summary>显式解决一条同名冲突，并返回最新规则视图。</summary>
    public static string ResolveConflict(WorkspaceInfo workspace, JsonObject parameters)
    {
        var title = RequiredString(parameters, "title");
        var mode = RequiredString(parameters, "mode");
        var actor = OptionalString(parameters, "actor") ?? Environment.UserName;
        try
        {
            var resolved = ManagedRulesStore.Resolve(workspace, title, mode, actor);
            var payload = new JsonObject
            {
                ["workspace"] = workspace.Root,
                ["resolved"] = new JsonObject
                {
                    ["title"] = resolved.Title,
                    ["winner"] = resolved.Winner,
                    ["globalId"] = resolved.GlobalId,
                    ["workspaceId"] = resolved.WorkspaceId,
                    ["mode"] = mode,
                    ["actor"] = actor,
                },
            };
            AppendManagedRules(payload, workspace);
            return payload.ToJsonString();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new DaemonRequestException(ex.Message);
        }
    }

    /// <summary>把结构化规则、生效顺序、冲突与命中记录挂到 rules 返回体上。</summary>
    public static void AppendManagedRules(JsonObject payload, WorkspaceInfo workspace)
    {
        var managed = new JsonArray();
        foreach (var rule in ManagedRulesStore.List(workspace))
            managed.Add((JsonNode)ManagedRuleNode(rule));
        var effective = new JsonArray();
        foreach (var rule in ManagedRulesStore.Effective(workspace))
        {
            effective.Add((JsonNode)new JsonObject
            {
                ["id"] = rule.Id,
                ["title"] = rule.Title,
                ["scope"] = rule.Scope,
                ["priority"] = rule.Priority,
            });
        }
        var conflicts = new JsonArray();
        foreach (var conflict in ManagedRulesStore.Conflicts(workspace))
        {
            conflicts.Add((JsonNode)new JsonObject
            {
                ["title"] = conflict.Title,
                ["globalId"] = conflict.GlobalId,
                ["workspaceId"] = conflict.WorkspaceId,
                ["winner"] = conflict.Winner,
            });
        }
        var hits = new JsonArray();
        var resolutions = new JsonArray();
        foreach (var hit in ManagedRulesStore.ListHits(workspace))
        {
            hits.Add((JsonNode)new JsonObject
            {
                ["ruleId"] = hit.RuleId,
                ["title"] = hit.Title,
                ["scope"] = hit.Scope,
                ["sessionId"] = hit.SessionId,
                ["at"] = hit.At,
                ["action"] = hit.Action,
                ["actor"] = hit.Actor,
            });
            if (hit.Action is null) continue;
            resolutions.Add((JsonNode)new JsonObject
            {
                ["ruleId"] = hit.RuleId,
                ["title"] = hit.Title,
                ["scope"] = hit.Scope,
                ["action"] = hit.Action,
                ["actor"] = hit.Actor,
                ["at"] = hit.At,
            });
        }
        payload["managed"] = managed;
        payload["effective"] = effective;
        payload["conflicts"] = conflicts;
        payload["hits"] = hits;
        payload["resolutions"] = resolutions;
    }

    private static JsonObject ManagedRuleNode(ManagedRuleEntry rule) => new()
    {
        ["id"] = rule.Id,
        ["title"] = rule.Title,
        ["content"] = rule.Content,
        ["enabled"] = rule.Enabled,
        ["priority"] = rule.Priority,
        ["scope"] = rule.Scope,
        ["createdAt"] = rule.CreatedAt,
        ["updatedAt"] = rule.UpdatedAt,
    };

    private static string FactWritePayload(
        WorkspaceInfo workspace, FactEntry entry, IReadOnlyList<FactEntry> superseded) =>
        FactsPayload(workspace, entry, superseded);

    private static string FactsPayload(
        WorkspaceInfo workspace, FactEntry? entry, IReadOnlyList<FactEntry> superseded)
    {
        var all = Facts.ListAllForRoot(workspace.Root);
        var now = DateTimeOffset.UtcNow;
        var payload = new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["capacity"] = FactsStore.CapacityPerWorkspace,
            ["activeCount"] = all.Count(fact => fact.SupersededBy is null && (fact.ExpiresAt is null || fact.ExpiresAt > now)),
            ["facts"] = FactsArray(all),
            ["conflicts"] = FactConflicts(all),
        };
        if (entry is not null) payload["entry"] = FactNode(entry);
        payload["superseded"] = FactsArray(superseded);
        return payload.ToJsonString();
    }

    private static JsonArray FactsArray(IEnumerable<FactEntry> facts)
    {
        var array = new JsonArray();
        foreach (var fact in facts) array.Add((JsonNode)FactNode(fact));
        return array;
    }

    private static JsonObject FactNode(FactEntry fact) => new()
    {
        ["id"] = fact.Id,
        ["content"] = fact.Content,
        ["topic"] = fact.Topic,
        ["source"] = fact.Source,
        ["confidence"] = fact.Confidence,
        ["createdAt"] = fact.CreatedAt,
        ["expiresAt"] = fact.ExpiresAt,
        ["supersededBy"] = fact.SupersededBy,
        ["supersededAt"] = fact.SupersededAt,
        ["lastUsedAt"] = fact.LastUsedAt,
        ["useCount"] = fact.UseCount,
        ["denialCount"] = fact.DenialCount,
        ["pinned"] = fact.Pinned,
        ["sourceSessionId"] = fact.SourceSessionId,
        ["kind"] = fact.Kind,
        ["confirmed"] = fact.Confirmed,
    };

    /// <summary>最近一次可见仲裁：某条活跃事实取代了哪些同主题旧事实。</summary>
    private static JsonArray FactConflicts(IReadOnlyList<FactEntry> facts)
    {
        var byId = facts.ToDictionary(fact => fact.Id, StringComparer.Ordinal);
        var array = new JsonArray();
        foreach (var group in facts
                     .Where(fact => fact.SupersededBy is not null && byId.ContainsKey(fact.SupersededBy))
                     .GroupBy(fact => fact.SupersededBy!, StringComparer.Ordinal))
        {
            var kept = byId[group.Key];
            if (kept.SupersededBy is not null) continue;
            var replacedIds = new JsonArray();
            var replacedContents = new JsonArray();
            foreach (var fact in group)
            {
                replacedIds.Add((JsonNode)JsonValue.Create(fact.Id)!);
                replacedContents.Add((JsonNode)JsonValue.Create(fact.Content)!);
            }
            array.Add((JsonNode)new JsonObject
            {
                ["topic"] = kept.Topic,
                ["keptId"] = kept.Id,
                ["keptContent"] = kept.Content,
                ["replacedIds"] = replacedIds,
                ["replacedContents"] = replacedContents,
                ["at"] = group.Max(fact => fact.SupersededAt),
            });
        }
        return array;
    }

    private static string RequiredString(JsonObject parameters, string name) =>
        OptionalString(parameters, name)
        ?? throw new DaemonRequestException($"params.{name} is required");

    private static string? OptionalString(JsonObject parameters, string name)
    {
        var value = parameters[name]?.GetValue<string>()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
