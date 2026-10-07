using System.Text.Json;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Workspaces;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Haoyue.Runtime.Skills;

/// <summary>One declared input of a skill (manifest v2). Rendered into the injected prompt appendix.</summary>
public sealed class SkillParameter
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Required { get; set; }
    public string? Default { get; set; }
}

/// <summary>skill.yaml manifest inside a skill directory.</summary>
public sealed class SkillManifest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Version { get; set; }
    /// <summary>Prompt file relative to the skill directory (default prompt.txt).</summary>
    public string Prompt { get; set; } = "prompt.txt";
    /// <summary>
    /// Tool-authority boundary (manifest v2): when non-empty, the agent may only use
    /// these tool names while the skill is injected. Multiple declaring skills union
    /// their lists; a turn with no triggered declaring skill keeps the full toolset.
    /// The kebab-case key <c>allowed-tools</c> is normalized to this property before
    /// deserialization (YamlDotNet applies the naming convention instead of aliases).
    /// </summary>
    public List<string>? AllowedTools { get; set; }
    /// <summary>Declared inputs the model must collect from the conversation before applying the skill.</summary>
    public List<SkillParameter>? Parameters { get; set; }
    /// <summary>
    /// Keyword gate (manifest v2): when non-empty the skill prompt is only injected
    /// for turns whose user message contains one of these keywords; skills without
    /// triggers stay always-on like before.
    /// </summary>
    public List<string>? Triggers { get; set; }
    /// <summary>
    /// N5 stage 1 (manifest key <c>always-listed</c>): whether the skill appears in
    /// the model-visible catalog so it can be pulled mid-turn via declare_skill.
    /// Default true; set false to hide internal/sensitive skills from the catalog.
    /// </summary>
    public bool AlwaysListed { get; set; } = true;
}

public sealed record SkillInfo(SkillManifest Manifest, string Directory, bool Enabled)
{
    public string Name => Manifest.Name;
    public string PromptFile => Path.Combine(Directory, Manifest.Prompt);
}

/// <summary>
/// Tool-authority derived from triggered manifest-v2 skills.
/// <see cref="AllowedTools"/> is null when no triggered skill declares a boundary —
/// the agent then keeps the unrestricted toolset.
/// </summary>
public sealed record SkillToolPolicy(IReadOnlyList<string>? AllowedTools);

public interface ISkillManager
{
    /// <summary>Rescans ~/.haoyue/skills and &lt;workspace&gt;/skills. Directory changes apply without restart.</summary>
    IReadOnlyList<SkillInfo> Discover(WorkspaceInfo workspace);

    void SetEnabled(string skillName, bool enabled);

    /// <summary>
    /// Tool allow-list derived from the skills injected this turn (triggers honored).
    /// Null means unrestricted — no triggered skill declared allowed-tools.
    /// </summary>
    SkillToolPolicy ResolveToolPolicy(WorkspaceInfo workspace, string? userMessage);

    /// <summary>
    /// N5 stage 1: lets the model pull a listed skill mid-turn via declare_skill.
    /// Returns false when no such enabled + always-listed skill exists.
    /// </summary>
    bool DeclareForTurn(WorkspaceInfo workspace, string skillName);

    /// <summary>Clears model-declared skills (called at the start of every turn).</summary>
    void ResetTurnDeclarations();
}

/// <summary>
/// Directory-based skills: each skill is a folder with skill.yaml + prompt.txt.
/// Enabled skills contribute their prompt to the composed system prompt each turn.
/// </summary>
public sealed class SkillManager : ISkillManager
{
#pragma warning disable IL2026, IL3050 // YAML manifest parsing uses reflection; acceptable for plugin metadata
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
#pragma warning restore IL2026, IL3050

    private readonly IConfigStore _configStore;
    private readonly string _globalSkillsDir;
    private readonly IPromptProvider _prompts;
    private WorkspaceInfo? _workspace;

