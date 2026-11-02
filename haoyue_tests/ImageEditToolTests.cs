using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>豆包式图片编辑：image_edit 的 data URI 输入、端点复用与产物落盘。</summary>
public class ImageEditToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-imgedit-" + Guid.NewGuid().ToString("N"));

    public ImageEditToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] TinyPng =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    ];

    [Fact]
    public async Task Execute_LocalImage_BecomesDataUriAndSavesResult()
    {
        var reference = Path.Combine(_dir, "reference.png");
        await File.WriteAllBytesAsync(reference, TinyPng);

        using var server = new MockHttpServer(
            MockHttpServer.MockResponse.Json(new JsonObject
            {
                ["data"] = new JsonArray(new JsonObject { ["b64_json"] = Convert.ToBase64String(TinyPng) }),
            }));

        var provider = new ProviderConfig
        {
            Id = "local-test",
            BaseUrl = $"http://127.0.0.1:{server.Port}/v1",
            Models = [new ModelConfig { Id = "img-1", Capabilities = new ModelCapabilities { Image = true } }],
        };
        var config = new HaoyueConfig { Providers = [provider] };
        config.ImageGen.Model = "local-test/img-1";

        var tool = new ImageEditTool(new FilePromptProvider(), MakeConfigStore(config), new LlmHttpFactory());
        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };

        var result = await tool.ExecuteAsync(
            new JsonObject
            {
                ["prompt"] = "把画面中的红色汽车换成蓝色",
                ["image_path"] = reference,
                ["file_name"] = "edited-car",
            },
            context, CancellationToken.None);

        Assert.True(result.Success, result.Output);
        var body = server.RequestBodies.Single();
        var payload = JsonNode.Parse(body);
        Assert.Equal("img-1", payload!["model"]!.GetValue<string>());
        Assert.Equal("把画面中的红色汽车换成蓝色", payload["prompt"]!.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,", payload["image"]!.GetValue<string>(), StringComparison.Ordinal);

        var saved = Directory.GetFiles(Path.Combine(_dir, ".haoyue", "outputs", "images"));
        Assert.Single(saved);
        Assert.Equal("edited-car.png", Path.GetFileName(saved[0]));
        Assert.Equal(TinyPng, await File.ReadAllBytesAsync(saved[0]));
    }

    [Fact]
    public async Task Execute_WithoutImageCapableModel_FailsWithGuidance()
    {
        var tool = new ImageEditTool(new FilePromptProvider(), MakeConfigStore(new HaoyueConfig()), new LlmHttpFactory());
        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };
        var result = await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "消除背景", ["image_path"] = Path.Combine(_dir, "x.png") },
            context, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("image", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_RequiresReferenceImage()
    {
        var tool = new ImageEditTool(new FilePromptProvider(), MakeConfigStore(new HaoyueConfig()), new LlmHttpFactory());
        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };
        var result = await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "改个颜色" }, context, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("reference image", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private ConfigStore MakeConfigStore(HaoyueConfig config)
    {
        var file = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
            config, Haoyue.Runtime.Configuration.HaoyueJsonContext.Default.HaoyueConfig));
        return new ConfigStore(file, Path.Combine(_dir, $"state-{Guid.NewGuid():N}.json"));
    }
}
