using System.IO;
using Quiesce.Core;
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

    /// <summary>The plan for what Engage would actually do: the active profile only.</summary>
    public EngagePlan? Plan { get; init; }

    /// <summary>
    /// A plan over <em>every</em> catalog entry, enabled or not. Status display only — never applied.
    /// </summary>
    /// <remarks>
    /// Exists because the Features page has to describe rows that are switched off, and the profile-filtered
    /// plan contains no steps for those. Without this, a disabled row could only be described as "off",
    /// with no way to say whether it is already at its lean value or would be refused by Windows — and
    /// after a bulk enable, every newly-on row would claim "will be applied on Engage" including the ones
    /// that are already lean and the ones the kernel vetoes. Read-only: planning probes the registry,
    /// queries services and enumerates processes, and mutates nothing.
    /// </remarks>
    public EngagePlan? StatusPlan { get; init; }

    /// <summary>
    /// True when whether the machine is modified could not be determined.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>MachineState.IsDirty == false</c> on purpose. "Not dirty" and "no idea" must never
    /// render the same, and Engage must be refused in the second case: engaging over an already-engaged
    /// machine captures the first session's tweaks as if they were the user's original settings.
    /// </remarks>
    public bool StateUnknown { get; init; }

    /// <summary>
    /// Entry titles waiting on a restart to take full effect. Empty when nothing is.
    /// </summary>
    /// <remarks>
    /// Titles rather than ids, resolved through the catalog, because the warning has to be actionable by
    /// someone who has never read the catalog. Ids survive as a fallback for an entry that has since been
    /// removed — a marker naming an id this build no longer ships is still a real outstanding reboot.
    /// </remarks>
    public IReadOnlyList<string> RebootPendingTitles { get; init; } = [];

    public bool RebootPending => RebootPendingTitles.Count > 0;

    /// <summary>
    /// Whether the engaged session still matches the machine. Null when nothing is engaged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null and "engaged with no drift" are different facts and must not render the same: the first means
    /// there is nothing to be out of sync with, the second means Quiesce looked and the machine matched.
    /// A <see cref="DriftReport"/> with <c>Unknown = true</c> is a third — it looked and could not tell.
    /// </para>
    /// <para>
    /// Computed on <see cref="Load"/> rather than on a timer. There is no background check in this app, on
    /// purpose: a periodic drift check would enumerate every process and query the SCM on a schedule,
    /// including while a game is fullscreen, which is the one time Quiesce should be doing nothing at all.
    /// The Dashboard has a Re-check button and the report carries the time it was taken.
    /// </para>
    /// </remarks>
    public DriftReport? Drift { get; init; }

    /// <summary>True only when Quiesce looked and found the machine changed.</summary>
    public bool Drifted => Drift is { Unknown: false, Any: true };

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

        // The one place a stale reboot marker is swept. The GUI is elevated and reads at startup, which
        // makes it the earliest thing after a restart that can write, and the marker's whole design leans
        // on being cleared promptly - see QuiesceState.RebootPending for what happens when it is not.
        // Best-effort: failing to clear an obsolete warning is not worth failing to start over.
        if (state.RebootPendingSinceUptimeMs is not null && !state.RebootPending)
        {
            try
            {
                new StateStore(paths.DataRoot).Save(state);
                state = state.WithoutStaleRebootMarker();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state = state.WithoutStaleRebootMarker();
            }
        }

        var imagePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var catalogPath = CatalogLocator.TryLocate(imagePath);

        if (catalogPath is null)
        {
            return new AppState
            {
                MachineState = state,
                DataRoot = paths.DataRoot,
                RebootPendingTitles = RebootTitles(state, catalog: null),
                LoadError = "No catalog found. Tweak status is unavailable; restore still works from the CLI.",
            };
        }

        try
        {
            var shipped = CatalogLoader.LoadFile(catalogPath);

            // A bad or unreadable user file must not take the shipped catalog down with it. The user's own
            // added apps go missing and the app says so — losing 36 working entries because one added
            // entry is malformed would be the wrong trade in a program whose main job is the undo.
            var catalog = shipped;
            string? userError = null;
            try
            {
                catalog = UserCatalogStore.Merge(shipped, new UserCatalogStore(paths.DataRoot).Load());
            }
            catch (Exception ex) when (ex is CatalogException or StateUnreadableException or IOException or UnauthorizedAccessException)
            {
                userError = $"Apps you added could not be loaded, so they are NOT in this plan: {ex.Message}";
            }

            var engine = CreateEngine();

            return new AppState
            {
                MachineState = state,
                DataRoot = paths.DataRoot,
                Catalog = catalog,
                CatalogPath = catalogPath,
                LoadError = userError,
                RebootPendingTitles = RebootTitles(state, catalog),
                Plan = engine.Plan(catalog, "default", new ProfileStore(paths.DataRoot).ActiveEnabled()),
                StatusPlan = engine.Plan(catalog, "default", enabledIds: null),
                Drift = DetectDrift(engine, state),
            };
        }
        catch (Exception ex) when (ex is CatalogException or IOException or UnauthorizedAccessException)
        {
            return new AppState
            {
                MachineState = state,
                DataRoot = paths.DataRoot,
                CatalogPath = catalogPath,
                RebootPendingTitles = RebootTitles(state, catalog: null),
                LoadError = ex.Message,
            };
        }
    }

    /// <summary>
    /// The drift report for the engaged session, or null when nothing is engaged.
    /// </summary>
    /// <remarks>
    /// Never allowed to take the window down. A drift check is a convenience on top of a machine that is
    /// already correctly described by <see cref="MachineState"/>, so a failure to compute it degrades to
    /// "not checked" rather than to a crash on startup — with one exception that matters:
    /// <see cref="DriftReport.Unknown"/> already covers an unreadable journal and is returned rather than
    /// thrown, so the only things caught here are genuinely unexpected.
    /// </remarks>
    private static DriftReport? DetectDrift(TransactionEngine engine, QuiesceState state)
    {
        if (!state.IsDirty || state.ActiveSessionId is not { } sessionId)
        {
            return null;
        }

        try
        {
            return engine.DetectDrift(sessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new DriftReport
            {
                SessionId = sessionId,
                Items = [],
                Unknown = true,
                UnknownReason = $"Quiesce could not check whether this machine still matches the session: {ex.Message}",
                AppliedBeforeLastRestart = false,
                CheckedUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    private static IReadOnlyList<string> RebootTitles(QuiesceState state, CatalogFile? catalog) =>
        !state.RebootPending
            ? []
            : [.. state.RebootPendingEntryIds.Select(id =>
                catalog?.Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Title ?? id)];

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
                serviceHostPids: services.ServiceHostProcessIds()),
            new Win32PowerControl());
    }

    /// <summary>
    /// Builds the running-app discovery, wired the same way the engine's process layer is.
    /// </summary>
    /// <remarks>
    /// <see cref="ProcessClassifier.ForMachine"/> rather than the bare constructor, for the same reason
    /// the engine uses it: without the self-protection set, the list would offer the user the application
    /// hosting Quiesce as something to close.
    /// </remarks>
    public static RunningAppDiscovery CreateDiscovery()
    {
        var processes = new Win32ProcessControl();
        var services = new Win32ServiceControl();

        return new RunningAppDiscovery(
            processes,
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
