using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;

namespace Haoyue.Runtime.Tools.Builtin;

public abstract class BuiltinTool(IPromptProvider prompts) : ITool
{
    public abstract string Name { get; }
    public abstract JsonObject ParameterSchema { get; }
    public virtual bool Mutating => false;
    // Virtual (not interface defaults): the interface map is fixed at this base
    // class, so derived tools must override these to be visible to ITool callers.
    public virtual bool RequiresWorkspace => true;
    public virtual bool RequiresNetwork => false;
    public virtual bool RequiresVision => false;
    public virtual string StatusLabel => "Working";

    /// <summary>Tools whose description file was already reported missing (warn once per tool).</summary>
    private static readonly HashSet<string> MissingDescriptionWarned = new(StringComparer.Ordinal);

    /// <summary>
    /// Tool descriptions live in prompts/tool/&lt;name&gt;.txt (hot-reloadable). A missing
    /// file silently degrades to a content-free one-liner — the model then picks tools
    /// mostly by name, so warn once loudly instead of failing quietly.
    /// </summary>
    public string Description
    {
        get
        {
            var description = prompts.TryGet($"tool/{Name}");
            if (description is not null) return description;
            lock (MissingDescriptionWarned)
            {
                if (MissingDescriptionWarned.Add(Name))
                    Console.Error.WriteLine(
                        $"[haoyue] warning: tool description 'prompts/tool/{Name}.txt' is missing; " +
                        "falling back to a generic one-liner. Tool-selection accuracy will suffer.");
            }
            return $"The {Name} tool.";
        }
    }

    /// <summary>How long a mutating tool waits for the central file write lock before failing.</summary>
    protected static readonly TimeSpan FileWriteLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Atomic file replacement: write a unique temp file in the target directory, then
    /// rename over the destination. A crash, IO error or cancellation mid-write can
    /// never leave a half-written target file behind — the worst case is an orphan
    /// temp file, which the finally block removes.
    /// </summary>
    protected static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var temp = Path.Combine(
            Path.GetDirectoryName(path) ?? ".",
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Runs a mutating tool body under the central file write lock when a coordinator is
    /// present (daemon / concurrent turns). The mutation itself re-reads the file inside
    /// the lock, so edits always apply to the latest on-disk content. Without a
    /// coordinator (single-turn CLI) the body runs directly.
    /// </summary>
    protected static async Task<ToolResult> WithFileWriteLockAsync(
        ToolContext context,
        string path,
        Func<CancellationToken, Task<ToolResult>> mutation,
        CancellationToken ct)
    {
        var coordinator = context.Coordinator;
        if (coordinator is null)
            return await mutation(ct).ConfigureAwait(false);

        var relative = Path.GetRelativePath(context.Workspace.Root, path);
        var acquired = await coordinator.TryAcquireAsync(
            context.Workspace.Root, path, context.Owner, FileWriteLockTimeout, ct).ConfigureAwait(false);
        if (!acquired)
        {
            var holder = coordinator.GetOwner(context.Workspace.Root, path);
            return ToolResult.Fail(
                $"Write lock timeout: {relative} is being edited by another task" +
                (holder is null ? "" : $" ({holder})") +
                ". Wait for it to finish, re-read the latest file content, then retry.");
        }
        try
        {
            return await mutation(ct).ConfigureAwait(false);
        }
        finally
        {
            coordinator.Release(context.Workspace.Root, path, context.Owner);
        }
    }

    public abstract Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct);

    protected static string? GetString(JsonObject args, string name) =>
        args[name] is { } node ? node.GetValue<string>() : null;

    protected static int? GetInt(JsonObject args, string name)
    {
        // JSON numbers arrive element-backed (model path) or CLR-backed Int32/Int64
        // (in-memory callers); JsonValue only converts to the exact boxed type, so
        // try each integer shape and fall back to parsing the JSON text.
        if (args[name] is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var intValue)) return intValue;
        if (value.TryGetValue<double>(out var doubleValue)) return (int)doubleValue;
        if (value.TryGetValue<long>(out var longValue)) return (int)longValue;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    protected static bool GetBool(JsonObject args, string name) =>
        args[name] is { } node && node.GetValue<bool>();
}

