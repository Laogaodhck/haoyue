using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Tools;

/// <summary>
/// Shared path boundary for the file tools. The workspace root is the trust boundary:
/// inside it reads are unrestricted, outside it a read needs an explicit reason —
/// either a directory the user whitelisted (<c>agent.extraReadableRoots</c>) or, for
/// directory-free global sessions, the user profile, which is where their own files live.
/// The profile fallback never reaches credential material.
/// </summary>
internal static class PathGuard
{
    /// <summary>Directories whose contents are key material or browser session stores.</summary>
    private static readonly string[] CredentialDirs =
        [".ssh", ".gnupg", ".aws", ".azure", ".gcloud", ".docker", ".kube", "User Data"];

    private static readonly string[] CredentialFileNames =
    [
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "secrets.json", "credentials",
        ".netrc", "_netrc", "ntuser.dat", "sam", "hosts.yml", "Login Data", "cookies.sqlite",
    ];

    private static readonly string[] CredentialExtensions = [".pem", ".p12", ".pfx", ".key"];

    internal static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Resolves every existing link (symlink, Windows junction) along the path, then
    /// appends the not-yet-existing tail. A logical path inside the workspace can point
    /// outside it through a link, so boundary checks must compare real targets.
    /// Returns null when a link chain cannot be resolved (broken link / IO error):
    /// callers treat that as "not inside" (fail closed) rather than trusting the
    /// logical spelling.
    /// </summary>
    internal static string? RealPath(string path)
    {
        try
        {
            var full = Trim(Path.GetFullPath(path));
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return full;

            var current = root;
            var segments = full[root.Length..]
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                var next = Path.Combine(current, segment);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (info.LinkTarget is not null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true)
                        ?? throw new IOException($"Unresolvable link: {next}");
                    next = target.FullName;
                }
                current = Path.GetFullPath(next);
            }
            return Trim(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = RealPath(root);
        var normalizedCandidate = RealPath(candidate);
        if (normalizedRoot is null || normalizedCandidate is null) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedCandidate.Equals(normalizedRoot, comparison)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>True for key material, token stores and browser session data.</summary>
    internal static bool IsCredentialPath(string path)
    {
        var resolved = RealPath(path) ?? Path.GetFullPath(path);
        var segments = resolved.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fileName = segments[^1];
        if (CredentialFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)
            || fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || CredentialExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
            return true;

        // Only a parent segment counts: the profile directory itself is not a credential.
        for (var i = 0; i < segments.Length - 1; i++)
            if (CredentialDirs.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Decides whether a resolved absolute path may be read. Explicit whitelist roots win
    /// over the credential filter — the user named that directory; the implicit profile
    /// fallback of a global session does not.
    /// </summary>
    internal static bool IsReadable(WorkspaceInfo workspace, AgentConfig agent, string resolved, out string? reason)
    {
        reason = null;
        // The workspace state directory always counts; relative paths of a global session
        // resolve against the process cwd, so that counts too.
        if (IsInside(workspace.Root, resolved) || IsInside(Directory.GetCurrentDirectory(), resolved)) return true;

        foreach (var extra in agent.ExtraReadableRoots.Where(entry => !string.IsNullOrWhiteSpace(entry)))
            if (IsInside(extra, resolved)) return true;

        // A directory-free global session has no project boundary; the user's own profile is
        // the working area, but it must not reach key material or browser session stores.
        if (workspace.IsGlobal && !IsCredentialPath(resolved)
            && (IsInside(HaoyuePaths.Home, resolved)
                || IsInside(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), resolved)))
            return true;

        reason = IsCredentialPath(resolved)
            ? $"Refusing to read {resolved}: it is credential material (key, token store or browser profile). "
              + "Ask the user to paste the specific value they want you to see instead."
            : $"Path escapes the workspace: {resolved}. Reads are restricted to the workspace root. "
              + "Ask the user to add the containing directory to agent.extraReadableRoots, or run it through the bash tool where the audit trail applies.";
        return false;
    }

    internal static string WorkspaceRoot(WorkspaceInfo workspace) =>
        Path.GetFullPath(workspace.IsGlobal ? Directory.GetCurrentDirectory() : workspace.Root);
}
