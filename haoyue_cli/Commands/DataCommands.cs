using System.CommandLine;
using Cronos;
using Haoyue.Runtime;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Experts;
using Haoyue.Runtime.Scheduling;
using Haoyue.Runtime.Workspaces;
using Spectre.Console;

namespace Haoyue.Cli.Commands;

/// <summary>Reads text content from stdin when the shell pipes it in; otherwise null.</summary>
internal static class PipedInput
{
    public static string? ReadText() => Console.IsInputRedirected ? Console.In.ReadToEnd() : null;
}

public static class KnowledgeCommands
{
    public static Command Build()
    {
        var command = new Command("knowledge", "Manage the workspace knowledge base");

        var list = new Command("list", "List knowledge entries in this workspace scope");
        list.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            RenderEntries(AnsiConsole.Console, rt.Knowledge.List(HaoyueDatabase.ScopeKey(rt.Workspace)));
            return 0;
        });

        var queryArg = new Argument<string>("query");
        var search = new Command("search", "Search entries by keyword (title, content and tags, ranked)");
        search.Add(queryArg);
        search.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            RenderEntries(AnsiConsole.Console, rt.Knowledge.Search(HaoyueDatabase.ScopeKey(rt.Workspace), parse.GetRequiredValue(queryArg)));
            return 0;
        });

        var titleArg = new Argument<string>("title");
        var contentArg = new Argument<string?>("content") { Arity = ArgumentArity.ZeroOrOne };
        var tagsOption = new Option<string?>("--tags") { Description = "Comma-separated tags" };
        var add = new Command("add", "Add an entry (an existing title is updated in place)");
        add.Add(titleArg);
        add.Add(contentArg);
        add.Add(tagsOption);
        add.SetAction(parse =>
        {
            var content = parse.GetValue(contentArg) ?? PipedInput.ReadText();
            if (string.IsNullOrWhiteSpace(content))
            {
                AnsiConsole.MarkupLine("[red]Content is required.[/] Pass it as an argument or pipe it in: [cyan]cat notes.md | haoyue knowledge add \"Title\" --tags a,b[/]");
                return 1;
            }
            using var rt = CliHost.CreateRuntime();
            var result = rt.Knowledge.Save(
                HaoyueDatabase.ScopeKey(rt.Workspace),
                parse.GetRequiredValue(titleArg), content,
                parse.GetValue(tagsOption));
            AnsiConsole.MarkupLine(result.Created
                ? $"[green]Created[/] #{result.Entry.Id} {Markup.Escape(result.Entry.Title)}"
                : $"[green]Updated[/] #{result.Entry.Id} {Markup.Escape(result.Entry.Title)}");
            return 0;
        });

        var showIdArg = new Argument<long>("id");
        var show = new Command("show", "Print one entry's full content");
        show.Add(showIdArg);
        show.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var entry = rt.Knowledge.Get(HaoyueDatabase.ScopeKey(rt.Workspace), parse.GetRequiredValue(showIdArg));
            if (entry is null)
            {
                AnsiConsole.MarkupLine($"[red]No knowledge entry #{parse.GetRequiredValue(showIdArg)} in this scope.[/]");
                return 1;
            }
            RenderDetail(AnsiConsole.Console, entry);
            return 0;
        });

        var deleteIdArg = new Argument<long>("id");
        var delete = new Command("delete", "Delete an entry by id");
        delete.Add(deleteIdArg);
        delete.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            if (!rt.Knowledge.Delete(HaoyueDatabase.ScopeKey(rt.Workspace), parse.GetRequiredValue(deleteIdArg)))
            {
                AnsiConsole.MarkupLine($"[red]No knowledge entry #{parse.GetRequiredValue(deleteIdArg)} in this scope.[/]");
                return 1;
            }
            AnsiConsole.MarkupLine("[yellow]Deleted.[/]");
            return 0;
        });

        command.Add(list); command.Add(search); command.Add(add); command.Add(show); command.Add(delete);

        var pathsArg = new Argument<string[]>("paths") { Arity = ArgumentArity.OneOrMore, Description = "Files to import (txt/md/csv/docx/xlsx/code, 10 MB max each)" };
        var import = new Command("import", "Import files as knowledge entries (auto-chunked, re-import upserts)");
        import.Add(pathsArg);
        import.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var scope = HaoyueDatabase.ScopeKey(rt.Workspace);
            var paths = parse.GetRequiredValue(pathsArg);
            var okFiles = 0;
            var totalEntries = 0;
            var failures = 0;
            foreach (var raw in paths)
            {
                try
                {
                    var (source, count) = KnowledgeIngest.ImportFile(rt.Knowledge, scope, raw);
                    okFiles++;
                    totalEntries += count;
                    AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(source.Title)} → {count} 条");
                }
                catch (KnowledgeImportException ex)
                {
                    failures++;
                    AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(Path.GetFileName(raw.Trim()))}：{Markup.Escape(ex.Message)}[/]");
                }
            }
            var failureNote = failures > 0 ? $"，{failures} 个文件失败" : "";
            AnsiConsole.MarkupLine($"[gray]导入完成：{okFiles} 个文件，{totalEntries} 条知识{failureNote}。[/]");
            return failures > 0 ? 1 : 0;
        });

        var exportFileArg = new Argument<string?>("file") { Arity = ArgumentArity.ZeroOrOne, Description = "Output .md path (prints to stdout when omitted)" };
        var export = new Command("export", "Export all entries as one Markdown document (backup/sharing)");
        export.Add(exportFileArg);
        export.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var entries = rt.Knowledge.List(HaoyueDatabase.ScopeKey(rt.Workspace), 2000);
            var markdown = KnowledgeExport.ToMarkdown(rt.Workspace.IsGlobal ? "全局" : rt.Workspace.Root, entries);
            var target = parse.GetValue(exportFileArg);
            if (string.IsNullOrWhiteSpace(target))
            {
                Console.Out.Write(markdown);
                return 0;
            }
            try
            {
                File.WriteAllText(target, markdown);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                AnsiConsole.MarkupLine($"[red]无法写入导出文件：{Markup.Escape(ex.Message)}[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[green]Exported[/] {entries.Count} 条知识 → {Markup.Escape(target)}");
            return 0;
        });

        command.Add(import); command.Add(export);
        return command;
    }

    internal static void RenderEntries(IAnsiConsole console, IReadOnlyList<KnowledgeEntry> entries)
    {
        if (entries.Count == 0)
        {
            console.MarkupLine("[gray]No knowledge entries yet.[/] Add one with [cyan]haoyue knowledge add[/].");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Id").AddColumn("Title").AddColumn("Tags").AddColumn("Updated");
        foreach (var entry in entries)
            table.AddRow(
                entry.Id.ToString(),
                Markup.Escape(entry.Title),
                Markup.Escape(entry.Tags ?? "-"),
                Markup.Escape(FormatTimestamp(entry.UpdatedAt)));
        console.Write(table);
    }

    internal static void RenderDetail(IAnsiConsole console, KnowledgeEntry entry)
    {
        console.MarkupLine($"[bold]#{entry.Id} {Markup.Escape(entry.Title)}[/]");
        console.MarkupLine($"[gray]tags: {Markup.Escape(entry.Tags ?? "-")} · updated: {Markup.Escape(FormatTimestamp(entry.UpdatedAt))}[/]");
        console.WriteLine();
        console.WriteLine(entry.Content);
    }

    private static string FormatTimestamp(string iso) =>
        DateTimeOffset.TryParse(iso, out var value) ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : iso;
}

