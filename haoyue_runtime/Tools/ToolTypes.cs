using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Tools;

public sealed class ToolContext
{
    public required WorkspaceInfo Workspace { get; init; }
    public required IEventBus Events { get; init; }
    public required AgentConfig Agent { get; init; }
    /// <summary>Adaptive per-call output budget (characters), derived from the model context window.</summary>
    public int MaxOutputChars { get; init; } = 30_000;
    public string CallId { get; init; } = "";
    /// <summary>Centralized write-lock coordinator shared by all concurrent turns.</summary>
    public IFileLockCoordinator? Coordinator { get; init; }
    /// <summary>Task identity used when acquiring file write locks.</summary>
    public string Owner { get; init; } = "";
    /// <summary>
    /// Per-turn execution scope: file-writing tools register the pre-turn content of
    /// each change here (undo ledger) and every execution appends a step record.
    /// Null in contexts without a turn scope (direct tool invocations in tests).
    /// </summary>
    public Haoyue.Runtime.Agents.TurnExecutionScope? TurnScope { get; init; }
    /// <summary>
    /// Per-turn plan state written by update_plan so the runtime can observe plan
    /// progress and inject plan-discipline nudges. Null without a turn scope.
    /// </summary>
    public Haoyue.Runtime.Agents.TurnPlan? PlanTracker { get; init; }

    public string ResolvePath(string path)
    {
        var root = Workspace.IsGlobal ? Directory.GetCurrentDirectory() : Workspace.Root;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }

    /// <summary>
    /// Workspace-relative name of a resolved path, for messages and lock keys.
    /// </summary>
    public string RelativePath(string absolutePath)
    {
        var root = Workspace.IsGlobal ? Directory.GetCurrentDirectory() : Workspace.Root;
        return Path.GetRelativePath(root, absolutePath);
    }

