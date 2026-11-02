using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// Sub-agent definitions: built-in presets plus Markdown files under
/// <c>~/.haoyue/agents</c> (user) and <c>&lt;workspace&gt;/.haoyue/agents</c> (project).
/// The runtime re-reads the files on every delegation, so saving here is hot.
/// </summary>
internal sealed partial class DaemonAdminApi
{
    public string ListSubAgents(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var agents = new JsonArray();
        foreach (var entry in _subAgents.Discover(ProjectRoot(workspace)))
        {
            agents.Add((JsonNode)new JsonObject
            {
                ["id"] = entry.Role.Id,
                ["name"] = entry.Role.Name,
                ["description"] = entry.Role.Description,
                ["prompt"] = entry.Role.Prompt,
                ["model"] = entry.Role.Model,
                ["tools"] = entry.Role.AllowedTools is { Count: > 0 } tools ? Strings(tools) : new JsonArray(),
                ["scope"] = ScopeName(entry.Scope),
                ["path"] = entry.Path is null ? null : JsonValue.Create(entry.Path),
                ["readOnly"] = entry.Scope == SubAgentScope.Builtin,
            });
        }
        return new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["isGlobal"] = workspace.IsGlobal,
            ["agents"] = agents,
        }.ToJsonString();
    }

    public string SaveSubAgent(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var scope = ParseSubAgentScope(parameters);
        var name = RequiredString(parameters, "name");
        var tools = parameters["tools"] is JsonArray array
            ? array
                .Select(node => node?.GetValue<string>()?.Trim())
                .Where(tool => !string.IsNullOrWhiteSpace(tool))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : null;

        string path;
        try
        {
            path = _subAgents.Save(
                ProjectRoot(workspace),
                scope,
                new SubAgentDraft(
                    name,
                    OptionalString(parameters, "description"),
                    OptionalString(parameters, "model"),
                    tools,
                    RequiredString(parameters, "prompt")),
                OptionalString(parameters, "previousName"));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                        or IOException or UnauthorizedAccessException)
        {
            throw new DaemonRequestException(ex.Message);
        }

        return new JsonObject
        {
            ["path"] = path,
            ["name"] = name.Trim(),
            ["scope"] = ScopeName(scope),
        }.ToJsonString();
    }

    public string DeleteSubAgent(JsonObject parameters)
    {
        var workspace = SessionWorkspace(parameters);
        var scope = ParseSubAgentScope(parameters);
        var name = RequiredString(parameters, "name");
        bool deleted;
        try
        {
            deleted = _subAgents.Delete(ProjectRoot(workspace), scope, name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                        or IOException or UnauthorizedAccessException)
        {
            throw new DaemonRequestException(ex.Message);
        }

        return new JsonObject
        {
            ["deleted"] = deleted,
            ["name"] = name.Trim(),
            ["scope"] = ScopeName(scope),
        }.ToJsonString();
    }

    private static SubAgentScope ParseSubAgentScope(JsonObject parameters) =>
        (OptionalString(parameters, "scope") ?? "user").ToLowerInvariant() switch
        {
            "user" or "global" => SubAgentScope.User,
            "project" or "workspace" => SubAgentScope.Project,
            _ => throw new DaemonRequestException("scope 必须是 user 或 project。"),
        };

    private static string ScopeName(SubAgentScope scope) => scope switch
    {
        SubAgentScope.Builtin => "builtin",
        SubAgentScope.User => "user",
        SubAgentScope.Project => "project",
        _ => "user",
    };

    private static string? ProjectRoot(WorkspaceInfo workspace) => workspace.IsGlobal ? null : workspace.Root;
}
