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

        // Merged with the apps the user added, which live in the data root and are validated by the same
        // loader. A failure here is deliberately loud rather than degraded: the CLI is the path used when
        // something is already wrong, and silently planning without the user's own entries would make
        // `print-plan` disagree with the GUI for reasons neither would explain.
        return UserCatalogStore.Merge(
            CatalogLoader.LoadFile(path),
            new UserCatalogStore(Paths.DataRoot).Load());
    }

    public TransactionEngine CreateEngine()
    {
        var broadcaster = new Win32ActivationBroadcaster();
        var services = new Win32ServiceControl();
        var processes = new Win32ProcessControl();

        return new TransactionEngine(
            new Win32Registry(),
            broadcaster,
            Paths,
            new EngineInfo
            {
                AppVersion = VersionInfo.Informational,
                OsBuild = OsBuild(),
                UserSid = QuiescePaths.CurrentUserSid(),
            },
            broadcaster,
            services,
            processes,
            BuildProcessClassifier(processes, services));
    }

    /// <summary>
    /// Builds the classifier the process layer is gated on.
    /// </summary>
    /// <remarks>
    /// Always through <see cref="ProcessClassifier.ForMachine"/>, never the bare constructor: the factory
    /// is what resolves the images of Quiesce and of whatever launched it, and a classifier missing that
    /// set would let the app close or throttle its own host. Service host PIDs come from one SCM
    /// enumeration rather than one per process.
    /// <para>
    /// <c>gameDirectories</c> is null because game discovery is not wired up yet — the <c>discover</c>
    /// verb still returns "not implemented". The consequence is specific and worth stating rather than
    /// leaving to be found: with no allowlist, a running game classifies as Ordinary, so the
    /// "change nothing while a game is live" guard in the closer and the throttler cannot fire. It does
    /// not put games at risk of being closed — the catalog names the applications it acts on, and no game
    /// is among them — but the guard is inert until discovery lands, and code that looks protected while
    /// being inert is worse than code that admits it.
    /// </para>
    /// </remarks>
    internal static Core.ProcessClassifier BuildProcessClassifier(
        IProcessControl processes,
        IServiceControl services) =>
        Core.ProcessClassifier.ForMachine(
            processes,
            gameDirectories: null,
            serviceHostPids: services.ServiceHostProcessIds());

    /// <summary>Delegates to the Core check so there is exactly one definition of "elevated".</summary>
    public static bool IsElevated() => Elevation.IsElevated();

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

    private static string? TryLocateCatalog(string imagePath) => CatalogLocator.TryLocate(imagePath);

    private static string OsBuild()
    {
        // Never ProductName — it still says "Windows 10" on Windows 11 builds.
        var version = Environment.OSVersion.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
