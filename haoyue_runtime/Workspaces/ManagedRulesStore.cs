using System.Text.Json;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Workspaces;

/// <summary>一条结构化规则（全局或项目作用域）。</summary>
public sealed class ManagedRuleEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>优先级 0..100，越大越先注入。</summary>
    public int Priority { get; set; } = ManagedRulesStore.DefaultPriority;
    /// <summary>作用域：global（用户级，对所有项目生效）| workspace（仅当前项目）。</summary>
    public string Scope { get; set; } = "workspace";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>同一标题在全局与项目各存在一条启用规则时的冲突提示（项目胜）。</summary>
public sealed record RuleConflict(string Title, string GlobalId, string WorkspaceId, string Winner);

/// <summary>
/// 规则命中记录：哪条规则在哪个会话/时间被注入过。
/// <paramref name="Action"/> / <paramref name="Actor"/> 为冲突解决审计字段（旧数据为 null）。
/// </summary>
public sealed record RuleHit(
    string RuleId,
    string Title,
    string Scope,
    string? SessionId,
    DateTimeOffset At,
    string? Action = null,
    string? Actor = null);

/// <summary>
/// 结构化规则存储：项目规则落在 <c>&lt;workspace&gt;/.haoyue/memory/rules.json</c>，
/// 全局规则落在用户目录 <c>~/memory/rules.json</c>。每回合由提示词组装重新读取，
/// 因此保存后下一回合即生效，无需重启。所有写入原子替换，损坏文件按空集处理。
/// </summary>
public static class ManagedRulesStore
{
    public const int DefaultPriority = 50;
    public const int MaxContentChars = 20_000;
    public const int MaxHits = 200;

    private static readonly object Gate = new();

    /// <summary>全局规则目录（与全局工作区的 MemoryDir 一致）。</summary>
    public static string DefaultGlobalMemoryDir => Path.Combine(HaoyuePaths.Home, "memory");

    public static string RulesFile(WorkspaceInfo workspace) =>
        Path.Combine(workspace.MemoryDir, "rules.json");

    /// <summary>读取项目 + 全局规则（含禁用），并标注每条的实际作用域。</summary>
    public static IReadOnlyList<ManagedRuleEntry> List(WorkspaceInfo workspace, string? globalMemoryDir = null)
    {
        lock (Gate)
        {
            var result = new List<ManagedRuleEntry>();
            var global = Load(Path.Combine(globalMemoryDir ?? DefaultGlobalMemoryDir, "rules.json"), "global");
            if (workspace.IsGlobal)
                return global;
            result.AddRange(global);
            result.AddRange(Load(RulesFile(workspace), "workspace"));
            return result;
        }
    }

    /// <summary>按 id 新增或更新；scope 决定写入位置。返回落盘后的条目。</summary>
    public static ManagedRuleEntry Save(WorkspaceInfo workspace, ManagedRuleEntry entry, string? globalMemoryDir = null)
    {
        var scope = string.Equals(entry.Scope, "global", StringComparison.OrdinalIgnoreCase) ? "global" : "workspace";
        if (scope == "global" && workspace.IsGlobal) scope = "global";
        var file = scope == "global"
            ? Path.Combine(globalMemoryDir ?? DefaultGlobalMemoryDir, "rules.json")
            : RulesFile(workspace);
        if (scope == "workspace" && workspace.IsGlobal)
            throw new InvalidOperationException("全局会话请把规则保存为 global 作用域。");

        var title = entry.Title.Trim();
        if (title.Length == 0) throw new ArgumentException("规则标题不能为空。", nameof(entry));
        var content = entry.Content ?? "";
        if (content.Length > MaxContentChars)
            throw new ArgumentException($"规则内容超过 {MaxContentChars} 字上限。", nameof(entry));
        if (content.Trim().Length == 0) throw new ArgumentException("规则内容不能为空。", nameof(entry));

        lock (Gate)
        {
            var entries = Load(file, scope).ToList();
            var now = DateTimeOffset.UtcNow;
            var existing = entries.FirstOrDefault(item =>
                (!string.IsNullOrWhiteSpace(entry.Id) && item.Id.Equals(entry.Id, StringComparison.Ordinal))
                || (string.IsNullOrWhiteSpace(entry.Id) && item.Title.Equals(title, StringComparison.OrdinalIgnoreCase)));
            if (existing is null)
            {
                existing = new ManagedRuleEntry
                {
                    Id = string.IsNullOrWhiteSpace(entry.Id)
                        ? $"rule-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
                        : entry.Id.Trim(),
                    CreatedAt = now,
                };
                entries.Add(existing);
            }
            existing.Title = title;
            existing.Content = content;
            existing.Enabled = entry.Enabled;
            existing.Priority = Math.Clamp(entry.Priority, 0, 100);
            existing.Scope = scope;
            existing.UpdatedAt = now;
            SaveEntries(file, entries);
            return existing;
        }
    }