    // N5 stage 1: skills the model pulled via declare_skill this turn. Lives on the
    // manager instance — isolated turn runtimes give every concurrent turn its own
    // SkillManager, and RunTurnAsync resets it at the start for shared managers (CLI).
    private readonly HashSet<string> _turnDeclared = new(StringComparer.OrdinalIgnoreCase);

    public SkillManager(IConfigStore configStore, IPromptRegistry promptRegistry, string? globalSkillsDir = null, IPromptProvider? prompts = null)
    {
        _configStore = configStore;
        _globalSkillsDir = globalSkillsDir ?? HaoyuePaths.SkillsDir;
        _prompts = prompts ?? new FilePromptProvider();
        // One dynamic contribution: enumerates enabled skills at compose time (hot reload for free).
        // Manifest-v2 triggers gate injection on the turn's user message; parameters
        // render as a collect-before-use appendix after the skill prompt.
        promptRegistry.Register(new PromptContribution("skills", PromptSlot.Skill, (ctx, _) =>
        {
            if (_workspace is null || !string.Equals(_workspace.Root, ctx.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult<string?>(null);

            var userMessage = ctx.Variables.TryGetValue("user_message", out var value) ? value : null;
            // N5 stage 1: the catalog tells the model which trigger-gated skills exist
            // so declare_skill can recover keyword misses; injected prompts follow.
            var parts = new List<string>();
            var catalog = RenderSkillCatalog(_workspace);
            if (catalog is not null) parts.Add(catalog);
            parts.AddRange(SelectInjectedSkills(_workspace, userMessage, _turnDeclared)
                .Where(s => File.Exists(s.PromptFile))
                .Select(s => RenderSkillPrompt(s))
                .Where(text => text.Length > 0));
            return ValueTask.FromResult<string?>(parts.Count == 0 ? null : string.Join("\n\n", parts));
        }));
    }

    /// <summary>Binds the manager to the current workspace (called during runtime initialization).</summary>
    public void Attach(WorkspaceInfo workspace) => _workspace = workspace;

    public IReadOnlyList<SkillInfo> Discover(WorkspaceInfo workspace)
    {
        var disabledGlobal = _configStore.State.DisabledSkills;
        var disabledWorkspace = workspace.Config?.DisabledSkills ?? [];
        var skills = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in new[] { _globalSkillsDir, workspace.SkillsDir })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var manifest = LoadManifest(dir);
                if (manifest is null) continue;
                var enabled = !disabledGlobal.ContainsKey(manifest.Name)
                              && !disabledWorkspace.Contains(manifest.Name, StringComparer.OrdinalIgnoreCase);
                // Workspace skills override same-named global skills.
                skills[manifest.Name] = new SkillInfo(manifest, dir, enabled);
            }
        }

        return skills.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Single source of truth for what is injected this turn: enabled skills, gated
    /// by manifest triggers against the user message (skills without triggers stay
    /// always-on) plus anything the model pulled via declare_skill this turn. Both
    /// the prompt contribution and the tool policy derive from this, so injection
    /// and tool authority can never drift apart.
    /// </summary>
    internal IReadOnlyList<SkillInfo> SelectInjectedSkills(
        WorkspaceInfo workspace, string? userMessage, IReadOnlyCollection<string>? modelDeclared = null)
    {
        return Discover(workspace)
            .Where(s => s.Enabled
                        && (Triggered(s.Manifest, userMessage)
                            || (modelDeclared?.Contains(s.Name) ?? false)))
            .ToList();
    }

    /// <summary>Null triggers are always-on; otherwise any keyword hit in the user message injects.</summary>
    internal static bool Triggered(SkillManifest manifest, string? userMessage)
    {
        var triggers = manifest.Triggers;
        if (triggers is not { Count: > 0 }) return true;
        if (string.IsNullOrWhiteSpace(userMessage)) return false;
        return triggers.Any(keyword =>
            !string.IsNullOrWhiteSpace(keyword)
            && MatchesKeyword(userMessage, keyword.Trim()));
    }

