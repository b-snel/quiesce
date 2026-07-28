using Quiesce.Core.Platform;

namespace Quiesce.Core;

/// <summary>
/// Decides what class a process belongs to, and therefore what Quiesce may do with it.
/// </summary>
/// <remarks>
/// Separate from <see cref="Guardrails"/> on purpose. Guardrails holds the hard limits as
/// compile-time constants; this applies them to live observations, and needs the game allowlist,
/// which is data discovered at runtime. Keeping the data out of Guardrails preserves the rule that
/// catalog and runtime data can only ever <em>narrow</em> what Quiesce is willing to touch.
/// </remarks>
public sealed class ProcessClassifier
{
    private readonly IReadOnlyList<string> _gameDirectories;
    private readonly IReadOnlySet<uint> _serviceHostPids;
    private readonly IReadOnlySet<int> _selfAndAncestors;
    private readonly IReadOnlySet<string> _selfHostImagePaths;

    /// <param name="gameDirectories">
    /// Directories containing discovered games, from launcher manifests. Overwatch installs to
    /// <c>C:\Program Files (x86)\Overwatch</c> — a sibling of the Battle.net root, not a child — so
    /// launcher-root matching alone misses games and per-game paths are required.
    /// </param>
    /// <param name="serviceHostPids">
    /// PIDs hosting at least one Windows service, from <see cref="IServiceControl.ServiceHostProcessIds"/>.
    /// Built once by the caller. Omitting it means service hosts are not recognised, so callers that
    /// intend to act on processes must supply it.
    /// </param>
    /// <param name="selfHostImagePaths">
    /// Images belonging to Quiesce and to whatever launched it, from
    /// <see cref="ProcessAncestry.SelfHostImagePaths"/>. Supply it via <see cref="ForMachine"/> in
    /// production; tests pass it explicitly so the host's real ancestry cannot decide a test.
    /// </param>
    public ProcessClassifier(
        IEnumerable<string>? gameDirectories = null,
        IReadOnlySet<uint>? serviceHostPids = null,
        IReadOnlySet<string>? selfHostImagePaths = null)
    {
        _gameDirectories = (gameDirectories ?? []).Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        _serviceHostPids = serviceHostPids ?? new HashSet<uint>();
        _selfAndAncestors = ProcessAncestry.SelfAndAncestors();
        _selfHostImagePaths = selfHostImagePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a classifier wired to the live machine, including the self-protection set.
    /// </summary>
    /// <remarks>
    /// The reason this factory exists: the self-protection set needs a process enumeration to resolve
    /// ancestor image paths, and a constructor that silently skipped it would leave callers
    /// unprotected by default. Making the safe construction the convenient one is the point.
    /// </remarks>
    public static ProcessClassifier ForMachine(
        IProcessControl processes,
        IEnumerable<string>? gameDirectories = null,
        IReadOnlySet<uint>? serviceHostPids = null)
    {
        ArgumentNullException.ThrowIfNull(processes);

        return new ProcessClassifier(
            gameDirectories,
            serviceHostPids,
            ProcessAncestry.SelfHostImagePaths(processes));
    }

    /// <summary>
    /// Classifies one process. Ordered most-protective first, so the class a process lands in is the
    /// most restrictive one that applies rather than whichever test happened to run.
    /// </summary>
    public ProcessClass Classify(ProcessSnapshot process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // FIRST, ahead of everything: Quiesce itself and whatever launched it. Closing your own
        // launcher kills the process driving the apply and strands the journal; throttling it starves
        // the apply with the apply. This also means the app hosting a development run is protected
        // automatically, without a hard-coded list of names to spare — and in production, where
        // Explorer is the ancestor instead, the same rule leaves ordinary applications closable.
        // PID match catches self and the direct chain. Image match catches the rest of the host's
        // family, which is where the real exposure was: measured on the development machine, only 2 of
        // the host application's 14 processes were ancestors — a Chromium-style app puts its renderers
        // and helpers beside the spawning process, not above it, so the other 12 read as ordinary and
        // would have been throttled. Breaking 12 of 14 processes breaks the application.
        if (_selfAndAncestors.Contains(process.Identity.Pid)
            || (process.ImagePath is { } path && _selfHostImagePaths.Contains(path)))
        {
            return ProcessClass.SelfOrLauncherOfSelf;
        }

        // Name-based, and correctly so: these are protected precisely because of what they ARE, and
        // the check must hold even when the path cannot be read. A process claiming to be csrss is
        // either the real one or something Quiesce should be nowhere near.
        if (Guardrails.IsProcessProtected(process.ImageName))
        {
            return ProcessClass.NeverTouch;
        }

        // Before the path checks, and deliberately not dependent on a readable path: a service host
        // must be recognised as one whether or not its image can be inspected. This is the check that
        // stops the process layer from reaching around every service guardrail — all of which are
        // keyed on service names and cannot see a request aimed at a PID.
        if (process.Identity.Pid > 0 && _serviceHostPids.Contains((uint)process.Identity.Pid))
        {
            return ProcessClass.ServiceHost;
        }

        // Everything below needs a path. An unreadable path resolves to NeverTouch, never to a
        // name-based guess: "something called chrome.exe, location unknown" is exactly the case
        // name matching gets wrong, and the cost of being wrong is closing a program the user did
        // not ask Quiesce to close. Protected and other-user processes deny the query routinely, so
        // this is a common path and not an error.
        if (string.IsNullOrWhiteSpace(process.ImagePath))
        {
            return ProcessClass.NeverTouch;
        }

        // No creation time means no recycling-proof identity, so nothing could be journalled against
        // this process and revert could not verify it is still the same one. Untouchable by
        // construction rather than by policy.
        if (!process.CreationTimeKnown)
        {
            return ProcessClass.NeverTouch;
        }

        if (IsUnderAnyGameDirectory(process.ImagePath))
        {
            return ProcessClass.Game;
        }

        if (Guardrails.IsUnderLauncherRoot(process.ImagePath))
        {
            return ProcessClass.LauncherOrAntiCheat;
        }

        // Checked AFTER the launcher and game roots. Several launchers ship an embedded Chromium
        // named chrome.exe inside their own install tree — the Epic and Battle.net panes both do —
        // and closing those as "a browser" tears the launcher's UI out from under a running game.
        if (Guardrails.IsBrowser(process.ImageName))
        {
            return ProcessClass.Browser;
        }

        return ProcessClass.Ordinary;
    }

    private bool IsUnderAnyGameDirectory(string imagePath)
    {
        foreach (var dir in _gameDirectories)
        {
            var normalized = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Compared as a path prefix followed by a separator, not as a substring: a bare
            // Contains would make "C:\Games\Doom" match "C:\Games\DoomLauncherStuff\x.exe", and
            // would also match the directory name appearing anywhere later in the path.
            if (imagePath.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