    /// <summary>删除一条规则（按 id）；不存在返回 false。</summary>
    public static bool Delete(WorkspaceInfo workspace, string id, string scope, string? globalMemoryDir = null)
    {
        var normalized = string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase) ? "global" : "workspace";
        var file = normalized == "global"
            ? Path.Combine(globalMemoryDir ?? DefaultGlobalMemoryDir, "rules.json")
            : RulesFile(workspace);
        lock (Gate)
        {
            var entries = Load(file, normalized).ToList();
            var removed = entries.RemoveAll(item => item.Id.Equals(id, StringComparison.Ordinal));
            if (removed == 0) return false;
            SaveEntries(file, entries);
            return true;
        }
    }

    /// <summary>生效集：启用 → 优先级降序 → 更新时间升序 → id 升序（稳定顺序）。</summary>
    public static IReadOnlyList<ManagedRuleEntry> Effective(WorkspaceInfo workspace, string? globalMemoryDir = null) =>
        [.. List(workspace, globalMemoryDir)
            .Where(rule => rule.Enabled)
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.UpdatedAt)
            .ThenBy(rule => rule.Id, StringComparer.Ordinal)];

    /// <summary>同标题跨作用域冲突；项目版本生效（winner=workspace）。</summary>
    public static IReadOnlyList<RuleConflict> Conflicts(WorkspaceInfo workspace, string? globalMemoryDir = null)
    {
        var all = List(workspace, globalMemoryDir).Where(rule => rule.Enabled).ToList();
        var conflicts = new List<RuleConflict>();
        foreach (var group in all.GroupBy(rule => rule.Title.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var global = group.FirstOrDefault(rule => rule.Scope == "global");
            var local = group.FirstOrDefault(rule => rule.Scope == "workspace");
            if (global is null || local is null) continue;
            conflicts.Add(new RuleConflict(group.Key, global.Id, local.Id, "workspace"));
        }
        return conflicts;
    }

    /// <summary>
    /// 显式解决同名全局/项目规则冲突。
    /// mode=workspace：项目胜出（禁用全局）；mode=global：全局胜出（禁用项目）；
    /// mode=merge：把全局内容并入项目规则后禁用全局。解决结果与操作者写入命中记录。
    /// 返回解决后的冲突快照（冲突已消失，用于回显胜者）。
    /// </summary>
    public static RuleConflict Resolve(
        WorkspaceInfo workspace, string title, string mode, string actor, string? globalMemoryDir = null)
    {
        var normalizedTitle = (title ?? "").Trim();
        if (normalizedTitle.Length == 0) throw new ArgumentException("规则标题不能为空。", nameof(title));
        var normalizedMode = (mode ?? "").Trim().ToLowerInvariant();
        if (normalizedMode is not ("workspace" or "global" or "merge"))
            throw new ArgumentException("解决方式必须是 workspace / global / merge。", nameof(mode));
        var operatorName = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim();
        var globalFile = Path.Combine(globalMemoryDir ?? DefaultGlobalMemoryDir, "rules.json");
        var localFile = RulesFile(workspace);

        lock (Gate)
        {
            var globalEntries = Load(globalFile, "global");
            var localEntries = Load(localFile, "workspace");
            var global = globalEntries.FirstOrDefault(item =>
                item.Enabled && item.Title.Trim().Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase));
            var local = localEntries.FirstOrDefault(item =>
                item.Enabled && item.Title.Trim().Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase));
            if (global is null || local is null)
                throw new InvalidOperationException($"没有名为「{normalizedTitle}」的启用冲突。");

            var now = DateTimeOffset.UtcNow;
            if (normalizedMode == "workspace")
            {
                global.Enabled = false;
                global.UpdatedAt = now;
                SaveEntries(globalFile, globalEntries);
            }
            else if (normalizedMode == "global")
            {
                local.Enabled = false;
                local.UpdatedAt = now;
                SaveEntries(localFile, localEntries);
            }
            else
            {
                var merged = $"{local.Content.Trim()}\n\n{global.Content.Trim()}";
                if (merged.Length > MaxContentChars)
                    throw new ArgumentException($"合并后规则内容超过 {MaxContentChars} 字上限，请改用手工编辑。", nameof(mode));
                local.Content = merged;
                local.UpdatedAt = now;
                global.Enabled = false;
                global.UpdatedAt = now;
                SaveEntries(localFile, localEntries);
                SaveEntries(globalFile, globalEntries);
            }

            var winner = normalizedMode == "global" ? global : local;
            var winnerScope = normalizedMode == "global" ? "global" : "workspace";
            var hitsFile = Path.Combine(workspace.MemoryDir, "rule-hits.json");
            var hits = LoadHits(hitsFile);
            hits.Add(new RuleHit(
                winner.Id, winner.Title, winnerScope, null, now,
                $"resolve:{normalizedMode}", operatorName));
            if (hits.Count > MaxHits) hits = [.. hits.TakeLast(MaxHits)];
            SaveHits(hitsFile, hits);

            return new RuleConflict(local.Title, global.Id, local.Id, winnerScope);
        }
    }

    /// <summary>渲染注入文本；无生效规则时返回空串。</summary>
    public static string RenderEffectiveText(WorkspaceInfo workspace, string? globalMemoryDir = null)
    {
        var effective = Effective(workspace, globalMemoryDir);
        if (effective.Count == 0) return "";
        return string.Join("\n\n", effective.Select(rule =>
            $"### {rule.Title}（{rule.Scope} · 优先级 {rule.Priority}）\n{rule.Content.Trim()}"));
    }

    /// <summary>记录本回合命中的规则；连续同会话同集合的去重，写失败静默。</summary>
    public static void RecordHits(WorkspaceInfo workspace, IEnumerable<string> ruleIds, string? sessionId)
    {
        try
        {
            var ids = ruleIds.Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 0) return;
            var now = DateTimeOffset.UtcNow;
            var file = Path.Combine(workspace.MemoryDir, "rule-hits.json");
            lock (Gate)
            {
                var hits = LoadHits(file);
                var last = hits.LastOrDefault();
                if (last is not null
                    && now - last.At < TimeSpan.FromMinutes(2)
                    && string.Equals(last.SessionId, sessionId, StringComparison.Ordinal))
                {
                    var lastGroup = hits.TakeLast(ids.Count).ToList();
                    if (lastGroup.Count == ids.Count
                        && lastGroup.All(hit => ids.Contains(hit.RuleId, StringComparer.Ordinal)))
                        return;
                }
                var catalog = List(workspace).ToDictionary(rule => rule.Id, StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    catalog.TryGetValue(id, out var rule);
                    hits.Add(new RuleHit(id, rule?.Title ?? "", rule?.Scope ?? "workspace", sessionId, now));
                }
                if (hits.Count > MaxHits) hits = [.. hits.TakeLast(MaxHits)];
                SaveHits(file, hits);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 命中记录是观测增强，绝不影响回合。
        }
    }

    public static IReadOnlyList<RuleHit> ListHits(WorkspaceInfo workspace, int limit = 20)
    {
        var file = Path.Combine(workspace.MemoryDir, "rule-hits.json");
        var hits = LoadHits(file);
        return [.. hits.OrderByDescending(hit => hit.At).Take(Math.Clamp(limit, 1, MaxHits))];
    }

    private static List<ManagedRuleEntry> Load(string file, string scope)
    {
        if (!File.Exists(file)) return [];
        try
        {
            var json = File.ReadAllText(file);
            var entries = JsonSerializer.Deserialize(json, HaoyueJsonContext.Default.ListManagedRuleEntry) ?? [];
            foreach (var entry in entries)
            {
                entry.Scope = scope;
                // 旧文件/手改文件可能缺省值：补齐到可渲染状态。
                entry.Priority = Math.Clamp(entry.Priority, 0, 100);
                entry.UpdatedAt = entry.UpdatedAt == default ? entry.CreatedAt : entry.UpdatedAt;
            }
            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void SaveEntries(string file, List<ManagedRuleEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var json = JsonSerializer.Serialize(entries, HaoyueJsonContext.Default.ListManagedRuleEntry);
        var temp = file + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, file, overwrite: true);
    }

    private static List<RuleHit> LoadHits(string file)
    {
        if (!File.Exists(file)) return [];
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(file), HaoyueJsonContext.Default.ListRuleHit) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void SaveHits(string file, List<RuleHit> hits)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var json = JsonSerializer.Serialize(hits, HaoyueJsonContext.Default.ListRuleHit);
        var temp = file + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, file, overwrite: true);
    }
}
