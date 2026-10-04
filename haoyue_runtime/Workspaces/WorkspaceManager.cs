using System.Text.Json;
using System.Text;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Workspaces;

/// <summary>A detected project workspace with its private Haoyue state directories.</summary>
public sealed class WorkspaceInfo
{
    public required string Root { get; init; }
    public required IReadOnlyList<string> ProjectKinds { get; init; }
    public WorkspaceConfig? Config { get; init; }
    public bool IsGlobal { get; init; }

    public string Name => IsGlobal ? "Global" : Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar, '/'));

    public string HaoyueDir => IsGlobal ? Root : Path.Combine(Root, ".haoyue");
    public string PromptsDir => Path.Combine(HaoyueDir, "prompts");
    public string MemoryDir => Path.Combine(HaoyueDir, "memory");
    public string MemoryFile => Path.Combine(MemoryDir, "MEMORY.md");
    public string CacheDir => Path.Combine(HaoyueDir, "cache");
    public string SessionsDir => IsGlobal
        ? Path.Combine(Root, "sessions")
        : Directory.Exists(Path.Combine(Root, ".session")) ? Path.Combine(Root, ".session") : Path.Combine(HaoyueDir, "sessions");
    public string LogsDir => Path.Combine(HaoyueDir, "logs");
    public string SkillsDir => Directory.Exists(Path.Combine(Root, "skills")) ? Path.Combine(Root, "skills") : Path.Combine(HaoyueDir, "skills");
    public string McpDir => Directory.Exists(Path.Combine(Root, "mcp")) ? Path.Combine(Root, "mcp") : Path.Combine(HaoyueDir, "mcp");
    public string DocsDir => Directory.Exists(Path.Combine(Root, "docs")) ? Path.Combine(Root, "docs") : Path.Combine(HaoyueDir, "docs");
}

public interface IWorkspaceManager
{
    /// <summary>Detects the workspace containing <paramref name="startDirectory"/> (walks up to a project marker).</summary>
    WorkspaceInfo Detect(string? startDirectory = null);

    /// <summary>Creates the directory-free context used by daemon clients for global tasks.</summary>
    WorkspaceInfo CreateGlobal(string? stateRoot = null);

    /// <summary>Creates the standard workspace directories and .gitignore entries (haoyue init).</summary>
    IReadOnlyList<string> Bootstrap(WorkspaceInfo workspace, string? templatesRoot = null);

    string? LoadMemory(WorkspaceInfo workspace);

    /// <summary>Loads hierarchical AGENTS.md instructions from the workspace root and the current directory chain.</summary>
    string? LoadAgentInstructions(WorkspaceInfo workspace);
}

public sealed class WorkspaceManager : IWorkspaceManager
{
    private static readonly string[] RootMarkers =
        [".git", ".haoyue", "package.json", "pyproject.toml", "Cargo.toml", "go.mod"];

    private readonly string _haoyueHome;

    public WorkspaceManager() : this(HaoyuePaths.Home)
    {
    }

    /// <summary>Test seam: simulates a different user profile / Haoyue state directory.</summary>
    internal WorkspaceManager(string haoyueHome) => _haoyueHome = haoyueHome;

    public WorkspaceInfo Detect(string? startDirectory = null)
    {
        var start = Path.GetFullPath(startDirectory ?? Directory.GetCurrentDirectory());
        var root = FindRoot(start) ?? start;
        return new WorkspaceInfo
        {
            Root = root,
            ProjectKinds = DetectKinds(root),
            Config = LoadWorkspaceConfig(root),
        };
    }

    public WorkspaceInfo CreateGlobal(string? stateRoot = null) => new()
    {
        Root = Path.GetFullPath(stateRoot ?? HaoyuePaths.Home),
        ProjectKinds = [],
        Config = null,
        IsGlobal = true,
    };

    private string? FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var matched = RootMarkers.Where(marker =>
                    Directory.Exists(Path.Combine(dir.FullName, marker)) ||
                    File.Exists(Path.Combine(dir.FullName, marker)))
                .ToList();
            if (matched.Count > 0 && !IsOnlyHaoyueHomeMarker(dir, matched))
                return dir.FullName;

