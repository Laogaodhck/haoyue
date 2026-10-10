using System.Runtime.InteropServices;

namespace Haoyue.Runtime.ComputerUse.SystemControl;

/// <summary>
/// OS-family detection shared by Computer Use tools. The model should never have to
/// guess which system commands are valid: shells, process tools, and paths all key
/// off this classification, and <see cref="OsLabel"/> is safe to embed in tool output
/// so the LLM can pick command dialects precisely.
/// </summary>
public static class SystemEnvironment
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>"Windows" / "Linux" / "macOS" — one word, stable, embeddable in output.</summary>
    public static string OsLabel =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : "Linux";

    /// <summary>The kind value computer_exec resolves when the model does not specify one:
    /// PowerShell on Windows, bash on POSIX systems.</summary>
    public static string DefaultShellKind => IsWindows ? "powershell" : "bash";
}
