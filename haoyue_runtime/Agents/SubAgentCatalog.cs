using System.Text;
using System.Text.RegularExpressions;
using Haoyue.Runtime.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Haoyue.Runtime.Agents;

/// <summary>Where one sub-agent definition came from.</summary>
public enum SubAgentScope
{
    Builtin,
    User,
    Project,
}

/// <summary>Editable payload of one sub-agent Markdown file.</summary>
public sealed record SubAgentDraft(
    string Name,
    string? Description,
    string? Model,
    IReadOnlyList<string>? Tools,
    string Prompt);

/// <summary>One discovered sub-agent plus its origin.</summary>
public sealed record SubAgentEntry(AgentRole Role, SubAgentScope Scope, string? Path);

/// <summary>
/// 文件驱动的子智能体目录。用户级定义放在 <c>~/.haoyue/agents/*.md</c>，项目级放在
/// <c>&lt;workspace&gt;/.haoyue/agents/*.md</c>；每个文件是一份 Markdown，frontmatter
/// 声明 <c>name / description / model / tools</c>，正文即工作指令。
///
/// 优先级：项目级 &gt; 用户级 &gt; 内置预设（同名覆盖）。目录在每次解析时重新读盘，
/// 因此编辑 Markdown 后下一次委派立即生效，无需重启运行时。
/// </summary>
public sealed class SubAgentCatalog(string? userAgentsDir = null)
{
#pragma warning disable IL2026, IL3050 // YAML frontmatter parsing uses reflection; acceptable for user-authored metadata
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
#pragma warning restore IL2026, IL3050