public static class MemoryCommands
{
    public static Command Build()
    {
        var command = new Command("memory", "Show or edit the workspace memory file (MEMORY.md)");
        var globalOption = new Option<bool>("--global") { Description = "Operate on the global (~/.haoyue) memory instead" };

        var show = new Command("show", "Print the memory file path and content");
        show.Add(globalOption);
        show.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var workspace = ResolveWorkspace(rt, parse.GetValue(globalOption));
            var memory = ReadMemory(workspace);
            AnsiConsole.MarkupLine($"[gray]{Markup.Escape(workspace.MemoryFile)}[/] (exists: {(memory is null ? "no" : "yes")})");
            if (memory is null)
                AnsiConsole.MarkupLine("[gray]Memory is empty.[/]");
            else
                AnsiConsole.WriteLine(memory);
            return 0;
        });

        var contentArg = new Argument<string?>("content") { Arity = ArgumentArity.ZeroOrOne };
        var set = new Command("set", "Overwrite the memory file (pipe content in or pass it as an argument)");
        set.Add(contentArg);
        set.Add(globalOption);
        set.SetAction(parse =>
        {
            var content = parse.GetValue(contentArg) ?? PipedInput.ReadText();
            if (content is null)
            {
                AnsiConsole.MarkupLine("[red]Content is required.[/] Pass it as an argument or pipe it in: [cyan]cat MEMORY.md | haoyue memory set[/]");
                return 1;
            }
            using var rt = CliHost.CreateRuntime();
            var workspace = ResolveWorkspace(rt, parse.GetValue(globalOption));
            try
            {
                Directory.CreateDirectory(workspace.MemoryDir);
                File.WriteAllText(workspace.MemoryFile, content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AnsiConsole.MarkupLine($"[red]无法写入记忆文件：{Markup.Escape(ex.Message)}[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[green]Saved[/] {Markup.Escape(workspace.MemoryFile)}");
            return 0;
        });

        command.Add(show); command.Add(set);
        return command;
    }

    internal static WorkspaceInfo ResolveWorkspace(HaoyueRuntime rt, bool global) =>
        global ? rt.Workspaces.CreateGlobal() : rt.Workspace;

    internal static string? ReadMemory(WorkspaceInfo workspace)
    {
        if (!File.Exists(workspace.MemoryFile)) return null;
        try { return File.ReadAllText(workspace.MemoryFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]无法读取记忆文件：{Markup.Escape(ex.Message)}[/]");
            return null;
        }
    }
}

