namespace Haoyue.Runtime.Teams;

/// <summary>团体中的一个成员：角色定位、职责、模型与工具白名单。</summary>
public sealed class TeamMember
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>角色定位（如：架构师、实现者、审核者）。</summary>
    public string Role { get; set; } = "";
    /// <summary>职责说明，会拼进成员任务提示词。</summary>
    public string Responsibility { get; set; } = "";
    /// <summary>可用模型（provider/model 或别名）；空 = 继承当前会话模型。</summary>
    public string? Model { get; set; }
    /// <summary>工具权限范围（工具名白名单）；空 = 不限制。</summary>
    public List<string> Tools { get; set; } = [];
    /// <summary>关联的既有专家/子智能体 id。</summary>
    public string? ExpertId { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>智能体团体定义：协作模式、成员与运行上限。</summary>
public sealed class TeamDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>协作模式：sequential | parallel | supervisor | review。</summary>
    public string Mode { get; set; } = "sequential";
    /// <summary>协调者成员 id（supervisor / review 必填）。</summary>
    public string? CoordinatorId { get; set; }
    public List<TeamMember> Members { get; set; } = [];
    public bool Enabled { get; set; } = true;
    /// <summary>最大轮次 1..10，防止无限循环。</summary>
    public int MaxRounds { get; set; } = 3;
    /// <summary>整体超时秒数 30..1800。</summary>
    public int TimeoutSeconds { get; set; } = 300;
    /// <summary>单成员失败重试次数 0..3。</summary>
    public int MaxRetries { get; set; } = 1;
    /// <summary>失败策略：stop | continue | fallback。</summary>
    public string FailureStrategy { get; set; } = "stop";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>一次团体运行中单个成员的轨迹。</summary>
public sealed record TeamRunMember(
    string MemberId,
    string Role,
    string Status,
    long DurationMs,
    int ToolCalls,
    string? Error,
    string OutputPreview);

/// <summary>一次团体运行的可观测快照。</summary>
public sealed record TeamRun(
    string RunId,
    string TeamId,
    string SessionId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int Rounds,
    List<TeamRunMember> Members,
    string? Result,
    string? Error,
    string WorkspaceRoot);
