using System.Text;
using Haoyue.Cli.Commands;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Scheduling;
using Haoyue.Runtime.Workspaces;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace Haoyue.Cli.Tests;

public sealed class DataCommandsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "haoyue-dc-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        // Pooling=true keeps file handles in the connection pool; release them
        // before deleting the temp directory.
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private (IAnsiConsole Console, StringWriter Writer) CreateConsole()
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 240; // avoid wrapping long ids in table cells
        return (console, writer);
    }

    [Fact]
    public void RenderEntries_PrintsIdTitleAndTags()
    {
        var (console, writer) = CreateConsole();
        var entries = new[]
        {
            new KnowledgeEntry(7, "部署流程", "内容", "ops,release", "2026-01-01T00:00:00.0000000Z", "2026-01-02T00:00:00.0000000Z"),
        };

        KnowledgeCommands.RenderEntries(console, entries);

        var output = writer.ToString();
        Assert.Contains("部署流程", output);
        Assert.Contains("7", output);
        Assert.Contains("ops,release", output);
    }

    [Fact]
    public void RenderEntries_EmptyList_PrintsHint()
    {
        var (console, writer) = CreateConsole();

        KnowledgeCommands.RenderEntries(console, []);

        Assert.Contains("No knowledge entries yet", writer.ToString());
    }

    [Fact]
    public void RenderDetail_PrintsFullContent()
    {
        var (console, writer) = CreateConsole();

        KnowledgeCommands.RenderDetail(console, new KnowledgeEntry(3, "API 约定", "全文内容在这里", null, "2026-01-01T00:00:00.0000000Z", "2026-01-01T00:00:00.0000000Z"));

        var output = writer.ToString();
        Assert.Contains("API 约定", output);
        Assert.Contains("全文内容在这里", output);
    }

    [Fact]
    public void RenderRules_EmptyList_PrintsHint()
    {
        var (console, writer) = CreateConsole();

        RulesCommands.RenderRules(console, []);

        Assert.Contains("No AGENTS.md rule files", writer.ToString());
    }

    [Fact]
    public void RenderRules_PrintsPaths()
    {
        var (console, writer) = CreateConsole();

        RulesCommands.RenderRules(console, [new WorkspaceRuleFile("AGENTS.md", true, "a\nb\n")]);

        var output = writer.ToString();
        Assert.Contains("AGENTS.md", output);
    }

    [Fact]
    public void RenderExperts_PrintsNamesAndDomains()
    {
        var (console, writer) = CreateConsole();

        ExpertCommands.RenderExperts(console, Haoyue.Runtime.Experts.ExpertCatalog.Entries);

        var output = writer.ToString();
        Assert.Contains("全栈工程师", output);
        Assert.Contains("fullstack-engineer", output);
        Assert.Contains("全栈开发", output);
    }

    [Fact]
    public void RenderSchedules_PrintsCronStatusAndShortIds()
    {
        var (console, writer) = CreateConsole();
        var task = new ScheduledTask
        {
            Id = "abcd1234-5678-90ef",
            Name = "晨报",
            Prompt = "生成日报",
            Cron = "0 9 * * *",
            Enabled = true,
            LastStatus = ScheduleRunStatus.Error,
            LastError = "boom",
        };

        ScheduleCommands.RenderSchedules(console, [task]);

        var output = writer.ToString();
        Assert.Contains("晨报", output);
        Assert.Contains("0 9 * * *", output);
        Assert.Contains("abcd1234", output); // short id column
        Assert.Contains("error", output);
    }

    [Fact]
    public void RenderScheduleDetail_PrintsPromptAndError()
    {
        var (console, writer) = CreateConsole();
        var task = new ScheduledTask
        {
            Id = "abcd1234-5678-90ef",
            Name = "晨报",
            Prompt = "生成日报",
            Cron = "0 9 * * *",
            Enabled = false,
            LastStatus = ScheduleRunStatus.Error,
            LastError = "provider offline",
            LastOutput = "部分输出",
        };

        ScheduleCommands.RenderScheduleDetail(console, task);

        var output = writer.ToString();
        Assert.Contains("生成日报", output);
        Assert.Contains("provider offline", output);
        Assert.Contains("部分输出", output);
    }

    [Fact]
    public void MatchTask_ResolvesExactId_UniquePrefix_AmbiguousPrefix_AndMiss()
    {
        Directory.CreateDirectory(_root);
        var store = new ScheduleStore(new HaoyueDatabase(Path.Combine(_root, "db.sqlite")));
        store.Upsert("aaaaaaaa-1", "one", null, "p", "0 9 * * *", enabled: true);
        store.Upsert("aaaaaaaa-2", "two", null, "p", "0 9 * * *", enabled: true);
        store.Upsert("bbbbbbbb-1", "three", null, "p", "0 9 * * *", enabled: true);

        var exact = ScheduleCommands.MatchTask(store, "aaaaaaaa-1");
        Assert.NotNull(exact.Task);
        Assert.Null(exact.Error);

        var uniquePrefix = ScheduleCommands.MatchTask(store, "bbbbbbbb");
        Assert.NotNull(uniquePrefix.Task);
        Assert.Equal("three", uniquePrefix.Task!.Name);

        var ambiguous = ScheduleCommands.MatchTask(store, "aaaaaaaa");
        Assert.Null(ambiguous.Task);
        Assert.Contains("Ambiguous", ambiguous.Error);

        var miss = ScheduleCommands.MatchTask(store, "zzzzzzzz");
        Assert.Null(miss.Task);
        Assert.Contains("not found", miss.Error);
    }
}
