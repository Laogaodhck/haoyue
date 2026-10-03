using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Providers;

/// <summary>
/// A local GGUF file enriched with the metadata read from its header, which is what front
/// ends show before registering it and what the registration defaults are derived from.
/// </summary>
public sealed record LocalModelInfo(
    string ModelId,
    string Path,
    long SizeBytes,
    string DisplayName,
    string? Architecture,
    long? TrainedContext,
    int ContextWindow,
    int MaxOutput);

/// <summary>
/// Turns the GGUF files of a local models directory into registrable model entries. The
/// numbers come from the model's own header rather than from a guess, but the registered
/// context window stays below the trained one: a KV cache for a 128k window costs
/// gigabytes of RAM that a machine running the model on CPU may not have. Users raise the
/// value in the model settings when their hardware allows it.
/// </summary>
public static class LocalModelProbe
{
    /// <summary>Context window registered for a scanned model unless its header asks for less.</summary>
    public const int DefaultContextWindow = 32_768;

    /// <summary>Lists the local GGUF files of a directory together with their header metadata.</summary>
    public static IReadOnlyList<LocalModelInfo> Scan(string? configuredDirectory = null)
    {
        var directory = LocalModels.ResolveDirectory(configuredDirectory);
        return LocalModels.ListFiles(directory)
            .Select(file =>
            {
                var header = GgufProbe.Read(file.Path);
                return new LocalModelInfo(
                    ModelId: file.FileName,
                    Path: file.Path,
                    SizeBytes: file.SizeBytes,
                    DisplayName: string.IsNullOrWhiteSpace(header.Name)
                        ? Path.GetFileNameWithoutExtension(file.FileName)
                        : header.Name!.Trim(),
                    Architecture: header.Architecture,
                    TrainedContext: header.ContextLength,
                    ContextWindow: header.ContextLength is > 0 and var trained
                        ? (int)Math.Min(trained, DefaultContextWindow)
                        : 8192,
                    MaxOutput: header.ContextLength is > 0 and var window
                        ? (int)Math.Clamp(window / 8, 2048, 8192)
                        : 2048);
            })
            .ToList();
    }

    /// <summary>Builds the configuration entry a scanned file is registered with.</summary>
    public static ModelConfig ToConfig(LocalModelInfo model) => new()
    {
        Id = model.ModelId,
        Alias = model.DisplayName,
        LocalPath = model.Path,
        ContextWindow = model.ContextWindow,
        MaxOutput = model.MaxOutput,
        Capabilities = new ModelCapabilities
        {
            Streaming = true,
            // Chat templates in the wild disagree on how tool calls should be rendered, so
            // local models are advertised without tool calling until that is verifiable.
            ToolCalling = false,
            Vision = false,
        },
    };
}