            if (HasFiles(dir, "*.sln") || HasFiles(dir, "*.slnx"))
                return dir.FullName;
        }
        return null;
    }

    internal bool IsOnlyHaoyueHomeMarker(DirectoryInfo dir, IReadOnlyList<string> matched)
    {
        // ~/.haoyue is the user's own global Haoyue state directory, not a project
        // marker. Without this exclusion every plain folder under the user profile would
        // resolve to the profile as its workspace root, merging unrelated projects into a
        // single shared session scope (and making sessions appear under the wrong project).
        if (matched.Count != 1 || !matched[0].Equals(".haoyue", StringComparison.Ordinal))
            return false;

        var markerPath = Path.GetFullPath(Path.Combine(dir.FullName, ".haoyue"));
        return string.Equals(
            markerPath, _haoyueHome,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static List<string> DetectKinds(string root)
    {
        var kinds = new List<string>();
        void AddIf(bool condition, string kind) { if (condition) kinds.Add(kind); }

        AddIf(Directory.Exists(Path.Combine(root, ".git")), "git");
        AddIf(HasFiles(root, "*.sln")
              || HasFiles(root, "*.slnx")
              || HasFiles(root, "*.csproj")
              || HasChildFiles(root, "*.csproj"), "dotnet");
        AddIf(File.Exists(Path.Combine(root, "package.json")), "node");
        AddIf(File.Exists(Path.Combine(root, "pyproject.toml"))
              || File.Exists(Path.Combine(root, "requirements.txt"))
              || File.Exists(Path.Combine(root, "setup.py")), "python");
        AddIf(File.Exists(Path.Combine(root, "Cargo.toml")), "rust");
        AddIf(File.Exists(Path.Combine(root, "go.mod")), "go");
        AddIf(Directory.Exists(Path.Combine(root, "Assets"))
              && Directory.Exists(Path.Combine(root, "ProjectSettings")), "unity");

        // Vue projects get their own developer prompt on top of node.
        var packageJson = Path.Combine(root, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                if (File.ReadAllText(packageJson).Contains("\"vue\"", StringComparison.OrdinalIgnoreCase))
                    kinds.Add("vue");
            }
            catch (IOException) { }
        }

        return kinds;
    }

    private static bool HasFiles(DirectoryInfo directory, string pattern)
    {
        try { return directory.EnumerateFiles(pattern).Any(); }
        catch (UnauthorizedAccessException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return false; }
    }

    private static bool HasFiles(string directory, string pattern) =>
        HasFiles(new DirectoryInfo(directory), pattern);

    private static bool HasChildFiles(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Any(directory => HasFiles(new DirectoryInfo(directory), pattern));
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return false; }
    }

    private static WorkspaceConfig? LoadWorkspaceConfig(string root)
    {
        var file = Path.Combine(root, ".haoyue", "config.json");
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), HaoyueJsonContext.Default.WorkspaceConfig);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> Bootstrap(WorkspaceInfo workspace, string? templatesRoot = null)
    {
        var created = new List<string>();
        foreach (var dir in new[]
                 {
                     workspace.HaoyueDir, workspace.PromptsDir, workspace.MemoryDir,
                     workspace.CacheDir, workspace.LogsDir,
                     workspace.SkillsDir, workspace.McpDir, workspace.DocsDir,
                 })
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                created.Add(Path.GetRelativePath(workspace.Root, dir));
            }
        }

        // Seed files: AGENTS.md at the root, the workspace config and any custom
        // prompt templates. Global overrides live under ~/.haoyue/templates/
        // workspace/; existing files are never overwritten.
        var agentsMd = Path.Combine(workspace.Root, "AGENTS.md");
        if (!File.Exists(agentsMd))
        {
            File.WriteAllText(agentsMd, WorkspaceTemplates.AgentsMdTemplate(templatesRoot));
            created.Add("AGENTS.md");
        }

        var workspaceConfig = Path.Combine(workspace.HaoyueDir, "config.json");
        if (!File.Exists(workspaceConfig))
        {
            File.WriteAllText(workspaceConfig, WorkspaceTemplates.WorkspaceConfigTemplate(templatesRoot));
            created.Add(".haoyue/config.json");
        }

        foreach (var (relativePath, content) in WorkspaceTemplates.PromptTemplates(templatesRoot))
        {
            var target = Path.Combine(workspace.PromptsDir, relativePath);
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
            created.Add(Path.Combine(".haoyue", "prompts", relativePath));
        }

        var gitignore = Path.Combine(workspace.Root, ".gitignore");
        var required = new[] { ".haoyue/", ".session/", ".cache/", "logs/" };
        var existing = File.Exists(gitignore) ? File.ReadAllLines(gitignore).ToList() : [];
        var missing = required.Where(entry => !existing.Any(line => line.Trim() == entry || line.Trim() == entry.TrimEnd('/'))).ToList();
        if (missing.Count > 0)
        {
            if (existing.Count > 0 && existing[^1].Length > 0) existing.Add("");
            existing.Add("# Haoyue");
            existing.AddRange(missing);
            File.WriteAllLines(gitignore, existing);
            created.Add(".gitignore (updated)");
        }

        return created;
    }

    public string? LoadMemory(WorkspaceInfo workspace) =>
        File.Exists(workspace.MemoryFile) ? File.ReadAllText(workspace.MemoryFile) : null;

    public string? LoadAgentInstructions(WorkspaceInfo workspace)
    {
        if (workspace.IsGlobal) return null;

        var rootPath = Path.GetFullPath(workspace.Root);
        var rootInstructions = Path.Combine(rootPath, "AGENTS.md");
        var files = new List<(string Path, string Text)>();

        if (File.Exists(rootInstructions))
        {
            try
            {
                files.Add((Path.GetRelativePath(rootPath, rootInstructions), File.ReadAllText(rootInstructions)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (cwd.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var current = cwd;
            while (!string.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(current, "AGENTS.md");
                if (File.Exists(candidate) && !string.Equals(candidate, rootInstructions, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var relativePath = Path.GetRelativePath(rootPath, candidate);
                        // Deeper AGENTS.md files take precedence; order them closer to the working directory.
                        files.Insert(0, (relativePath, File.ReadAllText(candidate)));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                var parent = Path.GetDirectoryName(current);
                if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }

        if (files.Count == 0) return null;

        var builder = new StringBuilder();
        foreach (var (path, text) in files)
        {
            builder.Append("## ").Append(path).AppendLine();
            builder.AppendLine(text.Trim());
            builder.AppendLine();
        }
        return builder.ToString();
    }
}
