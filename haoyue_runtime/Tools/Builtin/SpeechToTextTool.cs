using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Secrets;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// 语音转文字（豆包语音同源 ASR，录音文件极速版识别）：一次请求即返回
/// 识别文本，支持 WAV / MP3 / OGG Opus（≤100MB）。音频可传本地路径
/// （自动转 base64）或公网 URL。未配置 speech.apiKey 时工具隐藏。
/// 核心请求逻辑抽为 <see cref="TranscribeAsync"/>，供 daemon RPC 与工具共用。
/// </summary>
public sealed class SpeechToTextTool(
    IPromptProvider prompts,
    IConfigStore configStore) : BuiltinTool(prompts)
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(180) };

    public override string Name => "speech_to_text";
    public override string StatusLabel => "Transcribing audio";
    public override bool Mutating => false;
    public ToolRiskTier RiskTier => ToolRiskTier.Network;
    public override bool RequiresNetwork => true;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("audio_path", ToolSchema.String("Local path of the audio file (wav/mp3/ogg, ≤100MB). Mutually exclusive with audio_url."), false),
        ("audio_url", ToolSchema.String("Public URL of the audio file. Mutually exclusive with audio_path."), false));

    /// <summary>语音能力是否可用（speech.apiKey 已配置且可解析）。</summary>
    public static bool IsAvailable(SpeechConfig speech) =>
        !string.IsNullOrWhiteSpace(SecretResolver.Resolve(speech.ApiKey));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var path = GetString(arguments, "audio_path");
        var url = GetString(arguments, "audio_url");
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(url))
            return ToolResult.Fail("speech_to_text needs an audio input: pass audio_path (local file) or audio_url.");

        try
        {
            var text = await TranscribeAsync(configStore.Config.Speech, path, url, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
                return ToolResult.Fail("ASR returned no transcript (the audio may be silent or unsupported).");
            return ToolResult.Ok(text, "Audio transcribed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"speech_to_text failed: {ex.Message}");
        }
    }

    /// <summary>转写音频（本地路径或 URL），返回识别文本。失败抛异常（消息可直接展示）。</summary>
    public static async Task<string?> TranscribeAsync(
        SpeechConfig speech, string? audioPath, string? audioUrl, CancellationToken ct)
    {
        var apiKey = SecretResolver.Resolve(speech.ApiKey);
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("speech.apiKey is not configured.");

        JsonObject audio;
        if (audioPath is { Length: > 0 })
        {
            var full = Path.GetFullPath(audioPath);
            if (!File.Exists(full))
                throw new FileNotFoundException($"audio file not found: {full}");
            var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            if (bytes.Length > 100 * 1024 * 1024)
                throw new InvalidOperationException("audio file exceeds the 100MB limit of the recognition endpoint.");
            audio = new JsonObject { ["data"] = Convert.ToBase64String(bytes) };
        }
        else if (audioUrl is { Length: > 0 })
        {
            audio = new JsonObject { ["url"] = audioUrl };
        }
        else
        {
            throw new InvalidOperationException("audio_path or audio_url is required.");
        }

        var body = new JsonObject
        {
            ["user"] = new JsonObject { ["uid"] = apiKey },
            ["audio"] = audio,
            ["request"] = new JsonObject { ["model_name"] = "bigmodel" },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, speech.AsrBaseUrl)
        {
            Content = new StringContent(body.ToJsonString(),
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.TryAddWithoutValidation("X-Api-Resource-Id", speech.AsrResourceId);
        request.Headers.TryAddWithoutValidation("X-Api-Request-Id", Guid.NewGuid().ToString("N"));
        request.Headers.TryAddWithoutValidation("X-Api-Sequence", "-1");

        using var response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
        var respBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ASR endpoint returned {(int)response.StatusCode}: {Shorten(respBody)}");
        var payload = JsonNode.Parse(respBody) ?? throw new InvalidOperationException("response is not JSON");
        return payload["result"]?["text"]?.GetValue<string>();
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