public sealed class ReadFileTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "read_file";
    public override string StatusLabel => "Reading files";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.String("File path (absolute, or relative to the workspace root)"), true),
        ("offset", ToolSchema.Integer("1-based line number to start reading from"), false),
        ("limit", ToolSchema.Integer("Maximum number of lines to read"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var path = context.ResolvePath(GetString(arguments, "path") ?? "");
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"File not found: {path}"));

        var offset = Math.Max(1, GetInt(arguments, "offset") ?? 1);
        var limit = Math.Max(1, GetInt(arguments, "limit") ?? 2000);

        var lines = File.ReadLines(path).Skip(offset - 1).Take(limit).ToList();
        var sb = new StringBuilder();

        // Send file content in chunks with progress events
        const int chunkSize = 50; // lines per chunk
        var chunks = (lines.Count + chunkSize - 1) / chunkSize;

        for (var chunk = 0; chunk < chunks; chunk++)
        {
            var start = chunk * chunkSize;
            var end = Math.Min(start + chunkSize, lines.Count);

            var chunkSb = new StringBuilder();
            for (var i = start; i < end; i++)
            {
                var line = lines[i].Length > 2000 ? lines[i][..2000] + "…" : lines[i];
                chunkSb.Append(offset + i).Append('\t').AppendLine(line);
            }

            sb.Append(chunkSb);

            // Send progress event for UI display
            if (chunks > 1)
                context.Events.Publish(new ToolCallProgressEvent(
                    context.CallId,
                    $"[{end}/{lines.Count}] Reading…\n{chunkSb}"));
            else if (chunks == 1)
                context.Events.Publish(new ToolCallProgressEvent(context.CallId, chunkSb.ToString()));
        }

        var output = sb.Length == 0 ? "(empty file)" : context.Truncate(sb.ToString(), "file");
        var relative = Path.GetRelativePath(context.Workspace.Root, path);
        return Task.FromResult(ToolResult.Ok(output, $"Read {lines.Count} lines from {relative}") with { FilePath = path });
    }
}

public sealed class WriteFileTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "write_file";
    public override bool Mutating => true;
    public override string StatusLabel => "Editing";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.String("File path to create or overwrite"), true),
        ("content", ToolSchema.String("Full new file content"), true));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var path = context.ResolvePath(GetString(arguments, "path") ?? "");
        var content = GetString(arguments, "content") ?? "";
        return WithFileWriteLockAsync(context, path, _ => WriteCoreAsync(path, content, context, ct), ct);
    }

    private static async Task<ToolResult> WriteCoreAsync(
        string path, string content, ToolContext context, CancellationToken ct)
    {
        var existed = File.Exists(path);
        var oldText = existed ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : "";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteAtomicAsync(path, content, ct).ConfigureAwait(false);

        var relative = Path.GetRelativePath(context.Workspace.Root, path);
        var lineCount = content.Count(c => c == '\n') + 1;
        var diff = DiffUtil.Unified(oldText, content, relative);
        if (diff.Length > 0)
            context.Events.Publish(new FileDiffEvent(context.CallId, relative, diff));

        // Undo ledger: remember the pre-turn content so agent.undo can restore it.
        // previousContent=null marks a newly created file (undo deletes it).
        context.TurnScope?.TryRegisterFileChange(
            path, existed ? oldText : null,
            $"{(existed ? "覆盖" : "创建")} {relative}（写回 {(existed ? "旧内容" : "删除文件")}）");

        return ToolResult.Ok(
            existed ? $"Overwrote {relative}" : $"Created {relative}",
            $"{(existed ? "Updated" : "Created")} {relative} ({lineCount} lines)") with
        { Diff = diff, FilePath = path };
    }
}

