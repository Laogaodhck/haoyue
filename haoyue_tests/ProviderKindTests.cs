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

/// <summary>
/// 云厂商 kind：桌面端可单独展示/预填，但线上请求必须落回 OpenAI 兼容客户端；
/// provider.upsert 接受这些取值并原样保留，未知协议直接拒绝。
/// </summary>
public sealed class ProviderKindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"haoyue-provider-kind-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void VendorKinds_ResolveToOpenAiCompatibleClient()
    {
        foreach (var kind in new[]
                 {
                     ProviderKinds.OpenAi, ProviderKinds.Aliyun, ProviderKinds.Volcengine,
                     ProviderKinds.Tencent, ProviderKinds.Baidu, ProviderKinds.Zhipu, ProviderKinds.DeepSeek,
                 })
        {
            Assert.Equal(ProviderKinds.OpenAi, ProviderKinds.ToClientKind(kind));
        }

        Assert.Equal(ProviderKinds.Anthropic, ProviderKinds.ToClientKind("ANTHROPIC"));
        Assert.Equal(ProviderKinds.Local, ProviderKinds.ToClientKind("Local"));
        // 未知协议原样返回，让客户端工厂报明确错误，而不是静默降级成 OpenAI。
        Assert.Equal("mystery", ProviderKinds.ToClientKind("mystery"));
        Assert.True(ProviderKinds.IsKnown("Aliyun"));
        Assert.False(ProviderKinds.IsKnown("mystery"));
    }

    [Fact]
    public async Task UpsertProvider_AcceptsVendorKinds_AndRejectsUnknown()
    {
        var root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), "{}");
        var workspace = new WorkspaceManager().Detect(root);
        var global = new WorkspaceManager().CreateGlobal(Path.Combine(_dir, "global"));
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
        await using var runtime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(store);
            services.AddSingleton(new HaoyueDatabase(Path.Combine(_dir, "state.db")));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
        });
        var admin = new DaemonAdminApi(
            runtime, global, new FileLockCoordinator(),
            subAgents: new SubAgentCatalog(Path.Combine(_dir, "agents")));

        foreach (var kind in new[]
                 {
                     ProviderKinds.Aliyun, ProviderKinds.Volcengine, ProviderKinds.Tencent,
                     ProviderKinds.Baidu, ProviderKinds.Zhipu, ProviderKinds.DeepSeek,
                 })
        {
            var saved = JsonNode.Parse(admin.UpsertProvider(new JsonObject
            {
                ["id"] = $"vendor-{kind}",
                ["kind"] = kind,
                ["baseUrl"] = "https://example.test/v1",
                ["models"] = new JsonArray("test-model"),
            }))!.AsObject();
            Assert.Equal(kind, saved["kind"]!.GetValue<string>());
        }

        Assert.Throws<DaemonRequestException>(() => admin.UpsertProvider(new JsonObject
        {
            ["id"] = "bad-kind",
            ["kind"] = "mystery",
            ["baseUrl"] = "https://example.test/v1",
            ["models"] = new JsonArray("test-model"),
        }));
    }
}