public static class RulesCommands
{
    public static Command Build()
    {
        var command = new Command("rules", "Show or edit hierarchical AGENTS.md rule files");

        var list = new Command("list", "List rule files under the workspace root (two directory levels)");
        list.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            RenderRules(AnsiConsole.Console, WorkspaceRules.List(rt.Workspace));
            return 0;
        });

        var showPathArg = new Argument<string?>("path") { Arity = ArgumentArity.ZeroOrOne };
        var show = new Command("show", "Print one rule file's content (default: AGENTS.md)");
        show.Add(showPathArg);
        show.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var relative = (parse.GetValue(showPathArg) ?? "AGENTS.md").Trim().Replace('\\', '/');
            var rule = WorkspaceRules.List(rt.Workspace)
                .FirstOrDefault(r => string.Equals(r.Path, relative, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
            {
                AnsiConsole.MarkupLine($"[red]Rule file not found: {Markup.Escape(relative)}[/] (run [cyan]haoyue rules list[/])");
                return 1;
            }
            AnsiConsole.MarkupLine($"[gray]{Markup.Escape(rt.Workspace.Root)}/{Markup.Escape(rule.Path)}[/]");
            AnsiConsole.WriteLine(rule.Content);
            return 0;
        });

        var setPathArg = new Argument<string>("path");
        var setContentArg = new Argument<string?>("content") { Arity = ArgumentArity.ZeroOrOne };
        var set = new Command("set", "Write a rule file (path must end with AGENTS.md and stay inside the workspace)");
        set.Add(setPathArg);
        set.Add(setContentArg);
        set.SetAction(parse =>
        {
            var content = parse.GetValue(setContentArg) ?? PipedInput.ReadText();
            if (string.IsNullOrWhiteSpace(content))
            {
                AnsiConsole.MarkupLine("[red]Content is required.[/] Pass it as an argument or pipe it in: [cyan]cat AGENTS.md | haoyue rules set AGENTS.md[/]");
                return 1;
            }
            using var rt = CliHost.CreateRuntime();
            string saved;
            try
            {
                saved = WorkspaceRules.Save(rt.Workspace, parse.GetRequiredValue(setPathArg), content);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[green]Saved[/] {Markup.Escape(saved)}");
            return 0;
        });

        command.Add(list); command.Add(show); command.Add(set);
        return command;
    }

    internal static void RenderRules(IAnsiConsole console, IReadOnlyList<WorkspaceRuleFile> rules)
    {
        if (rules.Count == 0)
        {
            console.MarkupLine("[gray]No AGENTS.md rule files in this workspace.[/] Create one with [cyan]haoyue rules set AGENTS.md \"…\"[/].");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Path").AddColumn("Lines");
        foreach (var rule in rules)
            table.AddRow(
                rule.IsRoot ? $"[bold]{Markup.Escape(rule.Path)}[/]" : Markup.Escape(rule.Path),
                rule.Content.Count(static c => c == '\n').ToString());
        console.Write(table);
    }
}

