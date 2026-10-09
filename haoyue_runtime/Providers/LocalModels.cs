using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Providers;

/// <summary>A GGUF file found in a local models directory.</summary>
public sealed record LocalModelFile(string FileName, string Path, long SizeBytes);

/// <summary>
/// Locates GGUF model files for providers with kind "local". The directory is never
/// hard-coded: an explicit configuration value wins, then the HAOYUE_MODELS_DIR
/// environment variable, then the "models" folder of the repository checkout the running
/// application lives under, and finally ~/.haoyue/models.
/// </summary>
public static class LocalModels
{
    /// <summary>Overrides the default local models directory when set.</summary>
    public const string DirectoryEnvVar = "HAOYUE_MODELS_DIR";

    /// <summary>Resolves the directory holding local GGUF models.</summary>
    public static string ResolveDirectory(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured.Trim());

        var fromEnv = Environment.GetEnvironmentVariable(DirectoryEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv.Trim());

        // Development / portable checkouts keep models next to the repository root.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var models = Path.Combine(dir.FullName, "models");
            if (Directory.Exists(models)
                && (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                    || File.Exists(Path.Combine(dir.FullName, "Haoyue.slnx"))))
                return models;
        }

        return Path.Combine(HaoyuePaths.Home, "models");
    }

    /// <summary>Enumerates the *.gguf files directly inside <paramref name="directory"/>.</summary>
    public static IReadOnlyList<LocalModelFile> ListFiles(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];
        try
        {
            return Directory
                .EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists && info.Length > 0)
                .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
                .Select(info => new LocalModelFile(info.Name, info.FullName, info.Length))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// True for multimodal projector files (mmproj). They carry the vision encoder, not a
    /// chat model — llama.cpp cannot generate text from them, so scans must not register
    /// them and loaders must reject them. The same convention drives ResolveMmprojPath.
    /// </summary>
    public static bool IsProjectorFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Model ids for a provider's GGUF files, which are their file names. This lets the
    /// shared "fetch model list" flow work for local providers as it does for remote ones.
    /// Projector files are excluded: they are picked up as the mmproj of the main models
    /// and would only produce models that fail at load time.
    /// </summary>
    public static IReadOnlyList<string> ScanIds(string? configuredDirectory) =>
        ListFiles(ResolveDirectory(configuredDirectory))
            .Where(file => !IsProjectorFileName(file.FileName))
            .Select(file => file.FileName)
            .ToList();

    /// <summary>
    /// Absolute path of the GGUF file backing a local model entry. Registered entries may
    /// carry an explicit path; otherwise the id is resolved inside the models directory,
    /// with or without the extension.
    /// </summary>
    public static string ResolveModelPath(ProviderConfig provider, ModelConfig model)
    {
        if (!string.IsNullOrWhiteSpace(model.LocalPath))
            return Path.GetFullPath(model.LocalPath.Trim());

        var path = Path.Combine(ResolveDirectory(provider.ModelsDirectory), model.Id);
        if (File.Exists(path) || Path.GetExtension(model.Id).Equals(".gguf", StringComparison.OrdinalIgnoreCase))
            return path;
        return path + ".gguf";
    }

    /// <summary>
    /// Resolves the multimodal projector (mmproj) GGUF for a local model, or null when the
    /// model stays text-only. An explicit <paramref name="model"/>.MmprojPath wins; otherwise
    /// the models directory is scanned and exactly one *mmproj*.gguf file is accepted —
    /// multiple candidates are ambiguous, so none is chosen automatically.
    /// </summary>
    public static string? ResolveMmprojPath(ProviderConfig provider, ModelConfig model)
    {
        if (!string.IsNullOrWhiteSpace(model.MmprojPath))
            return Path.GetFullPath(model.MmprojPath.Trim());

        try
        {
            var candidates = ListFiles(ResolveDirectory(provider.ModelsDirectory))
                .Where(file => file.FileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return candidates.Count == 1 ? candidates[0].Path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
