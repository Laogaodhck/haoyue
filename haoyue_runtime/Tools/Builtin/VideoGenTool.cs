using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// 视频生成（豆包式多模态输出）：走 OpenAI 兼容端点的异步
/// contents/generations/tasks 链路（如火山方舟 Seedance）。
/// 创建任务后轮询到 succeeded 并下载 MP4 到工作区；单次轮询超时
/// 返回任务 ID，可由 <see cref="VideoStatusTool"/> 继续查询/取回。
/// 模型引用 videoGen.model（"providerId/modelId"）必填，未配置时工具隐藏。
/// </summary>
public sealed class VideoGenTool(
    IPromptProvider prompts,
    IConfigStore configStore,
    ILlmHttpFactory http) : BuiltinTool(prompts)
{
    public override string Name => "video_generate";
    public override string StatusLabel => "Generating video";
    public override bool Mutating => true;
    public ToolRiskTier RiskTier => ToolRiskTier.Cost | ToolRiskTier.Network | ToolRiskTier.Write;
    public override bool RequiresNetwork => true;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("prompt", ToolSchema.String(
            "What the video should show: subject, motion, camera movement, style, and any audio hint. " +
            "For image-to-video, describe how the reference image should move."), true),
        ("image_path", ToolSchema.String("Local path of the first-frame reference image (png/jpeg/webp/gif). Mutually exclusive with image_url."), false),
        ("image_url", ToolSchema.String("Public URL of the first-frame reference image. Mutually exclusive with image_path."), false),
        ("ratio", ToolSchema.String("Aspect ratio, e.g. 16:9, 9:16, 1:1, 4:3, or adaptive. Defaults to videoGen.ratio."), false),
        ("duration", ToolSchema.Integer("Video length in seconds (5..30). Defaults to videoGen.duration."), false),
        ("generate_audio", ToolSchema.Boolean("Whether to synthesize an audio track. Defaults to videoGen.generateAudio."), false),
        ("file_name", ToolSchema.String("Optional output file name without extension; a timestamped default is used otherwise."), false),
        ("wait_seconds", ToolSchema.Integer("How long to poll before returning the task id (30..600). Defaults to videoGen.waitSeconds."), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var prompt = GetString(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Fail("prompt is required.");

        var imagePath = GetString(arguments, "image_path");
        var imageUrl = GetString(arguments, "image_url");
        if (imagePath is { Length: > 0 } && imageUrl is { Length: > 0 })
            return ToolResult.Fail("pass either image_path or image_url, not both.");

        var config = configStore.Config;
        var endpoint = ResolveVideoEndpoint(config);
        if (endpoint is not (ProviderConfig provider, string model))
            return ToolResult.Fail(
                "video_generate is not available: no video model is configured. " +
                "Set agent.videoGen.model to 'providerId/modelId' on a provider that exposes a video generation endpoint.");

        if (context.Workspace is null)
            return ToolResult.Fail("video_generate requires a workspace to write the artifact into.");

        string? referenceImage = null;
        if (imagePath is { Length: > 0 })
        {
            try
            {
                referenceImage = await ImageEditTool.ToDataUriAsync(imagePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"video_generate cannot read the reference image: {ex.Message}");
            }
        }
        else if (imageUrl is { Length: > 0 })
        {
            referenceImage = imageUrl;
        }

        var ratio = GetString(arguments, "ratio") ?? config.VideoGen.Ratio;
        var duration = GetInt(arguments, "duration") ?? config.VideoGen.Duration;
        duration = Math.Clamp(duration, 5, 30);
        var generateAudio = arguments["generate_audio"] is null ? config.VideoGen.GenerateAudio : GetBool(arguments, "generate_audio");
        var waitSeconds = GetInt(arguments, "wait_seconds") ?? config.VideoGen.WaitSeconds;
        waitSeconds = Math.Clamp(waitSeconds, 30, 600);
        var pollInterval = Math.Clamp(config.VideoGen.PollIntervalSeconds, 5, 60);

        var client = http.GetClient(provider);
        var content = new JsonArray();
        content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = prompt });
        if (referenceImage is not null)
            content.Add((JsonNode)new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = referenceImage } });

        var requestBody = new JsonObject
        {
            ["model"] = model,
            ["content"] = content,
            ["ratio"] = ratio,
            ["duration"] = duration,
            ["generate_audio"] = generateAudio,
            ["watermark"] = config.VideoGen.Watermark,
        };

        string taskId;
        try
        {
            taskId = await CreateTaskAsync(client, provider, requestBody, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"video_generate failed to create the task: {ex.Message}");
        }

        // 轮询直到 succeeded / failed / 超时。
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        string status = "queued";
        string? videoUrl = null;
        string? error = null;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(pollInterval * 1000, ct).ConfigureAwait(false);
                var (current, url, taskError) = await QueryTaskAsync(client, provider, taskId, ct).ConfigureAwait(false);
                status = current;
                videoUrl = url;
                error = taskError;
                if (status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)) break;
                if (status.Equals("failed", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"video_generate polling failed for task {taskId}: {ex.Message}");
        }

        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail($"Video task {taskId} failed: {error ?? "unknown error"}.");

        if (!status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(videoUrl))
        {
            return ToolResult.Ok(
                $"Video task {taskId} is still {status} after {waitSeconds}s. Use video_status with task_id={taskId} to query later — " +
                "the task keeps running server-side and the video URL expires after ~24h.",
                $"Video task queued: {taskId} ({status})");
        }

        var outputsDir = Path.Combine(context.Workspace.Root, ".haoyue", "outputs", "videos");
        Directory.CreateDirectory(outputsDir);
        var customName = GetString(arguments, "file_name");
        var file = Sanitize(customName) is { Length: > 0 } safeName
            ? Path.Combine(outputsDir, $"{safeName}.mp4")
            : Path.Combine(outputsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.mp4");

        byte[] bytes;
        try
        {
            bytes = await client.GetByteArrayAsync(videoUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Video task {taskId} succeeded but the MP4 download failed: {ex.Message}");
        }
        await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
        return ToolResult.Ok(
            $"Video saved: {file} ({bytes.Length:N0} bytes). Use the read tool or the workspace file view to play it.",
            $"Generated video: {Path.GetFileName(file)}");
    }

    /// <summary>解析视频端点：仅接受显式 videoGen.model 引用（无“视频能力”自动发现）。</summary>
    public static (ProviderConfig Provider, string Model)? ResolveVideoEndpoint(HaoyueConfig config)
    {
        var reference = config.VideoGen.Model;
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var separator = reference.IndexOf('/');
        if (separator <= 0) return null;
        var provider = config.FindProvider(reference[..separator]);
        if (provider is null || !provider.Enabled || provider.IsLocal) return null;
        return (provider, reference[(separator + 1)..]);
    }

    /// <summary>创建内容生成任务，返回任务 ID。也供 video_status 之外的宿主（RPC/测试）复用。</summary>
    public static async Task<string> CreateTaskAsync(
        HttpClient client, ProviderConfig provider, JsonObject requestBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LlmUrl.Join(provider.BaseUrl, "contents/generations/tasks"))
        {
            Content = new StringContent(requestBody.ToJsonString(),
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
        };
        var key = provider.ResolveApiKey();
        if (!string.IsNullOrEmpty(key))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        if (provider.Headers is { } headers)
        {
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"endpoint returned {(int)response.StatusCode}: {Shorten(body)}");
        var payload = JsonNode.Parse(body) ?? throw new InvalidOperationException("response is not JSON");
        var id = payload["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("task response carried no id.");
        return id;
    }

    /// <summary>查询任务状态，返回 (status, videoUrl, error)。</summary>
    public static async Task<(string Status, string? VideoUrl, string? Error)> QueryTaskAsync(
        HttpClient client, ProviderConfig provider, string taskId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, LlmUrl.Join(provider.BaseUrl, $"contents/generations/tasks/{Uri.EscapeDataString(taskId)}"));
        var key = provider.ResolveApiKey();
        if (!string.IsNullOrEmpty(key))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        if (provider.Headers is { } headers)
        {
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"endpoint returned {(int)response.StatusCode}: {Shorten(body)}");
        var payload = JsonNode.Parse(body) ?? throw new InvalidOperationException("response is not JSON");
        var status = payload["status"]?.GetValue<string>() ?? "unknown";
        var videoUrl = payload["content"]?["video_url"]?.GetValue<string>();
        var error = payload["error"]?["message"]?.GetValue<string>()
            ?? (payload["error"] is JsonObject errObj && errObj.Count > 0 ? errObj.ToJsonString() : null);
        return (status, videoUrl, error);
    }

    /// <summary>轮询直到成功/失败/超时，返回最终 (status, videoUrl, error, timedOut)。</summary>
    public static async Task<(string Status, string? VideoUrl, string? Error, bool TimedOut)> PollUntilDoneAsync(
        HttpClient client, ProviderConfig provider, string taskId, int waitSeconds, int pollInterval, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        string status = "queued";
        string? videoUrl = null;
        string? error = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(pollInterval * 1000, ct).ConfigureAwait(false);
            (status, videoUrl, error) = await QueryTaskAsync(client, provider, taskId, ct).ConfigureAwait(false);
            if (status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)) return (status, videoUrl, error, false);
            if (status.Equals("failed", StringComparison.OrdinalIgnoreCase)) return (status, videoUrl, error, false);
        }
        return (status, videoUrl, error, true);
    }

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    internal static string Shorten(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
