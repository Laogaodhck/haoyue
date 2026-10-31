using System.Text.Json;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Tools;

namespace Haoyue.Runtime.Workspaces;

/// <summary>
/// 用户显式放行的工作区清单。工作区里的 <c>.haoyue/config.json</c> 与
/// <c>mcp/servers.json</c> 可以携带自动执行的命令（MCP stdio、verify 命令），
/// 因此未受信工作区的这些配置在加载时被整体忽略——克隆来的仓库不能靠
/// 「打开一次」就取得执行权。
/// </summary>
public interface IWorkspaceTrustStore
{
    bool IsTrusted(string root);

    IReadOnlyList<string> Trusted { get; }

    void Trust(string root);

    void Untrust(string root);
}

public sealed class WorkspaceTrustStore : IWorkspaceTrustStore
{
    private readonly string _file;
    private readonly Lock _gate = new();

    public WorkspaceTrustStore() : this(Path.Combine(HaoyuePaths.Home, "trusted-workspaces.json"))
    {
    }

    internal WorkspaceTrustStore(string file) => _file = file;

    public IReadOnlyList<string> Trusted
    {
        get { lock (_gate) return Load(); }
    }

    public bool IsTrusted(string root)
    {
        var real = Canonical(root);
        lock (_gate)
            return Load().Any(entry => SamePath(entry, real));
    }

    public void Trust(string root)
    {
        var real = Canonical(root);
        lock (_gate)
        {
            var entries = Load();
            if (entries.Any(entry => SamePath(entry, real))) return;
            entries.Add(real);
            Save(entries);
        }
    }

    public void Untrust(string root)
    {
        var real = Canonical(root);
        lock (_gate)
        {
            var entries = Load();
            if (entries.RemoveAll(entry => SamePath(entry, real)) == 0) return;
            Save(entries);
        }
    }

    private static string Canonical(string root) =>
        PathGuard.RealPath(root) ?? Path.GetFullPath(root);

    private static bool SamePath(string left, string right) =>
        string.Equals(
            PathGuard.Trim(left),
            PathGuard.Trim(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private List<string> Load()
    {
        try
        {
            if (!File.Exists(_file)) return [];
            var text = File.ReadAllText(_file);
            return JsonSerializer.Deserialize(text, HaoyueJsonContext.Default.ListString) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 损坏的信任清单视为「没有受信工作区」：失败方向必须是收紧，而不是放行。
            return [];
        }
    }

    private void Save(List<string> entries)
    {
        var directory = Path.GetDirectoryName(_file);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, HaoyueJsonContext.Default.ListString));
        File.Move(temp, _file, overwrite: true);
    }
}
