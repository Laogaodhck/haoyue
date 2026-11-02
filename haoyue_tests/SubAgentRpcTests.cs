using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Daemon;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Workspaces;
using Xunit;

namespace Haoyue.Tests;

/// <summary>agent.subagents.* RPC：用户级 / 项目级落盘、项目覆盖用户、删除回落与参数校验。</summary>
public sealed class SubAgentRpcTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"haoyue-subagent-rpc-{Guid.NewGuid():N}");
    private readonly string _userDir;

    public SubAgentRpcTests()
    {
        _userDir = Path.Combine(_dir, "agents");
        Directory.CreateDirectory(_userDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task ListSaveDelete_RoundTripsUserAndProjectScopes()
    {
        var (runtime, admin, workspace) = CreateAdmin();
        await using var _ = runtime;

        var listed = Parse(admin.ListSubAgents(new JsonObject { ["workspace"] = workspace.Root }));
        Assert.False(listed["isGlobal"]!.GetValue<bool>());
        Assert.Contains("reviewer", Ids(listed));

        var userSave = Parse(admin.SaveSubAgent(new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["scope"] = "user",
            ["name"] = "auditor",
            ["description"] = "审计改动",
            ["model"] = "openai/strong",
            ["tools"] = new JsonArray(JsonValue.Create("read_file"), JsonValue.Create("grep")),
            ["prompt"] = "你负责审计。",
        }));
        Assert.Equal("user", userSave["scope"]!.GetValue<string>());
        Assert.True(File.Exists(userSave["path"]!.GetValue<string>()));

        var projectSave = Parse(admin.SaveSubAgent(new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["scope"] = "project",
            ["name"] = "reviewer",
            ["description"] = "项目评审",
            ["prompt"] = "项目级评审指令。",
        }));
        Assert.Equal("project", projectSave["scope"]!.GetValue<string>());
        Assert.StartsWith(SubAgentCatalog.ProjectAgentsDir(workspace.Root),
            projectSave["path"]!.GetValue<string>());

        // 项目级同名定义覆盖内置 reviewer；用户级 auditor 同时在列。
        listed = Parse(admin.ListSubAgents(new JsonObject { ["workspace"] = workspace.Root }));
        Assert.Contains("auditor", Ids(listed));
        var reviewer = listed["agents"]!.AsArray()
            .Single(agent => agent!["id"]!.GetValue<string>() == "reviewer")!.AsObject();
        Assert.Equal("project", reviewer["scope"]!.GetValue<string>());
        Assert.Equal("项目评审", reviewer["description"]!.GetValue<string>());
        Assert.Equal("项目级评审指令。", reviewer["prompt"]!.GetValue<string>());
        Assert.False(reviewer["readOnly"]!.GetValue<bool>());

        // 删除项目级定义后，同名内置 reviewer 重新生效、恢复只读。
        var deleted = Parse(admin.DeleteSubAgent(new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["scope"] = "project",
            ["name"] = "reviewer",
        }));
        Assert.True(deleted["deleted"]!.GetValue<bool>());

        listed = Parse(admin.ListSubAgents(new JsonObject { ["workspace"] = workspace.Root }));
        reviewer = listed["agents"]!.AsArray()
            .Single(agent => agent!["id"]!.GetValue<string>() == "reviewer")!.AsObject();
        Assert.Equal("builtin", reviewer["scope"]!.GetValue<string>());
        Assert.True(reviewer["readOnly"]!.GetValue<bool>());

        var missing = Parse(admin.DeleteSubAgent(new JsonObject
        {
            ["workspace"] = workspace.Root,
            ["scope"] = "project",
            ["name"] = "reviewer",
        }));
        Assert.False(missing["deleted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Save_RejectsBuiltinScopeAndUnknownScope()
    {
        var (runtime, admin, _) = CreateAdmin();
        await using var _runtime = runtime;

        Assert.Throws<DaemonRequestException>(() => admin.SaveSubAgent(new JsonObject
        {
            ["scope"] = "builtin",
            ["name"] = "x",
            ["prompt"] = "y",
        }));
        Assert.Throws<DaemonRequestException>(() => admin.SaveSubAgent(new JsonObject
        {
            ["scope"] = "elsewhere",
            ["name"] = "x",
            ["prompt"] = "y",
        }));
        Assert.Throws<DaemonRequestException>(() => admin.SaveSubAgent(new JsonObject
        {
            ["global"] = true,
            ["scope"] = "project",
            ["name"] = "x",
            ["prompt"] = "y",
        })); // 全局会话没有项目根
    }

    private (HaoyueRuntime Runtime, DaemonAdminApi Admin, WorkspaceInfo Workspace) CreateAdmin()
    {
        var root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), "{}");
        var workspace = new WorkspaceManager().Detect(root);
        var global = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global"));
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
        var runtime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(store);
            services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "state.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
        });
        var admin = new DaemonAdminApi(
            runtime, global, new FileLockCoordinator(), subAgents: new SubAgentCatalog(_userDir));
        return (runtime, admin, workspace);
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static List<string> Ids(JsonObject listed) => listed["agents"]!.AsArray()
        .Select(agent => agent!["id"]!.GetValue<string>())
        .ToList();
}
