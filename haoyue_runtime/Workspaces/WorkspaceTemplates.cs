using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Workspaces;

/// <summary>
/// Seed files stamped into a workspace on init. Teams can customize every
/// template globally by placing same-named files under
/// <c>~/.haoyue/templates/workspace/</c>; missing entries fall back to the
/// built-in defaults. Existing files in the workspace are never overwritten.
/// </summary>
public static class WorkspaceTemplates
{
    /// <summary>Global template directory (~/.haoyue/templates/workspace).</summary>
    public static string DefaultTemplatesDir => Path.Combine(HaoyuePaths.Home, "templates", "workspace");

    /// <summary>Root AGENTS.md seed (user template wins when present).</summary>
    public static string AgentsMdTemplate(string? templatesRoot = null)
    {
        var path = Path.Combine(templatesRoot ?? DefaultTemplatesDir, "AGENTS.md");
        return File.Exists(path) ? File.ReadAllText(path) : DefaultAgentsMd;
    }

    /// <summary>.haoyue/config.json seed (user template wins when present).</summary>
    public static string WorkspaceConfigTemplate(string? templatesRoot = null)
    {
        var path = Path.Combine(templatesRoot ?? DefaultTemplatesDir, "config.json");
        return File.Exists(path) ? File.ReadAllText(path) : DefaultWorkspaceConfig;
    }

    /// <summary>
    /// Custom prompt seeds from <c>&lt;templatesRoot&gt;/prompts/**</c>, returned as
    /// (workspace-relative target path, content) pairs. Empty when the user has
    /// no prompt templates — the built-in prompt library needs no seeding.
    /// </summary>
    public static List<(string RelativePath, string Content)> PromptTemplates(string? templatesRoot = null)
    {
        var root = Path.Combine(templatesRoot ?? DefaultTemplatesDir, "prompts");
        if (!Directory.Exists(root)) return [];

        var templates = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                templates.Add((Path.GetRelativePath(root, file), File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable template files are skipped rather than failing init.
            }
        }
        return templates;
    }

    public const string DefaultAgentsMd = """
        # Agent Instructions

        ## 项目约定

        - 动手前先理解项目结构与既有代码风格，遵循现有目录布局与命名习惯。
        - 修改代码后运行项目的构建 / 测试命令验证，未验证不得宣称完成。
        - 不擅自引入新依赖；确有必要时先说明理由。

        ## 沟通

        - 使用中文交流与注释。
        - 完成任务时简要说明改动点与验证方式。
        """;

    public const string DefaultWorkspaceConfig = """
        {
          "mode": "edit",
          "language": "auto"
        }
        """;
}
