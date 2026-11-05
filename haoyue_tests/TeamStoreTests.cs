using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Daemon;
using Haoyue.Runtime.Teams;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

public sealed class TeamStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "haoyue-teams-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private WorkspaceInfo Workspace(string? root = null, bool isGlobal = false) => new()
    {
        Root = root ?? _root,
        ProjectKinds = [],
        Config = new WorkspaceConfig(),
        IsGlobal = isGlobal,
    };

    private static TeamDefinition Team(string name = "发布小队", string mode = "sequential") => new()
    {
        Name = name,
        Description = "负责发布前检查",
        Mode = mode,
        MaxRounds = 3,
        TimeoutSeconds = 300,
        MaxRetries = 1,
        FailureStrategy = "stop",
        Members =
        [
            new TeamMember { Name = "架构师", Role = "架构师", Responsibility = "评估风险" },
            new TeamMember { Name = "实现者", Role = "实现者", Responsibility = "落地修改" },
        ],
    };

    [Fact]
    public void Save_CreatesFileAndListReturnsNormalizedTeam()
    {
        var teams = new TeamStore().Save(Workspace(), Team());

        var team = Assert.Single(teams);
        Assert.StartsWith("team-", team.Id);
        Assert.Equal("发布小队", team.Name);
        Assert.Equal(2, team.Members.Count);
        Assert.All(team.Members, member => Assert.StartsWith("member-", member.Id));
        Assert.True(File.Exists(TeamStore.FileFor(Workspace())));
    }

    [Fact]
    public void Save_ById_UpdatesInsteadOfDuplicating()
    {
        var store = new TeamStore();
        var created = store.Save(Workspace(), Team()).Single();

        var update = Team("发布小队（改）");
        update.Id = created.Id;
        var teams = store.Save(Workspace(), update);

        var team = Assert.Single(teams);
        Assert.Equal(created.Id, team.Id);
        Assert.Equal(created.CreatedAt, team.CreatedAt);
        Assert.Equal("发布小队（改）", team.Name);
    }

    [Fact]
    public void Save_RejectsInvalidConfiguration()
    {
        var store = new TeamStore();
        Assert.Throws<DaemonRequestException>(() => store.Save(Workspace(), new TeamDefinition
        {
            Name = "",
            Mode = "sequential",
            FailureStrategy = "stop",
            MaxRounds = 3,
            TimeoutSeconds = 300,
        }));
        Assert.Throws<DaemonRequestException>(() => store.Save(Workspace(), Team(mode: "round-robin")));
        Assert.Throws<DaemonRequestException>(() => store.Save(Workspace(), Team(mode: "supervisor")));
        Assert.Throws<DaemonRequestException>(() => store.Save(Workspace(), Team(mode: "review")));
    }

    [Fact]
    public void Save_Supervisor_RequiresEnabledCoordinator()
    {
        var store = new TeamStore();
        var saved = store.Save(Workspace(), Team()).Single();

        var supervisor = Team(mode: "supervisor");
        supervisor.Id = saved.Id;
        // 复用已落盘的成员 id，否则重新保存时会生成新 id，协调者引用失效。
        supervisor.Members = saved.Members;
        Assert.Throws<DaemonRequestException>(() => store.Save(Workspace(), supervisor));

        supervisor.CoordinatorId = saved.Members[0].Id;
        var stored = Assert.Single(store.Save(Workspace(), supervisor));
        Assert.Equal(saved.Members[0].Id, stored.CoordinatorId);
        Assert.Equal("supervisor", stored.Mode);
    }

    [Fact]
    public void Delete_RemovesTeam_MissingIdThrows()
    {
        var store = new TeamStore();
        var saved = store.Save(Workspace(), Team()).Single();

        Assert.Empty(store.Delete(Workspace(), saved.Id));
        Assert.Throws<DaemonRequestException>(() => store.Delete(Workspace(), saved.Id));
    }

    [Fact]
    public void Copy_ProducesIndependentTeam()
    {
        var store = new TeamStore();
        var saved = store.Save(Workspace(), Team()).Single();

        var (copy, teams) = store.Copy(Workspace(), saved.Id, null);

        Assert.NotEqual(saved.Id, copy.Id);
        Assert.Equal("发布小队（副本）", copy.Name);
        Assert.Equal(2, teams.Count);
        // 成员 id 保留，协调者引用在副本里仍然有效；成员对象本身互不影响。
        Assert.Equal(saved.Members[0].Id, copy.Members[0].Id);
        copy.Members[0].Responsibility = "改了副本";
        Assert.Equal("评估风险", saved.Members[0].Responsibility);
    }

    [Fact]
    public void List_IsWorkspaceScoped_AndHotReloads()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var store = new TeamStore();
        store.Save(Workspace(), Team("A"));
        store.Save(Workspace(other), Team("B"));

        Assert.Equal("A", new TeamStore().List(Workspace()).Single().Name);
        Assert.Equal("B", new TeamStore().List(Workspace(other)).Single().Name);
    }

    [Fact]
    public void GlobalWorkspace_IsRejected()
    {
        var store = new TeamStore();
        Assert.Throws<DaemonRequestException>(() => store.List(Workspace(isGlobal: true)));
        Assert.Throws<DaemonRequestException>(() => store.Save(Workspace(isGlobal: true), Team()));
    }
}
