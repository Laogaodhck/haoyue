namespace Haoyue.Runtime.Configuration;

/// <summary>Well-known file system locations for global (per-user) state.</summary>
public static class HaoyuePaths
{
    /// <summary>The current user's profile directory (parent of the Haoyue state folder).</summary>
    public static string HomeDir { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Home => Path.Combine(HomeDir, ".haoyue");

    public static string ConfigFile => Path.Combine(Home, "config.json");
    public static string StateFile => Path.Combine(Home, "state.json");
    /// <summary>SQLite database for sessions and Desktop project metadata.</summary>
    public static string DatabaseFile => Path.Combine(Home, "haoyue.db");
    public static string UsageFile => Path.Combine(Home, "usage.jsonl");
    public static string LogsDir => Path.Combine(Home, "logs");
    public static string SkillsDir => Path.Combine(Home, "skills");
    public static string PromptsDir => Path.Combine(Home, "prompts");
    public static string SessionsDir => Path.Combine(Home, "sessions");
    /// <summary>Handshake token the daemon shares with its local clients.</summary>
    public static string DaemonTokenFile => Path.Combine(Home, "daemon.token");
    /// <summary>Crash-marker journal of in-flight agent turns (residue = interrupted by process death).</summary>
    public static string ActiveTurnsFile => Path.Combine(Home, "active-turns.json");

    /// <summary>Directory of the running application (default prompts / seed config ship here).</summary>
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>
    /// True when the path is the user's profile directory or Haoyue's own global state
    /// directory (or something inside it) — neither is ever a valid project root.
    /// </summary>
    public static bool IsForbiddenProjectPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var home = Path.GetFullPath(HomeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var state = Path.GetFullPath(Home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (full.Equals(home, comparison) || full.Equals(state, comparison))
            return true;
        return full.StartsWith(state + Path.DirectorySeparatorChar, comparison);
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(SkillsDir);
    }
}
