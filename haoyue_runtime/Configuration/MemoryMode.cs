namespace Haoyue.Runtime.Configuration;

/// <summary>
/// How the agent is allowed to maintain MEMORY.md. Auto (default) lets the agent
/// update the memory file as it works; manual keeps MEMORY.md read-only for the
/// agent so the user maintains it through the desktop editor.
/// </summary>
public static class MemoryMode
{
    public const string Auto = "auto";
    public const string Manual = "manual";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Manual => Manual,
        _ => Auto,
    };
}
