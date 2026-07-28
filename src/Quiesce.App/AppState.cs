using System.IO;
using Quiesce.Core.Catalog;
using Quiesce.Core.Engine;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.App;

/// <summary>
/// Everything the read-only M2 shell needs, loaded once at startup: machine state, catalog, and a
/// live plan (which doubles as per-entry status via no-op elision).
/// </summary>
/// <remarks>
/// M2 has no mutation path at all — the pages render reality and nothing more. Engage/Restore
/// arrive in M3 wired through the same <see cref="TransactionEngine"/> the CLI uses.
/// </remarks>
public sealed record AppState
{
    public required QuiesceState MachineState { get; init; }

    public required string DataRoot { get; init; }

    public CatalogFile? Catalog { get; init; }

    public string? CatalogPath { get; init; }

    public EngagePlan? Plan { get; init; }

    /// <summary>
    /// True when whether the machine is modified could not be determined.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>MachineState.IsDirty == false</c> on purpose. "Not dirty" and "no idea" must never
    /// render the same, and Engage must be refused in the second case: engaging over an already-engaged
    /// machine captures the first session's tweaks as if they were the user's original settings.
    /// </remarks>
    public bool StateUnknown { get; init; }

    public string? LoadError { get; init; }

    public static AppState Load()
    {
        var paths = new QuiescePaths();

        // The GUI is requireAdministrator, so this read succeeds in practice. Handled anyway rather than
        // left to crash the window on startup: if it ever cannot be read, the app must say so instead of
        // rendering a confident "clean" over an engaged machine.
        QuiesceState state;
        try
        {
            state = new StateStore(paths.DataRoot).Load();
        }
        catch (StateUnreadableException ex)
        {
            return new AppState
            {
                MachineState = new QuiesceState(),
                DataRoot = paths.DataRoot,
                StateUnknown = true,
                LoadError = ex.Message,
            };
        }

        var imagePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var catalogPath = CatalogLocator.TryLocate(imagePath);

        if (catalogPath is null)
        {
            return new AppState
            {
                MachineState = state,
                DataRoot = paths.DataRoot,
                LoadError = "No catalog found. Tweak status is unavailable; restore still works from the CLI.",
            };
        }

        try
        {
            var catalog = CatalogLoader.LoadFile(catalogPath);
            var engine = CreateEngine();

            return new AppState
            {
                MachineState = state,
                DataRoot = paths.DataRoot,
                Catalog = catalog,
                CatalogPath = catalogPath,
                Plan = engine.Plan(catalog, "default", new ProfileStore(paths.DataRoot).ActiveEnabled()),
            };
        }
        catch (Exception ex) when (ex is CatalogException or IOException or UnauthorizedAccessException)
        {
            return new AppState
            {
                MachineState = state,
                DataRoot = paths.DataRoot,
                CatalogPath = catalogPath,
                LoadError = ex.Message,
            };
        }
    }

    /// <summary>
    /// Builds the engine the GUI mutates through — the same <see cref="TransactionEngine"/> the CLI
    /// uses, so both paths share one tested implementation of apply and revert.
    /// </summary>
    public static TransactionEngine CreateEngine()
    {
        var broadcaster = new Win32ActivationBroadcaster();
        var services = new Win32ServiceControl();
        var processes = new Win32ProcessControl();

        return new TransactionEngine(
            new Win32Registry(),
            broadcaster,
            new QuiescePaths(),
            new EngineInfo
            {
                AppVersion = AppVersion(),
                OsBuild = $"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}.{Environment.OSVersion.Version.Build}",
                UserSid = QuiescePaths.CurrentUserSid(),
            },
            broadcaster,
            services,
            processes,
            // ForMachine, never the bare constructor: it is what resolves the images of Quiesce and of
            // whatever launched it, without which the app could close or throttle its own host process.
            Quiesce.Core.ProcessClassifier.ForMachine(
                processes,
                gameDirectories: null,
                serviceHostPids: services.ServiceHostProcessIds()));
    }

    public static string AppVersion() =>
        typeof(AppState).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute attr, ..]
            ? attr.InformationalVersion
            : "unknown";
}