    /// <summary>
    /// Resolves a path for file-READING tools and enforces the read boundary — the
    /// workspace root, the user's whitelisted roots and, for global sessions, the profile
    /// directory minus credential material. Without it a read tool could dump ~/.ssh or
    /// another user's home into the model context and ship it out via web_fetch.
    /// </summary>
    public bool TryResolveReadablePath(string path, out string resolved, out string? error)
    {
        resolved = ResolvePath(path);
        if (!PathGuard.IsReadable(Workspace, Agent, resolved, out error)) return false;

        // The trail the runtime writes about the agent must not be an input to the agent:
        // reading it lets a prompt leak or rewrite its own history.
        var auditDir = Path.Combine(Workspace.Root, ".haoyue", "audit");
        if (PathGuard.IsInside(auditDir, resolved))
        {
            error = $"Refusing to read {resolved}: the audit trail belongs to the user, not to the agent.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Resolves a path for file-MUTATING tools and enforces the workspace boundary:
    /// the result must stay inside the workspace root, and well-known sensitive
    /// targets (.git internals, .env secrets, the bash audit log) are rejected even
    /// inside the workspace. This is the runtime backstop behind the mode layer —
    /// without it an edit-mode agent could write arbitrary absolute paths on disk.
    /// Returns false with a model-facing error message when the path is out of bounds.
    /// </summary>
    public bool TryResolveWritablePath(string path, out string resolved, out string? error)
    {
        resolved = ResolvePath(path);
        error = null;

        var root = PathGuard.WorkspaceRoot(Workspace);
        if (!PathGuard.IsInside(root, resolved))
        {
            error = $"Path escapes the workspace: {resolved}. File writes are restricted to the workspace root ({root}). "
                + "Write the file inside the workspace, or ask the user to run a shell command for anything outside it.";
            return false;
        }

        // Sensitive in-workspace targets: git internals, env-secret files, audit trail.
        var relative = Path.GetRelativePath(root, resolved);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Refusing to write {relative}: .git internals are protected. Use git commands via the bash tool instead.";
            return false;
        }
        var fileName = segments[^1];
        if (fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Refusing to write {relative}: env-secret files are protected. Ask the user to edit them manually.";
            return false;
        }
        if (segments.Length >= 2 && segments[0].Equals(".haoyue", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("audit", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Refusing to write {relative}: the bash audit trail is append-only and protected.";
            return false;
        }
        return true;
    }

    public string Truncate(string text, string label = "output")
    {
        if (text.Length <= MaxOutputChars) return text;
        return text[..MaxOutputChars] + $"\n… [{label} truncated: {text.Length - MaxOutputChars} characters omitted]";
    }

    /// <summary>
    /// Head+tail truncation for outputs whose decisive information sits at the END
    /// (H-B): build/test logs put failure summaries, stack traces and exit codes at the
    /// tail, which a head-only cut systematically discards — the model then diagnoses
    /// from a truncated log and retries blindly. The middle is folded into a marker
    /// that states how much was omitted. <paramref name="tailShare"/> (0.1..0.9)
    /// controls the split; failing commands raise it toward 0.7-0.8 so the error tail
    /// survives. The rendered result stays within <see cref="MaxOutputChars"/>.
    /// </summary>
    public string TruncateTailPrioritized(string text, string label = "output", double tailShare = 0.5)
    {
        if (text.Length <= MaxOutputChars) return text;
        tailShare = Math.Clamp(tailShare, 0.1, 0.9);
        var marker = $"\n… [{label} truncated: {text.Length - MaxOutputChars} characters omitted from the middle] …\n";
        var body = Math.Max(2, MaxOutputChars - marker.Length);
        var tailChars = Math.Max(1, (int)Math.Round(body * tailShare));
        var headChars = Math.Max(1, body - tailChars);
        return text[..headChars] + marker + text[^tailChars..];
    }
}

public sealed record ToolResult
{
    public required bool Success { get; init; }
    /// <summary>Full text returned to the model.</summary>
    public required string Output { get; init; }
    /// <summary>One-line summary for the terminal renderer.</summary>
    public string? Summary { get; init; }
    public string? Diff { get; init; }
    public string? FilePath { get; init; }
    /// <summary>Optional multi-modal image attachments produced by the tool (e.g. capture_screen).</summary>
    public IReadOnlyList<ChatImageAttachment>? Images { get; init; }

    public static ToolResult Ok(string output, string? summary = null) =>
        new() { Success = true, Output = output, Summary = summary };

    public static ToolResult Ok(string output, IReadOnlyList<ChatImageAttachment>? images, string? summary = null) =>
        new() { Success = true, Output = output, Summary = summary, Images = images };

    public static ToolResult Fail(string error) =>
        new() { Success = false, Output = error, Summary = error.Split('\n')[0] };
}

/// <summary>
/// 工具风险五维（5.2）：分级同意矩阵的判定依据。默认不同意任何维度——
/// 只有显式声明的工具才升风险；「读」维度仅用于 UI 展示，不触发审批。
/// </summary>
[Flags]
public enum ToolRiskTier
{
    None = 0,
    /// <summary>只读（不会改动任何外部状态）。</summary>
    Read = 1,
    /// <summary>写入文件或持久化数据。</summary>
    Write = 2,
    /// <summary>对外发送数据（网络请求、上传、外发内容）。</summary>
    Network = 4,
    /// <summary>产生费用（模型调用、图像生成、外部付费 API）。</summary>
    Cost = 8,
    /// <summary>系统级操作（执行命令、控制本机、提权面）。</summary>
    System = 16,
}

public interface ITool
{
    string Name { get; }
    /// <summary>Loaded from prompts/tool/&lt;name&gt;.txt — never hard-coded.</summary>
    string Description { get; }
    JsonObject ParameterSchema { get; }
    /// <summary>True when the tool changes files (triggers the auto build/verify cycle).</summary>
    bool Mutating { get; }
    /// <summary>
    /// 5.2 风险分级：默认按 Mutating 推断写/读，再叠加 RequiresNetwork 的外发维度
    /// ——未显式打标的工具因此也拿到正确的外发标签；执行命令/系统控制/花钱类
    /// 工具由各自声明升档。高风险维度由 agent.toolPolicy.consentRiskTiers 决定
    /// 是否触发人工同意。
    /// </summary>
    ToolRiskTier RiskTier =>
        (Mutating ? ToolRiskTier.Write : ToolRiskTier.Read)
        | (RequiresNetwork ? ToolRiskTier.Network : ToolRiskTier.None);
    /// <summary>True when the tool needs a concrete local project directory.</summary>
    bool RequiresWorkspace => true;
    /// <summary>True when the tool needs outbound network access (web search / web fetch).</summary>
    bool RequiresNetwork => false;
    /// <summary>True when the tool requires a vision-capable multi-modal model (e.g. capture_screen).</summary>
    bool RequiresVision => false;
    /// <summary>Status label for the renderer while the tool runs (Reading files, Searching…).</summary>
    string StatusLabel { get; }

    Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct);
}

public interface IToolRegistry
{
    IDisposable Register(ITool tool);
    ITool? Resolve(string name);
    IReadOnlyList<ITool> All { get; }
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Lock _gate = new();
    private readonly List<ITool> _tools = [];

    public IReadOnlyList<ITool> All
    {
        get { lock (_gate) return [.. _tools]; }
    }

    public IDisposable Register(ITool tool)
    {
        lock (_gate)
        {
            if (_tools.Any(t => t.Name.Equals(tool.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Tool '{tool.Name}' is already registered.");
            _tools.Add(tool);
        }
        return new Registration(this, tool);
    }

    public ITool? Resolve(string name)
    {
        lock (_gate)
            return _tools.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private void Remove(ITool tool)
    {
        lock (_gate) _tools.Remove(tool);
    }

    private sealed class Registration(ToolRegistry owner, ITool tool) : IDisposable
    {
        public void Dispose() => owner.Remove(tool);
    }
}

/// <summary>Fluent JSON Schema builder for tool parameters.</summary>
public static class ToolSchema
{
    public static JsonObject Object(params (string Name, JsonObject Property, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, property, isRequired) in properties)
        {
            props[name] = property;
            if (isRequired) required.Add((JsonNode)name);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = required,
        };
    }

    public static JsonObject String(string description, params string[] allowedValues)
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = description };
        if (allowedValues is { Length: > 0 })
        {
            var values = new JsonArray();
            foreach (var value in allowedValues) values.Add((JsonNode)value);
            schema["enum"] = values;
        }
        return schema;
    }

    public static JsonObject Integer(string description) =>
        new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject Boolean(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Array(string description, JsonObject? items = null)
    {
        var schema = new JsonObject { ["type"] = "array", ["description"] = description };
        if (items is not null) schema["items"] = items;
        return schema;
    }
}