    private static readonly Regex FrontMatterPattern = new(
        @"\A\ufeff?---[ \t]*\r?\n(?<yaml>[\s\S]*?)\r?\n---[ \t]*(?:\r?\n|\z)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string UserAgentsDir { get; } = userAgentsDir ?? HaoyuePaths.AgentsDir;

    public static string ProjectAgentsDir(string projectRoot) =>
        Path.Combine(projectRoot, ".haoyue", "agents");

    /// <summary>Merged view: built-ins first (unless overridden), then custom agents by name.</summary>
    public IReadOnlyList<SubAgentEntry> Discover(string? projectRoot)
    {
        var custom = new Dictionary<string, SubAgentEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (directory, scope) in ScanRoots(projectRoot))
            foreach (var entry in LoadDir(directory, scope))
                custom[entry.Role.Id] = entry;

        var result = new List<SubAgentEntry>();
        foreach (var builtin in AgentRole.Builtins.Values)
            if (!custom.ContainsKey(builtin.Id))
                result.Add(new SubAgentEntry(builtin, SubAgentScope.Builtin, null));
        result.AddRange(custom.Values.OrderBy(entry => entry.Role.Name, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Resolves by id/name (case-insensitive); null when unknown.</summary>
    public AgentRole? Resolve(string? projectRoot, string role)
    {
        var name = role.Trim();
        if (name.Length == 0) return null;
        return Discover(projectRoot)
            .FirstOrDefault(entry => entry.Role.Id.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Role;
    }

    /// <summary>Names for tool schema hints and error messages.</summary>
    public IReadOnlyList<string> Names(string? projectRoot) =>
        Discover(projectRoot).Select(entry => entry.Role.Id).ToList();

    /// <summary>
    /// Writes one definition. <paramref name="previousName"/> lets a rename move the
    /// file instead of leaving a stale duplicate behind.
    /// </summary>
    public string Save(string? projectRoot, SubAgentScope scope, SubAgentDraft draft, string? previousName = null)
    {
        var name = draft.Name.Trim();
        if (name.Length == 0)
            throw new ArgumentException("子智能体名称不能为空。");
        if (name.Length > 64)
            throw new ArgumentException("子智能体名称不能超过 64 个字符。");
        if (name.Any(char.IsControl))
            throw new ArgumentException("子智能体名称不能包含换行或控制字符。");
        if (string.IsNullOrWhiteSpace(draft.Prompt))
            throw new ArgumentException("子智能体指令不能为空。");

        var directory = DirectoryFor(scope, projectRoot);
        Directory.CreateDirectory(directory);
        var target = FindFile(directory, name) ?? Path.Combine(directory, FileName(name));
        File.WriteAllText(target, Render(draft));

        if (!string.IsNullOrWhiteSpace(previousName)
            && !previousName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            var previous = FindFile(directory, previousName);
            if (previous is not null
                && !Path.GetFullPath(previous).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Delete(previous);
        }

        return target;
    }

    /// <summary>Deletes a user/project definition; built-ins are read-only. False when absent.</summary>
    public bool Delete(string? projectRoot, SubAgentScope scope, string name)
    {
        var directory = DirectoryFor(scope, projectRoot);
        var file = FindFile(directory, name);
        if (file is null) return false;
        File.Delete(file);
        return true;
    }

    /// <summary>Serializes one definition back to its Markdown file format.</summary>
    public static string Render(SubAgentDraft draft)
    {
        var builder = new StringBuilder("---\n");
        builder.Append("name: \"").Append(EscapeYaml(draft.Name.Trim())).Append("\"\n");
        if (!string.IsNullOrWhiteSpace(draft.Description))
            builder.Append("description: \"").Append(EscapeYaml(draft.Description.Trim())).Append("\"\n");
        if (!string.IsNullOrWhiteSpace(draft.Model))
            builder.Append("model: \"").Append(EscapeYaml(draft.Model.Trim())).Append("\"\n");
        var tools = draft.Tools?
            .Where(tool => !string.IsNullOrWhiteSpace(tool))
            .Select(tool => tool.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tools is { Count: > 0 })
        {
            builder.Append("tools:\n");
            foreach (var tool in tools)
                builder.Append("  - \"").Append(EscapeYaml(tool)).Append("\"\n");
        }
        builder.Append("---\n\n").Append(draft.Prompt.Trim()).Append('\n');
        return builder.ToString();
    }

    private IEnumerable<(string Directory, SubAgentScope Scope)> ScanRoots(string? projectRoot)
    {
        yield return (UserAgentsDir, SubAgentScope.User);
        if (!string.IsNullOrWhiteSpace(projectRoot))
            yield return (ProjectAgentsDir(projectRoot), SubAgentScope.Project);
    }

    private static IEnumerable<SubAgentEntry> LoadDir(string directory, SubAgentScope scope)
    {
        if (!Directory.Exists(directory)) yield break;
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
            if (TryLoad(file, scope) is { } entry)
                yield return entry;
    }

    private static SubAgentEntry? TryLoad(string path, SubAgentScope scope)
    {
        try
        {
            var text = File.ReadAllText(path);
            var (frontMatter, body) = SplitFrontMatter(text);
            FrontMatter? meta = null;
            if (frontMatter is not null)
                meta = Yaml.Deserialize<FrontMatter>(frontMatter);

            var fallbackName = Path.GetFileNameWithoutExtension(path);
            var name = string.IsNullOrWhiteSpace(meta?.Name) ? fallbackName : meta!.Name!.Trim();
            if (string.IsNullOrWhiteSpace(name)) return null;

            var description = string.IsNullOrWhiteSpace(meta?.Description) ? null : meta!.Description!.Trim();
            var model = string.IsNullOrWhiteSpace(meta?.Model) ? null : meta!.Model!.Trim();
            var role = new AgentRole(
                name,
                name,
                description ?? "自定义子智能体",
                body.Trim(),
                FlattenTools(meta?.Tools),
                model);
            return new SubAgentEntry(role, scope, Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    private static string? FindFile(string directory, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Directory.Exists(directory)) return null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
                if (TryLoad(file, SubAgentScope.User) is { } entry
                    && entry.Role.Id.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                    return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private string DirectoryFor(SubAgentScope scope, string? projectRoot) => scope switch
    {
        SubAgentScope.User => UserAgentsDir,
        SubAgentScope.Project when !string.IsNullOrWhiteSpace(projectRoot) => ProjectAgentsDir(projectRoot),
        SubAgentScope.Project => throw new InvalidOperationException(
            "项目级子智能体需要工作区；全局会话请保存为用户级。"),
        _ => throw new InvalidOperationException("内置子智能体只读，请新建同名自定义条目来覆盖。"),
    };

    /// <summary>Filename slug; the authoritative name still lives in the frontmatter.</summary>
    private static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var slug = new string(name.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '-' : ch).ToArray());
        slug = slug.Trim().Trim('.');
        if (slug.Length == 0) slug = "subagent";
        if (slug.Length > 80) slug = slug[..80].TrimEnd('.');
        return slug + ".md";
    }

    private static (string? FrontMatter, string Body) SplitFrontMatter(string text)
    {
        var match = FrontMatterPattern.Match(text);
        return match.Success
            ? (match.Groups["yaml"].Value, text[match.Length..])
            : (null, text);
    }

    private static IReadOnlyList<string>? FlattenTools(object? value)
    {
        IEnumerable<string> items = value switch
        {
            null => [],
            string text => text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            IEnumerable<object> sequence => sequence.OfType<string>(),
            _ => [],
        };
        var tools = items
            .Select(item => item.Trim().Trim('"', '\''))
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return tools.Count == 0 ? null : tools;
    }

    private static string EscapeYaml(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", " ")
        .Replace("\n", " ");

    private sealed class FrontMatter
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Model { get; set; }
        public object? Tools { get; set; }
    }
}
