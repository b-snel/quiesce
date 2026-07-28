using System.Security.AccessControl;
using System.Security.Principal;

namespace Quiesce.Core.Platform;

/// <summary>Resolves Quiesce's on-disk locations and enforces the ACL preflight.</summary>
public sealed class QuiescePaths
{
    /// <summary>Environment override for tests and development. Takes precedence when set.</summary>
    public const string DataRootEnvVar = "QUIESCE_DATA_ROOT";

    public QuiescePaths(string? dataRoot = null)
    {
        var explicitRoot = dataRoot ?? Environment.GetEnvironmentVariable(DataRootEnvVar);

        DefaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Quiesce");

        DataRoot = explicitRoot ?? DefaultRoot;
        IsDefaultRoot = explicitRoot is null;
    }

    public string DataRoot { get; }

    public string DefaultRoot { get; }

    /// <summary>
    /// False when the root came from <see cref="DataRootEnvVar"/> or an explicit constructor
    /// argument, i.e. a development or test root. Real installs always use the default.
    /// </summary>
    public bool IsDefaultRoot { get; }

    public string JournalRoot => Path.Combine(DataRoot, "journal");

    public string SessionDir(Guid sessionId) => Path.Combine(JournalRoot, sessionId.ToString("D"));

    /// <summary>
    /// A value that changes on every boot, recorded in <c>sessionStart</c> so recovery can tell
    /// "dirty in this boot" from "dirty and rebooted since". Derived from boot time truncated to
    /// the second — precise enough to distinguish boots, and needs no WMI.
    /// </summary>
    public static string CurrentBootId()
    {
        var bootUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return bootUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>SID of the user this process runs as. Recorded on every per-user registry op.</summary>
    public static string CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("Current process token has no user SID.");

    /// <summary>
    /// The ACL preflight. An elevated process reading a catalog — or executing a revert plan —
    /// from a path a standard user can write is an arbitrary-registry-write-as-Administrator
    /// primitive, so installed builds refuse to run when their paths are non-admin-writable.
    /// </summary>
    /// <returns>Human-readable findings; empty means clean.</returns>
    /// <remarks>
    /// Development builds (running from a user-profile bin directory) get findings reported as
    /// loud warnings instead of a refusal — otherwise nothing could be developed at all. The
    /// installed/dev distinction is made on the image path, not on a flag an attacker could set.
    /// </remarks>
    public static IReadOnlyList<string> AuditWritableByNonAdmins(params string[] paths)
    {
        var findings = new List<string>();

        // Well-known SIDs that legitimately hold write on protected paths.
        var trusted = new HashSet<string>
        {
            "S-1-5-18",     // SYSTEM
            "S-1-5-32-544", // BUILTIN\Administrators
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
        };

        foreach (var path in paths)
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                continue;
            }

            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();

            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                {
                    continue;
                }

                const FileSystemRights writeRights =
                    FileSystemRights.WriteData | FileSystemRights.AppendData |
                    FileSystemRights.WriteAttributes | FileSystemRights.Delete |
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

                if ((rule.FileSystemRights & writeRights) == 0)
                {
                    continue;
                }

                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (trusted.Contains(sid.Value))
                {
                    continue;
                }

                // The current user having write on their own dev tree is the expected dev case;
                // it still gets reported so the caller can decide warn-vs-refuse.
                findings.Add($"{path}: '{TranslateSid(sid)}' holds write access ({rule.FileSystemRights}).");
            }
        }

        return findings;
    }

    /// <summary>
    /// Creates the data root with inheritance broken and non-admin write access removed.
    /// </summary>
    /// <remarks>
    /// <c>C:\ProgramData</c> carries <c>BUILTIN\Users : Write : ContainerInherit</c>, so a
    /// default-inherited subdirectory is standard-user writable. Since an elevated Quiesce executes
    /// the revert plan found in that directory, leaving the inherited ACE would let any code running
    /// as the user rewrite the journal and have it applied as Administrator. Called on first use so
    /// the protection exists even when the installer has not run (portable use).
    /// <para>
    /// Applies only to <see cref="IsDefaultRoot"/>. A dev/test root under %TEMP% protects nothing
    /// (its parent is already user-writable) and locking it to Administrators would simply prevent
    /// an unelevated test run from using it. The runtime gate for both cases is the ACL preflight,
    /// which refuses to run an installed build whose paths are non-admin-writable.
    /// </para>
    /// </remarks>
    public void EnsureDataRootHardened()
    {
        var created = !Directory.Exists(DataRoot);
        var dir = Directory.CreateDirectory(DataRoot);

        if (!created || !IsDefaultRoot)
        {
            return;
        }

        var security = dir.GetAccessControl();

        // Detach from ProgramData's inheritable ACEs without copying them in.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in new[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        dir.SetAccessControl(security);
    }

    /// <summary>True when this binary runs from a location standard users cannot modify.</summary>
    public static bool IsInstalledLocation(string imagePath)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return imagePath.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string TranslateSid(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
