using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Providers;

namespace Haoyue.Tests;

/// <summary>
/// 本地模型多模态接入的纯逻辑测试：mmproj 解析与自动发现、图片按序收集、
/// 提示词图片标记注入。原生 mtmd 推理本身需要真实模型，由手动 visiontest 覆盖。
/// </summary>
public sealed class LocalMultimodalTests
{
    private static ProviderConfig Provider(string dir) => new()
    {
        Id = "local-test",
        Kind = "local",
        ModelsDirectory = dir,
    };

    private static string MakeGguf(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [0x47, 0x47, 0x55, 0x46, 0, 0, 0, 0]); // "GGUF" + stub
        return path;
    }

    [Fact]
    public void ExplicitMmprojPathWins()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"haoyue-mm-{Guid.NewGuid():N}");
        try
        {
            var auto = MakeGguf(dir, "mmproj-a.gguf");
            var explicitPath = Path.Combine(dir, "custom", "encoder.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(explicitPath)!);
            File.WriteAllBytes(explicitPath, [1, 2, 3]);

            var resolved = LocalModels.ResolveMmprojPath(
                Provider(dir), new ModelConfig { Id = "model.gguf", MmprojPath = explicitPath });

            Assert.Equal(Path.GetFullPath(explicitPath), resolved);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UniqueMmprojInModelsDirectoryIsAutoDiscovered()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"haoyue-mm-{Guid.NewGuid():N}");
        try
        {
            MakeGguf(dir, "model-Q4_K_M.gguf");
            var mmproj = MakeGguf(dir, "mmproj-model-f16.gguf");

            var resolved = LocalModels.ResolveMmprojPath(
                Provider(dir), new ModelConfig { Id = "model-Q4_K_M.gguf" });

            Assert.Equal(mmproj, resolved);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AmbiguousMmprojFilesResolveToNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"haoyue-mm-{Guid.NewGuid():N}");
        try
        {
            MakeGguf(dir, "model.gguf");
            MakeGguf(dir, "mmproj-a.gguf");
            MakeGguf(dir, "mmproj-b.gguf");

            var resolved = LocalModels.ResolveMmprojPath(
                Provider(dir), new ModelConfig { Id = "model.gguf" });

            Assert.Null(resolved);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoMmprojFileResolvesToNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"haoyue-mm-{Guid.NewGuid():N}");
        try
        {
            MakeGguf(dir, "model.gguf");

            var resolved = LocalModels.ResolveMmprojPath(
                Provider(dir), new ModelConfig { Id = "model.gguf" });

            Assert.Null(resolved);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CollectImagesPreservesConversationOrder()
    {
        var first = new ChatImageAttachment("a", "a.png", "image/png", "AAAA", 4);
        var second = new ChatImageAttachment("b", "b.png", "image/png", "BBBB", 4);
        var third = new ChatImageAttachment("c", "c.png", "image/png", "CCCC", 4);
        var request = new LlmRequest
        {
            Provider = Provider(Path.GetTempPath()),
            Model = new ModelConfig { Id = "m" },
            Messages =
            [
                ChatMessage.User("看这张", [first]),
                ChatMessage.Assistant("好"),
                ChatMessage.ToolResult("t1", "computer", "截图已附加", true, images: [second]),
                ChatMessage.User("再看这张", [third]),
            ],
        };

        var collected = LocalLlmClient.CollectImages(request);

        Assert.Equal([first, second, third], collected);
    }

    [Fact]
    public void GemmaPromptPlacesMarkersInsideTheCarryingTurn()
    {
        var image = new ChatImageAttachment("a", "shot.png", "image/png", "AAAA", 4);
        var prompt = LocalLlmClient.RenderGemmaTurnPrompt(
        [
            ChatMessage.User("这张图里有什么？", [image]),
        ], system: null, enableThinking: false, imageMarker: "<__image__>");

        Assert.Equal(
            "<|turn>user\n这张图里有什么？<__image__><turn|>\n" +
            "<|turn>model\n",
            prompt);
    }

    [Fact]
    public void GemmaPromptWithoutMarkerIsUnchangedForImageMessages()
    {
        var image = new ChatImageAttachment("a", "shot.png", "image/png", "AAAA", 4);
        var prompt = LocalLlmClient.RenderGemmaTurnPrompt(
            [ChatMessage.User("这张图里有什么？", [image])],
            system: null, enableThinking: false, imageMarker: null);

        Assert.Equal("<|turn>user\n这张图里有什么？<turn|>\n<|turn>model\n", prompt);
    }

    [Fact]
    public void ToolResultReplayCarriesItsOwnMarkers()
    {
        var image = new ChatImageAttachment("a", "shot.png", "image/png", "AAAA", 4);
        var prompt = LocalLlmClient.RenderGemmaTurnPrompt(
        [
            ChatMessage.ToolResult("t1", "computer", "动作完成", true, images: [image, image]),
        ], system: null, enableThinking: false, imageMarker: "<__image__>");

        Assert.Contains("[computer result]\n动作完成<__image__><__image__>", prompt);
    }
}
