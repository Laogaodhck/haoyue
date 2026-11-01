using System.Security.Cryptography;
using System.Text;

namespace haoyue_webserver.Security;

/// <summary>
/// One-time token that gates the first-run setup endpoint. Without it, whoever reaches
/// a freshly deployed instance before the operator could create the SuperAdmin account.
/// </summary>
public sealed class SetupTokenProvider
{
    public const string ConfigKey = "HAOYUE_SETUP_TOKEN";

    public string Token { get; }
    public bool IsConfigured { get; }

    public SetupTokenProvider(IConfiguration configuration)
    {
        var configured = configuration[ConfigKey] ?? Environment.GetEnvironmentVariable(ConfigKey);
        IsConfigured = !string.IsNullOrWhiteSpace(configured);
        Token = IsConfigured
            ? configured!.Trim()
            : Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public bool IsValid(string? provided)
    {
        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(Token);
        var actual = Encoding.UTF8.GetBytes(provided.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public void Announce(ILogger logger)
    {
        if (IsConfigured)
        {
            logger.LogInformation("Haoyue 初始化令牌已从 {Key} 读取。", ConfigKey);
            return;
        }

        logger.LogWarning(
            "Haoyue 尚未初始化。本次进程的初始化令牌：{SetupToken}（仅用于 /setup 页面，进程重启后失效）",
            Token);
    }
}
