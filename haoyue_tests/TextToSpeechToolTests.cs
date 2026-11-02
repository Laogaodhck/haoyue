using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>豆包语音同源：text_to_speech 的合成（含流式多 JSON 拼接）与产物落盘。</summary>
public class TextToSpeechToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-tts-" + Guid.NewGuid().ToString("N"));

    public TextToSpeechToolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static SpeechConfig SpeechAt(int port)
    {
        var speech = new SpeechConfig { ApiKey = "test-key" };
        speech.TtsBaseUrl = $"http://127.0.0.1:{port}/api/v3/tts/unidirectional";
        return speech;
    }

    [Fact]
    public async Task SynthesizeAsync_DecodesSinglePayload()
    {
        var audio = "fake-mp3-bytes"u8.ToArray();
        using var server = new MockHttpServer(
            MockHttpServer.MockResponse.Json(new JsonObject { ["code"] = 0, ["data"] = Convert.ToBase64String(audio) }));

        var (bytes, format) = await TextToSpeechTool.SynthesizeAsync(
            SpeechAt(server.Port), "你好", null, CancellationToken.None);

        Assert.Equal(audio, bytes);
        Assert.Equal("mp3", format);
        var headers = server.RequestHeaders.Single();
        Assert.Contains("X-Api-Key: test-key", headers, StringComparison.Ordinal);
        Assert.Contains("X-Api-Resource-Id: seed-tts-2.0", headers, StringComparison.Ordinal);
        var body = JsonNode.Parse(server.RequestBodies.Single());
        Assert.Equal("你好", body!["req_params"]!["text"]!.GetValue<string>());
        Assert.Equal("zh_female_vv_uranus_bigtts", body["req_params"]!["speaker"]!.GetValue<string>());
    }

    [Fact]
    public void ExtractAudioData_ConcatenatesChunkedJsonPayloads()
    {
        // HTTP chunked 下服务端推送多个 JSON 对象：每个都带 data 字段，需要逐个拼接。
        var audio = "streamed-audio"u8.ToArray();
        var half = audio.Length / 2;
        var body =
            $"{{\"code\":0,\"data\":\"{Convert.ToBase64String(audio[..half])}\"}}\r\n" +
            $"{{\"code\":0,\"data\":\"{Convert.ToBase64String(audio[half..])}\"}}\r\n";
        var decoded = TextToSpeechTool.ExtractAudioData(body);
        Assert.Equal(audio, decoded);
    }

    [Fact]
    public void ExtractAudioData_SingleJson_ParsesDirectly()
    {
        var audio = "whole-audio"u8.ToArray();
        var body = $"{{\"code\":0,\"data\":\"{Convert.ToBase64String(audio)}\"}}";
        Assert.Equal(audio, TextToSpeechTool.ExtractAudioData(body));
    }

    [Fact]
    public async Task Execute_SavesSynthesizedAudioIntoWorkspace()
    {
        var audio = "fake-mp3-bytes"u8.ToArray();
        using var server = new MockHttpServer(
            MockHttpServer.MockResponse.Json(new JsonObject { ["code"] = 0, ["data"] = Convert.ToBase64String(audio) }));

        var config = new HaoyueConfig { Speech = SpeechAt(server.Port) };
        var tool = new TextToSpeechTool(new FilePromptProvider(), MakeConfigStore(config));
        var context = new ToolContext
        {
            Workspace = new WorkspaceInfo { Root = _dir, ProjectKinds = [] },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };
        var result = await tool.ExecuteAsync(
            new JsonObject { ["text"] = "晚上好", ["file_name"] = "greeting" }, context, CancellationToken.None);

        Assert.True(result.Success, result.Output);
        var saved = Directory.GetFiles(Path.Combine(_dir, ".haoyue", "outputs", "audio"));
        Assert.Single(saved);
        Assert.Equal("greeting.mp3", Path.GetFileName(saved[0]));
        Assert.Equal(audio, await File.ReadAllBytesAsync(saved[0]));
    }

    private ConfigStore MakeConfigStore(HaoyueConfig config)
    {
        var file = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(
            config, Haoyue.Runtime.Configuration.HaoyueJsonContext.Default.HaoyueConfig));
        return new ConfigStore(file, Path.Combine(_dir, $"state-{Guid.NewGuid():N}.json"));
    }
}