public static class ExpertCommands
{
    public static Command Build()
    {
        var command = new Command("expert", "Show the built-in expert personas");

        var list = new Command("list", "List built-in expert personas");
        list.SetAction(_ =>
        {
            RenderExperts(AnsiConsole.Console, ExpertCatalog.Entries);
            return 0;
        });

        var idArg = new Argument<string>("id");
        var show = new Command("show", "Print one expert's full system prompt");
        show.Add(idArg);
        show.SetAction(parse =>
        {
            var id = parse.GetRequiredValue(idArg);
            var expert = ExpertCatalog.Entries.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Name, id, StringComparison.Ordinal));
            if (expert is null)
            {
                AnsiConsole.MarkupLine($"[red]Unknown expert: {Markup.Escape(id)}[/] (run [cyan]haoyue expert list[/])");
                return 1;
            }
            RenderExpertDetail(AnsiConsole.Console, expert);
            return 0;
        });

        command.Add(list); command.Add(show);
        return command;
    }

    internal static void RenderExperts(IAnsiConsole console, IReadOnlyList<ExpertCatalog.ExpertProfile> experts)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Avatar").AddColumn("Id").AddColumn("Name").AddColumn("Title").AddColumn("Domains");
        foreach (var expert in experts)
            table.AddRow(
                Markup.Escape(expert.Avatar),
                Markup.Escape(expert.Id),
                Markup.Escape(expert.Name),
                Markup.Escape(expert.Title),
                Markup.Escape(string.Join(", ", expert.Domains)));
        console.Write(table);
    }

    internal static void RenderExpertDetail(IAnsiConsole console, ExpertCatalog.ExpertProfile expert)
    {
        console.MarkupLine($"[bold]{Markup.Escape(expert.Avatar)} {Markup.Escape(expert.Name)}[/] [gray]({Markup.Escape(expert.Id)})[/] — {Markup.Escape(expert.Title)}");
        console.MarkupLine($"[gray]domains: {Markup.Escape(string.Join(", ", expert.Domains))}[/]");
        console.MarkupLine($"[gray]skills: {Markup.Escape(string.Join(", ", expert.Skills))}[/]");
        console.WriteLine(expert.Bio);
        console.MarkupLine("[gray]──────── system prompt ────────[/]");
        console.WriteLine(expert.Prompt);
    }
}

