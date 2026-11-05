using System.Text.Json;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Teams;

/// <summary>
/// 智能体团体持久化：每个项目工作区 <c>&lt;root&gt;/.haoyue/teams.json</c>，
/// 跨项目不可见；全局工作区不支持团体。每次读写都落盘，配置改动热生效。
/// </summary>
public sealed class TeamStore
{
    public const int MaxRoundsLimit = 10;
    public const int MaxTimeoutSeconds = 1800;
    public const int MaxRetriesLimit = 3;

    private static readonly object Gate = new();

    public static string FileFor(WorkspaceInfo workspace) =>
        Path.Combine(workspace.HaoyueDir, "teams.json");

    public IReadOnlyList<TeamDefinition> List(WorkspaceInfo workspace)
    {
        EnsureProject(workspace);
        lock (Gate) return Load(workspace);
    }

    public IReadOnlyList<TeamDefinition> Save(WorkspaceInfo workspace, TeamDefinition team)
    {
        EnsureProject(workspace);
        Validate(team);
        lock (Gate)
        {
            var teams = Load(workspace).ToList();
            var now = DateTimeOffset.UtcNow;
            var existing = teams.FirstOrDefault(item =>
                (!string.IsNullOrWhiteSpace(team.Id) && item.Id.Equals(team.Id, StringComparison.Ordinal))
                || (string.IsNullOrWhiteSpace(team.Id)
                    && item.Name.Equals(team.Name.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (existing is null)
            {
                existing = new TeamDefinition
                {
                    Id = string.IsNullOrWhiteSpace(team.Id)
                        ? $"team-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}"
                        : team.Id.Trim(),
                    CreatedAt = now,
                };
                teams.Add(existing);
            }
            existing.Name = team.Name.Trim();
            existing.Description = team.Description?.Trim() ?? "";
            existing.Mode = team.Mode;
            existing.CoordinatorId = string.IsNullOrWhiteSpace(team.CoordinatorId) ? null : team.CoordinatorId.Trim();
            existing.Members = NormalizeMembers(team.Members);
            existing.Enabled = team.Enabled;
            existing.MaxRounds = Math.Clamp(team.MaxRounds, 1, MaxRoundsLimit);
            existing.TimeoutSeconds = Math.Clamp(team.TimeoutSeconds, 30, MaxTimeoutSeconds);
            existing.MaxRetries = Math.Clamp(team.MaxRetries, 0, MaxRetriesLimit);
            existing.FailureStrategy = team.FailureStrategy;
            existing.UpdatedAt = now;
            SaveAll(workspace, teams);
            return teams;
        }
    }

    public IReadOnlyList<TeamDefinition> Delete(WorkspaceInfo workspace, string id)
    {
        EnsureProject(workspace);
        lock (Gate)
        {
            var teams = Load(workspace).ToList();
            if (teams.RemoveAll(team => team.Id.Equals(id, StringComparison.Ordinal)) == 0)
                throw new Daemon.DaemonRequestException($"No team with id \"{id}\" in this workspace.");
            SaveAll(workspace, teams);
            return teams;
        }
    }

    public (TeamDefinition Team, IReadOnlyList<TeamDefinition> Teams) Copy(
        WorkspaceInfo workspace, string id, string? name)
    {
        EnsureProject(workspace);
        lock (Gate)
        {
            var teams = Load(workspace).ToList();
            var source = teams.FirstOrDefault(team => team.Id.Equals(id, StringComparison.Ordinal))
                ?? throw new Daemon.DaemonRequestException($"No team with id \"{id}\" in this workspace.");
            var now = DateTimeOffset.UtcNow;
            var copy = new TeamDefinition
            {
                Id = $"team-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
                Name = string.IsNullOrWhiteSpace(name) ? $"{source.Name}（副本）" : name!.Trim(),
                Description = source.Description,
                Mode = source.Mode,
                CoordinatorId = source.CoordinatorId,
                Members = NormalizeMembers(source.Members.Select(member => new TeamMember
                {
                    Id = member.Id,
                    Name = member.Name,
                    Role = member.Role,
                    Responsibility = member.Responsibility,
                    Model = member.Model,
                    Tools = [.. member.Tools],
                    ExpertId = member.ExpertId,
                    Enabled = member.Enabled,
                }).ToList()),
                Enabled = source.Enabled,
                MaxRounds = source.MaxRounds,
                TimeoutSeconds = source.TimeoutSeconds,
                MaxRetries = source.MaxRetries,
                FailureStrategy = source.FailureStrategy,
                CreatedAt = now,
                UpdatedAt = now,
            };
            teams.Add(copy);
            SaveAll(workspace, teams);
            return (copy, teams);
        }
    }

    public static void Validate(TeamDefinition team)
    {
        if (string.IsNullOrWhiteSpace(team.Name))
            throw new Daemon.DaemonRequestException("团体名称不能为空");
        if (team.Mode is not ("sequential" or "parallel" or "supervisor" or "review"))
            throw new Daemon.DaemonRequestException($"不支持的协作模式「{team.Mode}」");
        if (team.FailureStrategy is not ("stop" or "continue" or "fallback"))
            throw new Daemon.DaemonRequestException($"不支持的失败策略「{team.FailureStrategy}」");
        if (team.MaxRounds is < 1 or > MaxRoundsLimit)
            throw new Daemon.DaemonRequestException($"轮次上限必须在 1..{MaxRoundsLimit} 之间");
        if (team.TimeoutSeconds is < 30 or > MaxTimeoutSeconds)
            throw new Daemon.DaemonRequestException($"超时必须在 30..{MaxTimeoutSeconds} 秒之间");
        if (team.MaxRetries is < 0 or > MaxRetriesLimit)
            throw new Daemon.DaemonRequestException($"重试次数必须在 0..{MaxRetriesLimit} 之间");
        var members = NormalizeMembers(team.Members);
        if (members.Count == 0)
            throw new Daemon.DaemonRequestException("团体至少需要一个启用成员");
        if (team.Mode is "supervisor" or "review")
        {
            if (string.IsNullOrWhiteSpace(team.CoordinatorId))
                throw new Daemon.DaemonRequestException($"{team.Mode} 模式必须指定协调者");
            if (members.All(member => !member.Id.Equals(team.CoordinatorId, StringComparison.Ordinal)))
                throw new Daemon.DaemonRequestException("协调者必须是启用成员之一");
            if (team.Mode == "review" && members.Count < 2)
                throw new Daemon.DaemonRequestException("评审制至少需要执行者与审核者两个成员");
        }
    }

    private static List<TeamMember> NormalizeMembers(List<TeamMember> members)
    {
        var result = new List<TeamMember>();
        foreach (var member in members ?? [])
        {
            if (string.IsNullOrWhiteSpace(member.Name) && string.IsNullOrWhiteSpace(member.Role))
                continue;
            member.Id = string.IsNullOrWhiteSpace(member.Id)
                ? $"member-{Guid.NewGuid().ToString("N")[..8]}"
                : member.Id.Trim();
            member.Name = string.IsNullOrWhiteSpace(member.Name) ? member.Role.Trim() : member.Name.Trim();
            member.Role = member.Role?.Trim() ?? "";
            member.Responsibility = member.Responsibility?.Trim() ?? "";
            member.Tools = [.. (member.Tools ?? []).Select(tool => tool.Trim())
                .Where(tool => tool.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
            result.Add(member);
        }
        return result;
    }

    private static void EnsureProject(WorkspaceInfo workspace)
    {
        if (workspace.IsGlobal)
            throw new Daemon.DaemonRequestException("智能体团体只支持项目工作区，不支持全局范围。");
    }

    private static List<TeamDefinition> Load(WorkspaceInfo workspace)
    {
        var file = FileFor(workspace);
        if (!File.Exists(file)) return [];
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(file), HaoyueJsonContext.Default.ListTeamDefinition) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void SaveAll(WorkspaceInfo workspace, List<TeamDefinition> teams)
    {
        var file = FileFor(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var json = JsonSerializer.Serialize(teams, HaoyueJsonContext.Default.ListTeamDefinition);
        var temp = file + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, file, overwrite: true);
    }
}