public sealed class EditFileTool(IPromptProvider prompts) : BuiltinTool(prompts)
{
    public override string Name => "edit_file";
    public override bool Mutating => true;
    public override string StatusLabel => "Editing";

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("path", ToolSchema.String("File path to edit"), true),
        ("old_string", ToolSchema.String("Exact text to replace (must match uniquely unless replace_all)"), true),
        ("new_string", ToolSchema.String("Replacement text"), true),
        ("replace_all", ToolSchema.Boolean("Replace every occurrence instead of requiring a unique match"), false));

    public override Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var path = context.ResolvePath(GetString(arguments, "path") ?? "");
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Fail($"File not found: {path}"));

        var oldString = GetString(arguments, "old_string") ?? "";
        var newString = GetString(arguments, "new_string") ?? "";
        if (oldString.Length == 0)
            return Task.FromResult(ToolResult.Fail("old_string must not be empty."));
        if (oldString == newString)
            return Task.FromResult(ToolResult.Fail("old_string and new_string are identical."));

        var replaceAll = GetBool(arguments, "replace_all");
        return WithFileWriteLockAsync(context, path, _ => EditCoreAsync(path, oldString, newString, replaceAll, context, ct), ct);
    }

    private static async Task<ToolResult> EditCoreAsync(
        string path, string oldString, string newString, bool replaceAll, ToolContext context, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var (effectiveOld, effectiveNew, occurrences) = ResolveMatch(text, oldString, newString);
        if (occurrences == 0)
            return ToolResult.Fail($"old_string not found in {path}. Read the file again — the content may have changed.");

        if (occurrences > 1 && !replaceAll)
            return ToolResult.Fail(
                $"old_string matches {occurrences} locations in {path}. Provide more surrounding context to make it unique, or set replace_all.");

        var updated = replaceAll
            ? text.Replace(effectiveOld, effectiveNew)
            : ReplaceFirst(text, effectiveOld, effectiveNew);
        await WriteAtomicAsync(path, updated, ct).ConfigureAwait(false);

        var relative = Path.GetRelativePath(context.Workspace.Root, path);
        var diff = DiffUtil.Unified(text, updated, relative);
        if (diff.Length > 0)
            context.Events.Publish(new FileDiffEvent(context.CallId, relative, diff));

        // Undo ledger: remember the pre-edit content so agent.undo can restore it.
        context.TurnScope?.TryRegisterFileChange(path, text, $"编辑 {relative}（写回编辑前内容）");

        var summary = replaceAll && occurrences > 1
            ? $"Replaced {occurrences} occurrences in {relative}"
            : $"Edited {relative}";
        return ToolResult.Ok(summary, summary) with { Diff = diff, FilePath = path };
    }

    /// <summary>
    /// Resolves the effective old/new pair for the replacement. Exact matching wins; when
    /// it finds nothing, the match is retried with the other line-ending style — models
    /// routinely describe edits with "\n" while Windows files use "\r\n" (and vice versa),
    /// and a raw byte comparison would report a false "not found". The matched variant also
    /// normalizes new_string to the same style, so the file never ends up with mixed
    /// line endings. Uniqueness is enforced on the variant actually being replaced.
    /// </summary>
    internal static (string EffectiveOld, string EffectiveNew, int Occurrences) ResolveMatch(
        string text, string oldString, string newString)
    {
        var occurrences = CountOccurrences(text, oldString);
        if (occurrences > 0)
            return (oldString, newString, occurrences);

        foreach (var (variantOld, normalizeNew) in LineEndingVariants(oldString))
        {
            var variantOccurrences = CountOccurrences(text, variantOld);
            if (variantOccurrences > 0)
                return (variantOld, normalizeNew(newString), variantOccurrences);
        }
        return (oldString, newString, 0);
    }

    /// <summary>
    /// Yields the old_string rewritten in the opposite line-ending style (LF↔CRLF) together
    /// with a function that normalizes new_string into that same style. A string without
    /// any line break produces no variants.
    /// </summary>
    private static List<(string VariantOld, Func<string, string> NormalizeNew)> LineEndingVariants(string oldString)
    {
        var variants = new List<(string, Func<string, string>)>();
        var lfForm = oldString.Replace("\r\n", "\n");
        if (lfForm != oldString)
            variants.Add((lfForm, newString => newString.Replace("\r\n", "\n")));
        var crlfForm = lfForm.Replace("\n", "\r\n");
        if (crlfForm != oldString)
            variants.Add((crlfForm, newString => newString.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        return variants;
    }

    internal static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    internal static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newValue + text[(index + oldValue.Length)..];
    }
}
