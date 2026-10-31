namespace Haoyue.Runtime.Configuration;

/// <summary>
/// Daemon 自身的接入面配置。目前只有局域网访问：默认关闭，开启后除本机管道 / socket
/// 之外额外监听 TCP，供同一局域网内的桌面端连接。局域网连接必须经过握手令牌认证
/// （且使用挑战应答，令牌本身不上线），因此配置里不保存任何凭据。
/// </summary>
public sealed class DaemonConfig
{
    public LanAccessConfig Lan { get; set; } = new();
}

public sealed class LanAccessConfig
{
    public bool Enabled { get; set; }
    /// <summary>监听地址。0.0.0.0 表示所有网卡（局域网可达），127.0.0.1 仅本机回环。</summary>
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 47615;
}
