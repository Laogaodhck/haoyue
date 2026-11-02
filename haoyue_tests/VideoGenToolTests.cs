using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>豆包式视频生成：seedance 异步任务创建 → 轮询 → MP4 下载落盘。</summary>
public class VideoGenToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-videogen-" + Guid.NewGuid().ToString("N"));

    public VideoGenToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (ProviderConfig Provider, HaoyueConfig Config) MakeVideoConfig(int port, string modelId = "doubao-seedance-2-5-260628")
    {
        var provider = new ProviderConfig
        {
            Id = "ark",
            BaseUrl = $"http://127.0.0.1:{port}/v1",
            Models = [new ModelConfig { Id = modelId }],
        };
        var config = new HaoyueConfig { Providers = [provider] };
        config.VideoGen.Model = $"ark/{modelId}";
        config.VideoGen.PollIntervalSeconds = 5;
        config.VideoGen.WaitSeconds = 60;
        return (provider, config);
    }

    [Fact]
    public void ResolveVideoEndpoint_RequiresExplicitReference()
    {
        Assert.Null(VideoGenTool.ResolveVideoEndpoint(new HaoyueConfig()));
        var (_, config) = MakeVideoConfig(1);
        var endpoint = VideoGenTool.ResolveVideoEndpoint(config);
        Assert.NotNull(endpoint);
        Assert.Equal("ark", endpoint!.Value.Provider.Id);
        Assert.Equal("doubao-seedance-2-5-260628", endpoint.Value.Model);
    }

    [Fact]
    public async Task CreateTaskAsync_PostsContentAndReadsId()
    {
        using var server = new MockHttpServer(
            MockHttpServer.MockResponse.Json(new JsonObject { ["id"] = "task-abc" }));
        var (provider, _) = MakeVideoConfig(server.Port);

        var client = new LlmHttpFactory().GetClient(provider);
        var id = await VideoGenTool.CreateTaskAsync(
            client, provider,
            new JsonObject
            {
                ["model"] = "doubao-seedance-2-5-260628",
                ["content"] = new JsonArray((JsonNode)new JsonObject { ["type"] = "text", ["text"] = "一只猫在奔跑" }),
                ["ratio"] = "16:9",
                ["duration"] = 5,
                ["generate_audio"] = true,
                ["watermark"] = true,
            },
            CancellationToken.None);

        Assert.Equal("task-abc", id);
        Assert.Contains("POST /v1/contents/generations/tasks", server.RequestLines[0], StringComparison.Ordinal);
        var payload = JsonNode.Parse(server.RequestBodies[0]);
        Assert.Equal("一只猫在奔跑", payload!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task QueryTaskAsync_ReadsStatusAndVideoUrl()
    {
        using var server = new MockHttpServer(
            MockHttpServer.MockResponse.Json(new JsonObject
            {
                ["status"] = "succeeded",
                ["content"] = new JsonObject { ["video_url"] = "https://example.com/video.mp4" },
            }));
        var (provider, _) = MakeVideoConfig(server.Port);

        var (status, videoUrl, error) = await VideoGenTool.QueryTaskAsync(
            new LlmHttpFactory().GetClient(provider), provider, "task-abc", CancellationToken.None);

        Assert.Equal("succeeded", status);
        Assert.Equal("https://example.com/video.mp4", videoUrl);
        Assert.Null(error);
        Assert.Contains("GET /v1/contents/generations/tasks/task-abc", server.RequestLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollUntilDone_ReturnsSucceededWithoutExtraDelay()
    {
        using var server = new MockHttpServer(
            MockHttpServer.MockResponse.Json(new JsonObject
            {
                ["status"] = "succeeded",
                ["content"] = new JsonObject { ["video_url"] = "https://example.com/video.mp4" },
            }));
        var (provider, _) = MakeVideoConfig(server.Port);

        var (status, videoUrl, _, timedOut) = await VideoGenTool.PollUntilDoneAsync(
            new LlmHttpFactory().GetClient(provider), provider, "task-abc", waitSeconds: 60, pollInterval: 0, CancellationToken.None);

        Assert.Equal("succeeded", status);
        Assert.Equal("https://example.com/video.mp4", videoUrl);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task Execute_EndToEnd_DownloadsMp4IntoWorkspace()
    {
        var mp4 = "fake-mp4-bytes"u8.ToArray();
        using var server = new MockHttpServer();
        server.Enqueue(MockHttpServer.MockResponse.Json(new JsonObject { ["id"] = "task-abc" }));
        server.Enqueue(MockHttpServer.MockResponse.Json(new JsonObject
        {
            ["status"] = "succeeded",
            ["content"] = new JsonObject { ["video_url"] = $"http://127.0.0.1:{server.Port}/final.mp4" },
        }));
        server.Enqueue(MockHttpServer.MockResponse.Bytes(mp4));

        var (_, config) = MakeVideoConfig(server.Port);
        var tool = new VideoGenTool(new FilePromptProvider(), MakeConfigStore(config), new LlmHttpFactory());
        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };

        var result = await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "一只猫在草原上奔跑，镜头跟随", ["file_name"] = "cat-run" },
            context, CancellationToken.None);

        Assert.True(result.Success, result.Output);
        var saved = Directory.GetFiles(Path.Combine(_dir, ".haoyue", "outputs", "videos"));
        Assert.Single(saved);
        Assert.Equal("cat-run.mp4", Path.GetFileName(saved[0]));
        Assert.Equal(mp4, await File.ReadAllBytesAsync(saved[0]));
        Assert.Equal(3, server.RequestLines.Count);
    }

    [Fact]
    public async Task Execute_WithoutConfiguredVideoModel_FailsWithGuidance()
    {
        var tool = new VideoGenTool(new FilePromptProvider(), MakeConfigStore(new HaoyueConfig()), new LlmHttpFactory());
        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };
        var result = await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "anything" }, context, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("videoGen.model", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private ConfigStore MakeConfigStore(HaoyueConfig config)
    {
        var file = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
            config, Haoyue.Runtime.Configuration.HaoyueJsonContext.Default.HaoyueConfig));
        return new ConfigStore(file, Path.Combine(_dir, $"state-{Guid.NewGuid():N}.json"));
    }
}