    /// <summary>
    /// Pure-ASCII keywords match on word boundaries so "git" never hits "digit" or
    /// "gitignore" (误调用防护); keywords containing non-ASCII characters (中文等 CJK
    /// 无词边界概念) keep plain case-insensitive substring matching.
    /// </summary>
    internal static bool MatchesKeyword(string text, string keyword)
    {
        if (IsAsciiWord(keyword))
            return WordBoundaryRegexes.GetOrAdd(
                keyword,
                static key => new Regex($@"\b{Regex.Escape(key)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled))
                .IsMatch(text);
        return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAsciiWord(string keyword) =>
        keyword.Length > 0
        && keyword.All(ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-');

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> WordBoundaryRegexes = new(StringComparer.Ordinal);

    public SkillToolPolicy ResolveToolPolicy(WorkspaceInfo workspace, string? userMessage)
    {
        // Single selection pass shared with injection semantics: one Discover scan,
        // and the allow-list can never drift from what gets injected this turn.
        var union = SelectInjectedSkills(workspace, userMessage, _turnDeclared)
            .SelectMany(s => s.Manifest.AllowedTools ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SkillToolPolicy(union.Count == 0 ? null : union);
    }

    /// <inheritdoc />
    public bool DeclareForTurn(WorkspaceInfo workspace, string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName)) return false;
        var match = Discover(workspace).FirstOrDefault(s =>
            s.Enabled
            && s.Manifest.AlwaysListed
            && string.Equals(s.Name, skillName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        _turnDeclared.Add(match.Name);
        return true;
    }

    /// <inheritdoc />
    public void ResetTurnDeclarations() => _turnDeclared.Clear();

    /// <summary>
    /// N5 stage 1: a model-visible catalog of trigger-gated skills so the agent can
    /// declare (declare_skill) skills that keyword matching missed. Always-on skills
    /// need no declaration and internal skills (<c>always-listed: false</c>) stay hidden.
    /// </summary>
    internal string? RenderSkillCatalog(WorkspaceInfo workspace)
    {
        var candidates = Discover(workspace)
            .Where(s => s.Enabled && s.Manifest.AlwaysListed && s.Manifest.Triggers is { Count: > 0 })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.Append("可用技能目录（如用户任务匹配某技能的适用场景、但该技能没有被自动注入，可调用 declare_skill 工具并传入技能名显式加载；已注入的技能无需声明）：");
        foreach (var skill in candidates)
        {
            sb.Append("\n- ").Append(skill.Name);
            if (!string.IsNullOrWhiteSpace(skill.Manifest.Description))
                sb.Append(" — 适用场景：").Append(skill.Manifest.Description.Trim());
            if (skill.Manifest.Triggers is { Count: > 0 } triggers)
            {
                var keywords = triggers
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .ToList();
                if (keywords.Count > 0) sb.Append("；触发词：").Append(string.Join("/", keywords));
            }
        }
        return ContextPlanner.FitInjectedText(sb.ToString());
    }

    /// <summary>
    /// Applicability header (description + declared triggers, so the model can judge
    /// when the skill applies), prompt body (size-capped), and a collect-before-use
    /// appendix for declared parameters.
    /// </summary>
    internal string RenderSkillPrompt(SkillInfo skill)
    {
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(skill.Manifest.Description))
            meta.Add($"适用场景：{skill.Manifest.Description.Trim()}");
        if (skill.Manifest.Triggers is { Count: > 0 } triggers)
            meta.Add($"触发条件：用户消息命中 {string.Join(" / ", triggers.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()))} 时自动注入");
        var head = meta.Count > 0 ? string.Join("；", meta) : "";

        // Imported skill bodies are third-party text entering the system prompt: fit the
        // payload to the injected-fragment budget, then wrap it in a trust boundary so
        // the model treats it as data rather than instructions.
        var body = ContextPlanner.WrapUntrustedSource(
            ContextPlanner.FitInjectedText(File.ReadAllText(skill.PromptFile).Trim()),
            $"skill '{skill.Manifest.Name}'");
        var parameters = skill.Manifest.Parameters;
        if (parameters is not { Count: > 0 })
            return string.Join("\n\n", new[] { head, body }.Where(part => part.Length > 0)).Trim();

        var appendix = _prompts.TryGet("builtin/skill-parameters") ?? "";
        var lines = parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p =>
            {
                var description = string.IsNullOrWhiteSpace(p.Description) ? "" : $" — {p.Description.Trim()}";
                var defaultValue = string.IsNullOrWhiteSpace(p.Default) ? "" : $"（默认 {p.Default.Trim()}）";
                var marker = p.Required ? "必填" : "可选";
                return $"- {{{{{p.Name.Trim()}}}}}（{marker}{defaultValue}）{description}";
            });
        return string.Join("\n\n", new[] { head, body, appendix, string.Join("\n", lines) }.Where(part => part.Length > 0)).Trim();
    }

    public void SetEnabled(string skillName, bool enabled)
    {        if (enabled)
        {
            _configStore.State.DisabledSkills.Remove(skillName);
            if (_workspace?.Config?.DisabledSkills is { } disabled
                && disabled.RemoveAll(name => name.Equals(skillName, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Directory.CreateDirectory(_workspace.HaoyueDir);
                File.WriteAllText(
                    Path.Combine(_workspace.HaoyueDir, "config.json"),
                    JsonSerializer.Serialize(_workspace.Config, HaoyueJsonContext.Default.WorkspaceConfig));
            }
        }
        else
            _configStore.State.DisabledSkills[skillName] = "";
        _configStore.SaveState();
    }

    /// <summary>
    /// Imports one <c>.md</c> or <c>.zip</c> file into the global skills directory.
    /// Markdown files become bare prompt-only skills. ZIP files may contain either one
    /// skill directory at the archive root or one directory per skill; a skill directory
    /// is recognized by <c>skill.yaml</c>/<c>skill.yml</c>/<c>prompt.txt</c>.
    /// </summary>
    public IReadOnlyList<SkillInfo> ImportGlobal(string path, WorkspaceInfo workspace)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Skill import file not found.", fullPath);

        Directory.CreateDirectory(_globalSkillsDir);
        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            ImportMarkdown(fullPath);
        else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            ImportZip(fullPath);
        else
            throw new InvalidDataException("Only .md and .zip skill files can be imported.");

        return Discover(workspace);
    }

    /// <summary>
    /// Installs an official catalog skill into the global skills directory and enables it.
    /// An existing skill directory is kept untouched so local edits survive reinstalls.
    /// </summary>
    public IReadOnlyList<SkillInfo> InstallOfficial(WorkspaceInfo workspace, OfficialSkillCatalog.OfficialSkill entry)
    {
        var directory = Path.Combine(_globalSkillsDir, entry.Slug);
        if (Directory.Exists(directory))
        {
            if (!LooksLikeSkillDirectory(directory))
                throw new InvalidDataException($"A directory already occupies the skill path: {directory}");
        }
        else
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "skill.yaml"),
                $"name: \"{EscapeYaml(entry.Name)}\"\n"
                + $"description: \"{EscapeYaml(entry.Description)}\"\n"
                + $"version: \"{EscapeYaml(entry.Version)}\"\n");
            File.WriteAllText(Path.Combine(directory, "prompt.txt"), entry.Prompt);
        }

        SetEnabled(entry.Name, true);
        return Discover(workspace);
    }

    private void ImportMarkdown(string markdownFile)
    {
        var name = Path.GetFileNameWithoutExtension(markdownFile);
        var directory = NewSkillDirectory(SanitizeSkillName(name));
        Directory.CreateDirectory(directory);
        File.Copy(markdownFile, Path.Combine(directory, "prompt.txt"), overwrite: false);
        WriteManifest(directory, name, $"Imported from {Path.GetFileName(markdownFile)}");
    }

    private void ImportZip(string zipFile)
    {
        var extractionRoot = Path.Combine(
            Path.GetTempPath(), "haoyue-skill-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionRoot);

        try
        {
            ZipFile.ExtractToDirectory(zipFile, extractionRoot);
            var imported = false;

            if (LooksLikeSkillDirectory(extractionRoot))
            {
                CopySkillDirectory(extractionRoot, Path.GetFileNameWithoutExtension(zipFile));
                imported = true;
            }
            else
            {
                foreach (var directory in Directory.EnumerateDirectories(extractionRoot))
                {
                    if (!LooksLikeSkillDirectory(directory)) continue;
                    CopySkillDirectory(directory, Path.GetFileName(directory));
                    imported = true;
                }

                foreach (var markdown in Directory.EnumerateFiles(extractionRoot, "*.md", SearchOption.TopDirectoryOnly))
                {
                    ImportMarkdown(markdown);
                    imported = true;
                }
            }

            if (!imported)
                throw new InvalidDataException("ZIP did not contain any skill.yaml/prompt.txt skill folders or markdown files.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractionRoot))
                    Directory.Delete(extractionRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void CopySkillDirectory(string source, string fallbackName)
    {
        var manifest = LoadManifest(source);
        var desiredName = !string.IsNullOrWhiteSpace(manifest?.Name)
            ? manifest.Name
            : fallbackName;
        var destination = NewSkillDirectory(SanitizeSkillName(desiredName));
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private string NewSkillDirectory(string name)
    {
        var candidate = Path.Combine(_globalSkillsDir, name);
        if (Directory.Exists(candidate) || File.Exists(candidate))
            throw new InvalidDataException($"A global skill named '{name}' already exists.");
        return candidate;
    }

    private static string SanitizeSkillName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "imported-skill" : sanitized;
    }

    private static bool LooksLikeSkillDirectory(string directory) =>
        File.Exists(Path.Combine(directory, "skill.yaml"))
        || File.Exists(Path.Combine(directory, "skill.yml"))
        || File.Exists(Path.Combine(directory, "prompt.txt"));

    private static void WriteManifest(string directory, string name, string description)
    {
        File.WriteAllText(
            Path.Combine(directory, "skill.yaml"),
            $"name: \"{EscapeYaml(name)}\"\ndescription: \"{EscapeYaml(description)}\"\n");
    }

    private static string EscapeYaml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static SkillManifest? LoadManifest(string dir)
    {
        var file = new[] { "skill.yaml", "skill.yml" }
            .Select(name => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

        try
        {
            SkillManifest? manifest = null;
            if (file is not null)
                manifest = Yaml.Deserialize<SkillManifest>(NormalizeKebabKeys(File.ReadAllText(file)));
            else if (File.Exists(Path.Combine(dir, "prompt.txt")))
                manifest = new SkillManifest(); // bare skill: folder with just a prompt

            if (manifest is null) return null;
            if (string.IsNullOrWhiteSpace(manifest.Name))
                manifest.Name = Path.GetFileName(dir);
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    /// <summary>
    /// YamlDotNet applies the naming convention instead of YamlMember aliases, so the
    /// common kebab-case keys are normalized to their camelCase properties before
    /// binding. Anchored to line starts (indentation allowed) to avoid rewriting text.
    /// </summary>
    private static string NormalizeKebabKeys(string yaml) => KebabKeys.Replace(
        yaml,
        static match => match.Groups[2].Value switch
        {
            "allowed-tools" => match.Groups[1].Value + "allowedTools:",
            "always-listed" => match.Groups[1].Value + "alwaysListed:",
            _ => match.Value,
        });

    private static readonly System.Text.RegularExpressions.Regex KebabKeys =
        new(@"^(\s*)(allowed-tools|always-listed)\s*:", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);
}
