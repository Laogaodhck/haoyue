namespace Haoyue.Runtime.Configuration;

/// <summary>Well-known file system locations for global (per-user) state.</summary>
public static class HaoyuePaths
{
    /// <summary>Override for the state root. Tests point this at a temp directory so a run
    /// never touches the developer's real ~/.haoyue (usage log, sessions, credentials).</summary>
    public const string HomeEnvironmentVariable = "HAOYUE_HOME";

    /// <summary>The current user's profile directory (parent of the Haoyue state folder).</summary>
    public static string HomeDir { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Home { get; } = ResolveHome();

    private static string ResolveHome()
    {
        var overridden = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(HomeDir, ".haoyue")
            : Path.GetFullPath(overridden.Trim());
    }

    public static string ConfigFile => Path.Combine(Home, "config.json");
    public static string StateFile => Path.Combine(Home, "state.json");
    /// <summary>SQLite database for sessions and Desktop project metadata.</summary>
    public static string DatabaseFile => Path.Combine(Home, "haoyue.db");
    public static string UsageFile => Path.Combine(Home, "usage.jsonl");
    public static string LogsDir => Path.Combine(Home, "logs");
    public static string SkillsDir => Path.Combine(Home, "skills");
    /// <summary>Candidate skill drafts produced by the evolution engine. Deliberately NOT
    /// a skill scan root: drafts stay inert until a human adopts them into SkillsDir.</summary>
    public static string SkillsCandidatesDir => Path.Combine(Home, "skills-candidates");
    /// <summary>Scratch space for evolution reflection turns (MRE files and skill drafts).</summary>
    public static string LabsDir => Path.Combine(Home, "labs");
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
        // ~/.haoyue holds the daemon socket, handshake token, sessions and config:
        // owner-only (0700) on Unix so another local account cannot even list it.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(SkillsDir);
    }
}
