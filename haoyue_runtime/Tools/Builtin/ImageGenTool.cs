using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;

namespace Haoyue.Runtime.Tools.Builtin;

/// <summary>
/// Multimodal output (G7): generates an image through an OpenAI-compatible
/// images/generations endpoint and writes the PNG/JPEG artifact under the
/// workspace. When no image-capable provider is configured the tool is hidden
/// from the model's tool view (see Agent.Prompting) instead of failing at runtime.
/// </summary>
public sealed class ImageGenTool(
    IPromptProvider prompts,
    IConfigStore configStore,
    ILlmHttpFactory http) : BuiltinTool(prompts)
{
    public override string Name => "image_generate";
    public override string StatusLabel => "Generating image";
    public override bool Mutating => true;
    public override bool RequiresNetwork => true;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("prompt", ToolSchema.String("What the image should show. Be concrete about subject, style and composition."), true),
        ("file_name", ToolSchema.String("Optional file name without extension; a timestamped default is used otherwise."), false));

    /// <summary>Resolves (provider, model) for image generation: explicit config.ImageGen.Model
    /// reference first, then the first enabled HTTP provider exposing an image-capable model.
    /// Null when the tool should stay hidden.</summary>
    public static (ProviderConfig Provider, string Model)? ResolveEndpoint(HaoyueConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ImageGen.Model))
        {
            var separator = config.ImageGen.Model.IndexOf('/');
            if (separator > 0
                && config.FindProvider(config.ImageGen.Model[..separator]) is { } explicitProvider
                && explicitProvider.Enabled && !explicitProvider.IsLocal)
                return (explicitProvider, config.ImageGen.Model[(separator + 1)..]);
            return null;
        }

        foreach (var provider in config.Providers.Where(p => p.Enabled && !p.IsLocal))
        {
            var model = provider.Models.FirstOrDefault(m => m.Capabilities.Image);
            if (model is not null)
                return (provider, model.Id);
        }
        return null;
    }

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var prompt = GetString(arguments, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Fail("prompt is required.");

        var config = configStore.Config;
        var endpoint = ResolveEndpoint(config);
        if (endpoint is not (ProviderConfig provider, string model))
            return ToolResult.Fail(
                "image_generate is not available: no provider exposes an image-capable model. " +
                "Set model capabilities.image = true for one provider, or configure agent.imageGen.model as 'providerId/modelId'.");

        var size = config.ImageGen.Size is { Length: > 0 } configured ? configured : "1024x1024";
        var client = http.GetClient(provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, LlmUrl.Join(provider.BaseUrl, "images/generations"))
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["model"] = model,
                    ["prompt"] = prompt,
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
            return ToolResult.Fail($"Image generation failed: {ex.Message}");
        }

        if (context.Workspace is null)
            return ToolResult.Fail("image_generate requires a workspace to write the artifact into.");

        var outputsDir = Path.Combine(context.Workspace.Root, ".haoyue", "outputs", "images");
        Directory.CreateDirectory(outputsDir);
        var customName = GetString(arguments, "file_name");
        var extension = DetectExtension(bytes);
        var file = Sanitize(customName) is { Length: > 0 } safeName
            ? Path.Combine(outputsDir, $"{safeName}{extension}")
            : Path.Combine(outputsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{extension}");

        await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
        return ToolResult.Ok(
            $"Image saved: {file} ({bytes.Length:N0} bytes). Use the read tool with this path to view it.",
            $"Generated image: {Path.GetFileName(file)}");
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
