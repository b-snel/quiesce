using System.Security.Principal;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Platform;

namespace Quiesce.Cli;

/// <summary>Resolves the pieces every verb needs: catalog, engine, elevation and preflight state.</summary>
internal sealed class CliEnvironment
{
    public const string CatalogEnvVar = "QUIESCE_CATALOG";

    private CliEnvironment(QuiescePaths paths, string imagePath, bool devMode)
    {
        Paths = paths;
        ImagePath = imagePath;
        DevMode = devMode;
    }

    public QuiescePaths Paths { get; }

    public string ImagePath { get; }

    /// <summary>Running from a non-installed (user-writable) location. Preflight warns, not refuses.</summary>
    public bool DevMode { get; }

    /// <summary>
    /// Resolved catalog path, or null when no catalog can be found.
    /// </summary>
    /// <remarks>
    /// Deliberately lazy and nullable. The revert verbs — <c>restore</c>, <c>revert-all</c>,
    /// <c>recover</c> — must work with the catalog deleted, moved, or from a different app version,
    /// because the journal alone describes the undo. Resolving the catalog eagerly in the shared
    /// setup path would silently reintroduce a catalog dependency into the panic button, which is
    /// the one thing that has to work when everything else is broken.
    /// </remarks>
    public string? CatalogPath => _catalogPath ??= TryLocateCatalog(ImagePath);

    private string? _catalogPath;

    public static CliEnvironment Create()
    {
        var imagePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine own image path.");

        return new CliEnvironment(new QuiescePaths(), imagePath, !QuiescePaths.IsInstalledLocation(imagePath));
    }

    /// <summary>Loads the catalog, or throws if none is present. Only the non-revert verbs call this.</summary>
    public CatalogFile LoadCatalog()
    {
        var path = CatalogPath ?? throw new FileNotFoundException(
            $"No catalog found. Set {CatalogEnvVar} or place catalog\\tweaks.json next to the executable. " +
            "(Note: restore, revert-all and recover do not need a catalog.)");

        return CatalogLoader.LoadFile(path);
    }

    public TransactionEngine CreateEngine() => new(
        new Win32Registry(),
        new Win32ActivationBroadcaster(),
        Paths,
        new EngineInfo
        {
            AppVersion = VersionInfo.Informational,
            OsBuild = OsBuild(),
            UserSid = QuiescePaths.CurrentUserSid(),
        });

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// The ACL preflight. Installed builds refuse to run when the catalog or data root is
    /// non-admin-writable (an elevated process reading attacker-writable data is an arbitrary-
    /// write-as-admin primitive). Dev builds get the findings as loud warnings — enforced refusal
    /// on a dev tree would make development impossible, and dev builds are not the attack surface
    /// the rule exists for.
    /// </summary>
    /// <returns>0 to proceed; non-zero exit code to refuse.</returns>
    public int RunAclPreflight(TextWriter stderr)
    {
        var auditPaths = new List<string> { Path.GetDirectoryName(ImagePath)!, Paths.DataRoot };
        if (CatalogPath is { } catalogPath)
        {
            auditPaths.Add(Path.GetDirectoryName(catalogPath)!);
        }

        var findings = QuiescePaths.AuditWritableByNonAdmins([.. auditPaths]);

        if (findings.Count == 0)
        {
            return 0;
        }

        if (DevMode)
        {
            stderr.WriteLine("quiesce: DEV MODE - running from a non-installed path. ACL findings (warnings here, fatal when installed):");
            foreach (var f in findings)
            {
                stderr.WriteLine($"  ! {f}");
            }

            return 0;
        }

        stderr.WriteLine("quiesce: REFUSING TO RUN - paths an elevated Quiesce trusts are writable by non-admins:");
        foreach (var f in findings)
        {
            stderr.WriteLine($"  X {f}");
        }

        stderr.WriteLine("Fix the ACLs (or reinstall) and try again.");
        return CommandRouter.ExitCode.UsageError;
    }

    /// <summary>
    /// Catalog resolution: env override, then next to the exe (installed layout), then walking up
    /// from the exe looking for <c>catalog\tweaks.json</c> (dev layout, exe deep in bin\...).
    /// Returns null rather than throwing — absence is legitimate for the revert verbs.
    /// </summary>
    private static string? TryLocateCatalog(string imagePath)
    {
        if (Environment.GetEnvironmentVariable(CatalogEnvVar) is { Length: > 0 } overridePath)
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        var dir = Path.GetDirectoryName(imagePath)!;

        for (var probe = new DirectoryInfo(dir); probe is not null; probe = probe.Parent)
        {
            var candidate = Path.Combine(probe.FullName, "catalog", "tweaks.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string OsBuild()
    {
        // Never ProductName — it still says "Windows 10" on Windows 11 builds.
        var version = Environment.OSVersion.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
