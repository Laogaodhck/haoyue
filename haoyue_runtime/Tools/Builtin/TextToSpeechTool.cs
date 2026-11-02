using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Secrets;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// 文字转语音（豆包语音同源 TTS，大模型语音合成）：一次性输入文本，
/// 合成 mp3/wav/ogg_opus 音频并落盘到工作区 .haoyue/outputs/audio/。
/// 核心请求逻辑抽为 <see cref="SynthesizeAsync"/>，供 daemon RPC 与工具共用。
/// </summary>
public sealed partial class TextToSpeechTool(
    IPromptProvider prompts,
    IConfigStore configStore) : BuiltinTool(prompts)
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(120) };

    public override string Name => "text_to_speech";
    public override string StatusLabel => "Synthesizing speech";
    public override bool Mutating => true;
    public ToolRiskTier RiskTier => ToolRiskTier.Network | ToolRiskTier.Write;
    public override bool RequiresNetwork => true;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("text", ToolSchema.String("Text to synthesize (up to a few thousand characters per request)."), true),
        ("speaker", ToolSchema.String("Voice id from the console voice library; defaults to speech.ttsSpeaker."), false),
        ("file_name", ToolSchema.String("Optional output file name without extension; a timestamped default is used otherwise."), false));

    [GeneratedRegex("\"data\"\\s*:\\s*\"([A-Za-z0-9+/=]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex DataFieldRegex();

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var text = GetString(arguments, "text");
        if (string.IsNullOrWhiteSpace(text))
            return ToolResult.Fail("text is required.");

        byte[] bytes;
        string format;
        try
        {
            (bytes, format) = await SynthesizeAsync(configStore.Config.Speech, text, GetString(arguments, "speaker"), ct).ConfigureAwait(false);
            if (bytes.Length == 0)
                return ToolResult.Fail("TTS returned no audio payload.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"text_to_speech failed: {ex.Message}");
        }

        if (context.Workspace is null)
            return ToolResult.Fail("text_to_speech requires a workspace to write the artifact into.");

        var outputsDir = Path.Combine(context.Workspace.Root, ".haoyue", "outputs", "audio");
        Directory.CreateDirectory(outputsDir);
        var customName = GetString(arguments, "file_name");
        var extension = "." + (format == "ogg_opus" ? "ogg" : format);
        var file = Sanitize(customName) is { Length: > 0 } safeName
            ? Path.Combine(outputsDir, $"{safeName}{extension}")
            : Path.Combine(outputsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{extension}");

        await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
        return ToolResult.Ok(
            $"Speech saved: {file} ({bytes.Length:N0} bytes).",
            $"Speech audio: {Path.GetFileName(file)}");
    }

    /// <summary>合成语音，返回 (音频字节, 格式)。失败抛异常（消息可直接展示）。</summary>
    public static async Task<(byte[] Bytes, string Format)> SynthesizeAsync(
        SpeechConfig speech, string text, string? speaker, CancellationToken ct)
    {
        var apiKey = SecretResolver.Resolve(speech.ApiKey);
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException("speech.apiKey is not configured.");

        var format = string.IsNullOrWhiteSpace(speech.TtsFormat) ? "mp3" : speech.TtsFormat;
        var sampleRate = Math.Clamp(speech.TtsSampleRate, 8000, 48000);
        var body = new JsonObject
        {
            ["req_params"] = new JsonObject
            {
                ["text"] = text,
                ["speaker"] = speaker ?? speech.TtsSpeaker,
                ["audio_params"] = new JsonObject
                {
                    ["format"] = format,
                    ["sample_rate"] = sampleRate,
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, speech.TtsBaseUrl)
        {
            Content = new StringContent(body.ToJsonString(),
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.TryAddWithoutValidation("X-Api-Resource-Id", speech.TtsResourceId);
        request.Headers.TryAddWithoutValidation("X-Api-Request-Id", Guid.NewGuid().ToString("N"));

        using var response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
        var respBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"TTS endpoint returned {(int)response.StatusCode}: {Shorten(respBody)}");
        var bytes = ExtractAudioData(respBody);
        return (bytes, format);
    }

    /// <summary>
    /// 提取流式合成响应中的音频块。服务端按 HTTP chunked 推送多个 JSON 对象，
    /// 单个请求也可能整体返回一个 JSON：先整段解析，失败则按字段正则逐个解码
    /// base64 块并拼接字节流（每个块自带 padding，不能先拼字符串再解码）。
    /// </summary>
    internal static byte[] ExtractAudioData(string body)
    {
        try
        {
            var single = JsonNode.Parse(body);
            var data = single?["data"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(data))
                return Convert.FromBase64String(data);
        }
        catch (Exception)
        {
            // 多个 JSON 拼接或含非 JSON 噪声——走逐块路径。
        }

        using var stream = new MemoryStream();
        foreach (Match match in DataFieldRegex().Matches(body))
        {
            var block = Convert.FromBase64String(match.Groups[1].Value);
            stream.Write(block);
        }
        return stream.ToArray();
    }

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
