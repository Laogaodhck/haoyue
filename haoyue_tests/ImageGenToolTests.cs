using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>G7：多模态输出——图像生成工具的端点解析与产物落盘。</summary>
public class ImageGenToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-imggen-" + Guid.NewGuid().ToString("N"));

    public ImageGenToolTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ResolveEndpoint_ExplicitReference_MissingProvider_ReturnsNull()
    {
        var config = new HaoyueConfig();
        config.ImageGen.Model = "ghost/image-model";
        Assert.Null(ImageGenTool.ResolveEndpoint(config));
    }

    [Fact]
    public void ResolveEndpoint_AutoSelectsImageCapableModel()
    {
        var config = new HaoyueConfig
        {
            Providers =
            [
                new ProviderConfig
                {
                    Id = "text-only",
                    BaseUrl = "https://api.example.com/v1",
                    Models = [new ModelConfig { Id = "chat" }],
                },
                new ProviderConfig
                {
                    Id = "visual",
                    BaseUrl = "https://img.example.com/v1",
                    Models = [new ModelConfig { Id = "dalle-x", Capabilities = new ModelCapabilities { Image = true } }],
                },
            ],
        };
        var endpoint = ImageGenTool.ResolveEndpoint(config);
        Assert.NotNull(endpoint);
        Assert.Equal("visual", endpoint!.Value.Provider.Id);
        Assert.Equal("dalle-x", endpoint.Value.Model);
    }

    [Fact]
    public async Task Execute_SavesDecodedImageIntoWorkspace()
    {
        // 1x1 透明 PNG
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        ];

        using var server = new OneShotJsonServer(
            new JsonObject { ["data"] = new JsonArray(new JsonObject { ["b64_json"] = Convert.ToBase64String(png) }) });

        var provider = new ProviderConfig
        {
            Id = "local-test",
            BaseUrl = $"http://127.0.0.1:{server.Port}/v1",
            Models = [new ModelConfig { Id = "img-1", Capabilities = new ModelCapabilities { Image = true } }],
        };
        var config = new HaoyueConfig { Providers = [provider] };
        config.ImageGen.Model = "local-test/img-1";

        var http = new LlmHttpFactory();
        var tool = new ImageGenTool(new FilePromptProvider(), MakeConfigStore(config), http);
        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };

        var result = await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "一枚极简风格的圆形图标" }, context, CancellationToken.None);

        Assert.True(result.Success, result.Output);
        var saved = Directory.GetFiles(Path.Combine(_dir, ".haoyue", "outputs", "images"));
        Assert.Single(saved);
        Assert.Equal(png, await File.ReadAllBytesAsync(saved[0]));
        Assert.EndsWith(".png", Path.GetFileName(saved[0]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_WithoutConfiguredProvider_FailsWithGuidance()
    {
        var tool = new ImageGenTool(new FilePromptProvider(), MakeConfigStore(new HaoyueConfig()), new LlmHttpFactory());
        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };
        var result = await tool.ExecuteAsync(
            new JsonObject { ["prompt"] = "anything" }, context, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("image", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把配置写入临时文件，让 ConfigStore 从盘上加载（Config 属性只读）。</summary>
    private ConfigStore MakeConfigStore(HaoyueConfig config)
    {
        var file = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
            config, Haoyue.Runtime.Configuration.HaoyueJsonContext.Default.HaoyueConfig));
        return new ConfigStore(file, Path.Combine(_dir, $"state-{Guid.NewGuid():N}.json"));
    }

    /// <summary>一次性 TCP 服务器：收到任一请求后回一份 JSON 响应并关闭。</summary>
    private sealed class OneShotJsonServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public int Port { get; }

        public OneShotJsonServer(JsonObject payload)
        {
            _payload = payload;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoop();
        }

        private readonly JsonObject _payload;

        private async Task AcceptLoop()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var buffer = new byte[4096];
                while (await stream.ReadAsync(buffer) > 0)
                {
                    if (Encoding.ASCII.GetString(buffer, 0, buffer.Length).Contains("\r\n\r\n"))
                        break;
                }
                var body = _payload.ToJsonString();
                var response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                               $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n" + body;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
            }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => _listener.Stop();
    }
}
