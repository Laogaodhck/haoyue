using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// 图片编辑（豆包式多模态输出）：走 OpenAI 兼容 images/generations 端点并携带
/// 参考图（本地路径转 data URI，或直接传 URL），实现消除、扩图、风格迁移、
/// 元素替换等编辑。端点解析复用 <see cref="ImageGenTool.ResolveEndpoint"/>：
/// 未配置图像能力模型时工具与 image_generate 一同隐藏。
/// </summary>
public sealed class ImageEditTool(
    IPromptProvider prompts,
    IConfigStore configStore,
    ILlmHttpFactory http) : BuiltinTool(prompts)
{
    public override string Name => "image_edit";
    public override string StatusLabel => "Editing image";
    public override bool Mutating => true;
    /// <summary>按次计费、外发图片与提示词、产物落盘——费用 + 网络 + 写三重复合。</summary>
    public ToolRiskTier RiskTier => ToolRiskTier.Cost | ToolRiskTier.Network | ToolRiskTier.Write;
    public override bool RequiresNetwork => true;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("prompt", ToolSchema.String(
            "What to change in the reference image. Be concrete about the edit region and the desired result, e.g. " +
            "“remove the red car on the left”, “change the dress material to glass”, “extend the sky upward”. " +
            "You may reference the image by 图1 (for image_path) / 图2 (for image_url) when coordinating multiple inputs."), true),
        ("image_path", ToolSchema.String("Local path of the reference image (png/jpeg/webp/gif). Mutually exclusive with image_url."), false),
        ("image_url", ToolSchema.String("Public URL of the reference image. Mutually exclusive with image_path."), false),
        ("file_name", ToolSchema.String("Optional output file name without extension; a timestamped default is used otherwise."), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var prompt = GetString(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Fail("prompt is required.");

        var imagePath = GetString(arguments, "image_path");
        var imageUrl = GetString(arguments, "image_url");
        if (string.IsNullOrWhiteSpace(imagePath) && string.IsNullOrWhiteSpace(imageUrl))
            return ToolResult.Fail("image_edit needs a reference image: pass image_path (local file) or image_url.");

        var config = configStore.Config;
        var endpoint = ImageGenTool.ResolveEndpoint(config);
        if (endpoint is not (ProviderConfig provider, string model))
            return ToolResult.Fail(
                "image_edit is not available: no provider exposes an image-capable model. " +
                "Set model capabilities.image = true for one provider, or configure agent.imageGen.model as 'providerId/modelId'.");

        // 本地文件 → data URI（端点只认 URL 或 data:image/...;base64, 形式）。
        string image;
        try
        {
            image = imagePath is { Length: > 0 }
                ? await ToDataUriAsync(imagePath, ct).ConfigureAwait(false)
                : imageUrl!;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"image_edit cannot read the reference image: {ex.Message}");
        }

        var size = config.ImageGen.Size is { Length: > 0 } configured ? configured : "1024x1024";
        var client = http.GetClient(provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, LlmUrl.Join(provider.BaseUrl, "images/generations"))
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["model"] = model,
                    ["prompt"] = prompt,
                    ["image"] = image,
                    ["n"] = 1,
                    ["size"] = size,
                }.ToJsonString(),
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

        byte[] bytes;
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ToolResult.Fail($"Image endpoint returned {(int)response.StatusCode}: {Shorten(body)}");

            var payload = JsonNode.Parse(body) ?? throw new InvalidOperationException("response is not JSON");
            var item = payload["data"] is JsonArray array && array.Count > 0 ? array[0] : null;
            var b64 = item?["b64_json"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(b64))
            {
                bytes = Convert.FromBase64String(b64);
            }
            else
            {
                var url = item?["url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(url))
                    return ToolResult.Fail("Image response carried neither b64_json nor url.");
                bytes = await client.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Image edit failed: {ex.Message}");
        }

        if (context.Workspace is null)
            return ToolResult.Fail("image_edit requires a workspace to write the artifact into.");

        var outputsDir = PathGuard.ArtifactDirectory(context.Workspace, "images", out var artifactError);
        if (outputsDir is null) return ToolResult.Fail(artifactError!);
        Directory.CreateDirectory(outputsDir);
        var customName = GetString(arguments, "file_name");
        var extension = DetectExtension(bytes);
        var file = Sanitize(customName) is { Length: > 0 } safeName
            ? Path.Combine(outputsDir, $"{safeName}{extension}")
            : Path.Combine(outputsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{extension}");

        await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
        return ToolResult.Ok(
            $"Edited image saved: {file} ({bytes.Length:N0} bytes). Use the read tool with this path to view it.",
            $"Edited image: {Path.GetFileName(file)}");
    }

    /// <summary>本地图片 → data:image/&lt;fmt&gt;;base64,&lt;...&gt; data URI（端点输入约束：格式必须小写）。</summary>
    internal static async Task<string> ToDataUriAsync(string path, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"reference image not found: {full}");
        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        var mime = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private static string DetectExtension(byte[] bytes) => bytes.Length > 3
        ? bytes[0] == 0x89 && bytes[1] == (byte)'P' ? ".png"
            : bytes[0] == 0xFF && bytes[1] == 0xD8 ? ".jpg"
            : ".png"
        : ".png";

    private static string Shorten(string text) => text.Length > 200 ? text[..200] + "…" : text;
}
