using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>豆包语音同源：speech_to_text 的可用性判断与转写请求（含 X-Api-* 头）。</summary>
public class SpeechToTextToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-stt-" + Guid.NewGuid().ToString("N"));

    public SpeechToTextToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static SpeechConfig SpeechAt(int port, string? apiKey = "test-key")
    {
        var speech = new SpeechConfig { ApiKey = apiKey };
        speech.AsrBaseUrl = $"http://127.0.0.1:{port}/api/v3/auc/bigmodel/recognize/flash";
        return speech;
    }

    [Fact]
    public void IsAvailable_RequiresApiKey()
    {
        Assert.False(SpeechToTextTool.IsAvailable(new SpeechConfig()));
        Assert.True(SpeechToTextTool.IsAvailable(new SpeechConfig { ApiKey = "k" }));
    }

    [Fact]
    public async Task TranscribeAsync_PostsAudioAndReturnsText()
    {
        using var server = new MockHttpServer(
            MockHttpServer.MockResponse.Json(new JsonObject
            {
                ["result"] = new JsonObject { ["text"] = "今天天气怎么样" },
            }));

        var wav = Path.Combine(_dir, "voice.wav");
        await File.WriteAllBytesAsync(wav, "fake-wav"u8.ToArray());

        var text = await SpeechToTextTool.TranscribeAsync(
            SpeechAt(server.Port), wav, null, CancellationToken.None);

        Assert.Equal("今天天气怎么样", text);
        var headers = server.RequestHeaders.Single();
        Assert.Contains("X-Api-Key: test-key", headers, StringComparison.Ordinal);
        Assert.Contains("X-Api-Resource-Id: volc.bigasr.auc_turbo", headers, StringComparison.Ordinal);
        Assert.Contains("X-Api-Sequence: -1", headers, StringComparison.Ordinal);
        var body = JsonNode.Parse(server.RequestBodies.Single());
        Assert.Equal("bigmodel", body!["request"]!["model_name"]!.GetValue<string>());
        Assert.NotNull(body["audio"]!["data"]);
    }

    [Fact]
    public async Task TranscribeAsync_SupportsUrlInput()
    {
        using var server = new MockHttpServer(
            MockHttpServer.MockResponse.Json(new JsonObject
            {
                ["result"] = new JsonObject { ["text"] = "hello" },
            }));
        var text = await SpeechToTextTool.TranscribeAsync(
            SpeechAt(server.Port), null, "https://example.com/voice.mp3", CancellationToken.None);
        Assert.Equal("hello", text);
        var body = JsonNode.Parse(server.RequestBodies.Single());
        Assert.Equal("https://example.com/voice.mp3", body!["audio"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task TranscribeAsync_WithoutApiKey_Throws()
    {
        using var server = new MockHttpServer(
            MockHttpServer.MockResponse.Json(new JsonObject { ["result"] = new JsonObject { ["text"] = "x" } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SpeechToTextTool.TranscribeAsync(SpeechAt(server.Port, null), null, "https://example.com/a.mp3", CancellationToken.None));
    }

    [Fact]
    public async Task Execute_TranscribesLocalAudio()
    {
        using var server = new MockHttpServer(
            MockHttpServer.MockResponse.Json(new JsonObject
            {
                ["result"] = new JsonObject { ["text"] = "帮我写一封邮件" },
            }));

        var wav = Path.Combine(_dir, "voice.wav");
        await File.WriteAllBytesAsync(wav, "fake-wav"u8.ToArray());

        var config = new HaoyueConfig { Speech = SpeechAt(server.Port) };
        var tool = new SpeechToTextTool(new FilePromptProvider(), MakeConfigStore(config));
        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };
        var result = await tool.ExecuteAsync(
            new JsonObject { ["audio_path"] = wav }, context, CancellationToken.None);
        Assert.True(result.Success, result.Output);
        Assert.Equal("帮我写一封邮件", result.Output);
    }

    private ConfigStore MakeConfigStore(HaoyueConfig config)
    {
        var file = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
            config, Haoyue.Runtime.Configuration.HaoyueJsonContext.Default.HaoyueConfig));
        return new ConfigStore(file, Path.Combine(_dir, $"state-{Guid.NewGuid():N}.json"));
    }
}
