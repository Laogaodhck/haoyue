using System.CommandLine;
using Haoyue.Cli;
using Haoyue.Cli.Commands;
using Spectre.Console;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
catch (IOException) { /* redirected or headless output */ }

var root = new RootCommand("Haoyue — industrial-grade AI agent runtime with a modern terminal front end");

var promptArg = new Argument<string[]>("prompt")
{
    Arity = ArgumentArity.ZeroOrMore,
    Description = "One-shot prompt; omit to start interactive chat",
};
var continueOption = new Option<bool>("--continue", "-c") { Description = "Continue the most recent session" };
var resumeOption = new Option<string?>("--resume") { Description = "Resume a session by id" };
var modelOption = new Option<string?>("--model", "-m") { Description = "Override the active model for this run (provider/model)" };
var expertOption = new Option<string?>("--expert", "-e") { Description = "Bind an expert persona to this session (id from 'haoyue expert list')" };

root.Add(promptArg);
root.Add(continueOption);
root.Add(resumeOption);
root.Add(modelOption);
root.Add(expertOption);
root.SetAction((parse, _) => RunChatAsync(
    parse.GetValue(promptArg) ?? [],
    parse.GetValue(continueOption),
    parse.GetValue(resumeOption),
    parse.GetValue(modelOption),
    parse.GetValue(expertOption)));

var chat = new Command("chat", "Interactive chat (the default command)");
chat.Add(continueOption);
chat.Add(resumeOption);
chat.Add(modelOption);
chat.Add(expertOption);
chat.SetAction((parse, _) => RunChatAsync(
    [],
    parse.GetValue(continueOption),
    parse.GetValue(resumeOption),
    parse.GetValue(modelOption),
    parse.GetValue(expertOption)));
root.Add(chat);

root.Add(ProviderCommands.Build());
root.Add(ModelCommands.Build());
root.Add(UsageCommands.Build());
root.Add(DoctorCommand.Build());
root.Add(SwitchCommand.Build());
root.Add(InitCommand.Build());
root.Add(SkillCommands.Build());
root.Add(McpCommands.Build());
root.Add(SessionCommands.Build());
root.Add(KnowledgeCommands.Build());
root.Add(MemoryCommands.Build());
root.Add(RulesCommands.Build());
root.Add(ExpertCommands.Build());
root.Add(ScheduleCommands.Build());
root.Add(EvolutionCommands.Build());
root.Add(DaemonCommand.Build());

return await root.Parse(args).InvokeAsync();

static async Task<int> RunChatAsync(string[] promptWords, bool continueLast, string? resumeId, string? modelOverride, string? expertId)
{
    await using var runtime = CliHost.CreateRuntime();

    if (modelOverride is not null)
    {
        var model = runtime.Models.Resolve(modelOverride);
        if (model is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown model:[/] {Markup.Escape(modelOverride)}");
            return 1;
        }
        // In-memory override only — the saved config is untouched.
        var config = runtime.ConfigStore.Config;
        config.Provider = model.Provider.Id;
        config.Model = model.Model.Id;
    }

    if (expertId is not null && Haoyue.Runtime.Experts.ExpertCatalog.Find(expertId) is null)
    {
        AnsiConsole.MarkupLine($"[red]Unknown expert:[/] {Markup.Escape(expertId)} (run [cyan]haoyue expert list[/])");
        return 1;
    }

    var loop = new ChatLoop(runtime, expertId);
    var prompt = string.Join(' ', promptWords).Trim();
    return prompt.Length > 0
        ? await loop.RunOneShotAsync(prompt, continueLast)
        : await loop.RunInteractiveAsync(continueLast, resumeId);
}