public static class ScheduleCommands
{
    public static Command Build()
    {
        var command = new Command("schedule", "Manage scheduled agent tasks (5-field cron)");

        var list = new Command("list", "List scheduled tasks");
        list.SetAction(_ =>
        {
            using var rt = CliHost.CreateRuntime();
            RenderSchedules(AnsiConsole.Console, rt.Schedules.List());
            return 0;
        });

        var idArg = new Argument<string>("id") { Description = "Task id — a unique prefix also works" };
        var show = new Command("show", "Show one task's details including the last run result");
        show.Add(idArg);
        show.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var (task, error) = MatchTask(rt.Schedules, parse.GetRequiredValue(idArg));
            if (task is null)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error ?? "Task not found.")}[/]");
                return 1;
            }
            RenderScheduleDetail(AnsiConsole.Console, task);
            return 0;
        });

        var nameArg = new Argument<string>("name");
        var cronArg = new Argument<string>("cron") { Description = "5-field cron: minute hour day-of-month month day-of-week" };
        var promptArg = new Argument<string>("prompt");
        var workspaceOption = new Option<string?>("--workspace") { Description = "Absolute workspace path the task runs in (defaults to global)" };
        var disabledOption = new Option<bool>("--disabled") { Description = "Create the task without enabling it" };
        var add = new Command("add", "Add a scheduled task");
        add.Add(nameArg); add.Add(cronArg); add.Add(promptArg); add.Add(workspaceOption); add.Add(disabledOption);
        add.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            ScheduledTask task;
            try
            {
                task = rt.Schedules.Upsert(
                    null,
                    parse.GetRequiredValue(nameArg),
                    parse.GetValue(workspaceOption),
                    parse.GetRequiredValue(promptArg),
                    parse.GetRequiredValue(cronArg),
                    enabled: !parse.GetValue(disabledOption));
            }
            catch (CronFormatException ex)
            {
                AnsiConsole.MarkupLine($"[red]Invalid cron expression: {Markup.Escape(ex.Message)}[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(task.Id)} — next run: {Markup.Escape(FormatTime(task.NextRunAt))}");
            AnsiConsole.MarkupLine($"[gray]Run it from the desktop, or inspect with [cyan]haoyue schedule show {Markup.Escape(task.Id[..8])}[/].[/]");
            return 0;
        });

        var toggleIdArg = new Argument<string>("id") { Description = "Task id — a unique prefix also works" };
        var enable = new Command("enable", "Enable a task");
        enable.Add(toggleIdArg);
        enable.SetAction(parse => Toggle(parse.GetRequiredValue(toggleIdArg), enabled: true));

        var disable = new Command("disable", "Disable a task");
        disable.Add(toggleIdArg);
        disable.SetAction(parse => Toggle(parse.GetRequiredValue(toggleIdArg), enabled: false));

        var removeIdArg = new Argument<string>("id") { Description = "Task id — a unique prefix also works" };
        var remove = new Command("remove", "Delete a task");
        remove.Add(removeIdArg);
        remove.SetAction(parse =>
        {
            using var rt = CliHost.CreateRuntime();
            var (task, error) = MatchTask(rt.Schedules, parse.GetRequiredValue(removeIdArg));
            if (task is null)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error ?? "Task not found.")}[/]");
                return 1;
            }
            rt.Schedules.Remove(task.Id);
            AnsiConsole.MarkupLine($"[yellow]Removed[/] {Markup.Escape(task.DisplayName)} ({Markup.Escape(task.Id)})");
            return 0;
        });

        command.Add(list); command.Add(show); command.Add(add);
        command.Add(enable); command.Add(disable); command.Add(remove);
        return command;
    }

    private static int Toggle(string idOrPrefix, bool enabled)
    {
        using var rt = CliHost.CreateRuntime();
        var (task, error) = MatchTask(rt.Schedules, idOrPrefix);
        if (task is null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error ?? "Task not found.")}[/]");
            return 1;
        }
        rt.Schedules.SetEnabled(task.Id, enabled);
        AnsiConsole.MarkupLine(enabled ? "[green]Enabled.[/]" : "[yellow]Disabled.[/]");
        return 0;
    }

    /// <summary>Resolves a task by exact id or a unique id prefix. Multiple matches produce an error message.</summary>
    internal static (ScheduledTask? Task, string? Error) MatchTask(IScheduleStore store, string idOrPrefix)
    {
        var exact = store.Get(idOrPrefix);
        if (exact is not null) return (exact, null);
        var matches = store.List()
            .Where(t => t.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, $"Task not found: {idOrPrefix}"),
            _ => (null, $"Ambiguous id prefix matches {matches.Count} tasks: {string.Join(", ", matches.Take(3).Select(t => t.Id[..8]))}…"),
        };
    }

    internal static void RenderSchedules(IAnsiConsole console, IReadOnlyList<ScheduledTask> tasks)
    {
        if (tasks.Count == 0)
        {
            console.MarkupLine("[gray]No scheduled tasks yet.[/] Add one with [cyan]haoyue schedule add[/].");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Id").AddColumn("Name").AddColumn("Cron").AddColumn("On").AddColumn("Next run").AddColumn("Last status");
        foreach (var task in tasks)
            table.AddRow(
                Markup.Escape(task.Id.Length > 8 ? task.Id[..8] : task.Id),
                Markup.Escape(task.DisplayName),
                Markup.Escape(task.Cron),
                task.Enabled ? "[green]yes[/]" : "[red]no[/]",
                Markup.Escape(FormatTime(task.NextRunAt)),
                Markup.Escape(task.LastStatus ?? "-"));
        console.Write(table);
    }

    internal static void RenderScheduleDetail(IAnsiConsole console, ScheduledTask task)
    {
        console.MarkupLine($"[bold]{Markup.Escape(task.DisplayName)}[/] [gray]({Markup.Escape(task.Id)})[/]");
        console.MarkupLine($"[gray]cron: {Markup.Escape(task.Cron)} · enabled: {(task.Enabled ? "yes" : "no")}[/]");
        console.MarkupLine($"[gray]workspace: {Markup.Escape(task.Workspace ?? "(global)")}[/]");
        console.MarkupLine($"[gray]next run: {Markup.Escape(FormatTime(task.NextRunAt))} · last run: {Markup.Escape(FormatTime(task.LastRunAt))} ({Markup.Escape(task.LastStatus ?? "-")})[/]");
        if (!string.IsNullOrWhiteSpace(task.LastError))
            console.MarkupLine($"[red]last error: {Markup.Escape(task.LastError)}[/]");
        console.MarkupLine("[gray]──────── prompt ────────[/]");
        console.WriteLine(task.Prompt);
        if (!string.IsNullOrWhiteSpace(task.LastOutput))
        {
            console.MarkupLine("[gray]──────── last output ────────[/]");
            console.WriteLine(task.LastOutput);
        }
    }

    private static string FormatTime(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
}
