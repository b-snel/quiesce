using Quiesce.Core.Catalog;
using Quiesce.Core.Platform;

namespace Quiesce.Core;

/// <summary>
/// One running application, grouped by the directory it is installed in, offered to the user as
/// something they could add to the catalog.
/// </summary>
/// <remarks>
/// Grouped by directory rather than by image name because that is what an application actually is on
/// disk. A Chromium-based app runs a dozen processes with the same image name beside each other, and an
/// Electron app frequently runs several different names out of one install tree; both are one thing to
/// the person looking at the taskbar, and both are matched by one directory anchor.
/// </remarks>
public sealed record AppCandidate
{
    /// <summary>Full path of the directory the executables live in.</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>
    /// The directory as a catalog op would carry it: rooted, and terminated with a separator.
    /// </summary>
    public required string DirectoryFragment { get; init; }

    /// <summary>Distinct image names seen running from this directory, without <c>.exe</c>.</summary>
    public required IReadOnlyList<string> ImageNames { get; init; }

    public required int ProcessCount { get; init; }

    /// <summary>
    /// How many of them own a top-level window.
    /// </summary>
    /// <remarks>
    /// The number that decides whether a close can work at all: <c>WM_CLOSE</c> needs a window, and
    /// Quiesce has no less polite option. A group with a windowed process can be closed; one without can
    /// still be throttled.
    /// </remarks>
    public required int WindowedCount { get; init; }

    /// <summary>
    /// Windowed processes in this directory that the eligibility filter removed.
    /// </summary>
    /// <remarks>
    /// Exists so the UI cannot say something false. <see cref="WindowedCount"/> counts only processes that
    /// survived the eligibility filter, so a directory whose ONLY windowed process is protected — the
    /// sharp case is an application bundling a fixed-version <c>msedgewebview2</c>, which is never-touch by
    /// name — produces <c>WindowedCount == 0</c> for a directory that demonstrably does own a window. The
    /// honest sentence there is "the window belongs to something Quiesce will not touch", not "nothing
    /// here owns a window".
    /// </remarks>
    public required int WindowedButProtectedCount { get; init; }

    /// <summary>The name to show. The windowed process's image name, which is the one the user knows.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Ids of catalog entries whose <em>process</em> ops already match processes in this group.
    /// </summary>
    /// <remarks>
    /// PROCESS OPS ONLY, and callers must not render an empty list as "the catalog does not cover this".
    /// A registry or service entry can address an application perfectly well without naming a process —
    /// <c>shell.disable-widgets-policy</c> turns Widgets off through a policy value, so Widgets shows up
    /// here with an empty <c>CoveredBy</c> while the catalog demonstrably does handle it. The honest reading
    /// of empty is "nothing in the catalog targets this as a running process", which is a narrower claim.
    /// </remarks>
    public required IReadOnlyList<string> CoveredBy { get; init; }

    public bool IsCovered => CoveredBy.Count > 0;

    /// <summary>True when the group contains a process that could be asked to close.</summary>
    public bool CanClose => WindowedCount > 0;
}

/// <summary>
/// The discovery result: the candidates, plus what was left out and why.
/// </summary>
/// <remarks>
/// The omitted counts are here rather than dropped because a list that silently shortens is the failure
/// this whole feature exists to fix. Saying "and 7 Windows components are not listed" is a different
/// statement from showing 14 rows and letting the user conclude that is everything.
/// </remarks>
public sealed record AppDiscoveryResult
{
    public required IReadOnlyList<AppCandidate> Candidates { get; init; }

    /// <summary>How many groups were dropped for living inside the Windows directory.</summary>
    public required int WindowsComponentsOmitted { get; init; }
}

/// <summary>
/// Finds running applications Quiesce would be permitted to act on, so the user can add them.
/// </summary>
/// <remarks>
/// THIS PROPOSES; IT NEVER TARGETS. Nothing here changes what Engage does. The output is a list for a
/// human to choose from, and choosing one writes an ordinary catalog entry — pinned to an image name and
/// the directory that application is installed in, validated by the same
/// <see cref="CatalogLoader"/> as the shipped catalog, and subject to every guardrail.
/// <para>
/// That distinction is the whole design. Path-based, catalog-named targeting is the safety property: it
/// is what makes "close browsers" mean nine specific programs in nine specific directories rather than
/// "anything whose name looks browser-ish". Discovery that fed straight into targeting would throw that
/// away and replace it with a heuristic. What discovery fixes is the *visibility* gap — an application
/// the catalog has never heard of was previously invisible, so a browser Quiesce did not close looked
/// exactly like a browser that was not running. Perplexity's Comet went through an entire Engage that
/// way, and the plan printed nine confident "nothing matching X is running" lines while it did.
/// </para>
/// </remarks>
/// <param name="ownImageName">
/// Quiesce's own image name, without <c>.exe</c>. Another copy of Quiesce on disk is a different image
/// path, so the classifier's self-protection — which is deliberately path-based — does not cover it, and
/// the list would offer the user the program they are reading the list in. Injectable so a test's outcome
/// cannot depend on what the test host happens to be called.
/// </param>
public sealed class RunningAppDiscovery(
    IProcessControl processes,
    ProcessClassifier classifier,
    string? ownImageName = null)
{
    private readonly string _ownImageName = Bare(ownImageName
        ?? Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty));

    /// <summary>
    /// Groups the live process list into candidates, marking the ones the catalog already covers.
    /// </summary>
    /// <param name="catalog">
    /// Used only to work out what is already covered — including entries that are switched off, because
    /// an entry that exists does not need adding again.
    /// </param>
    public AppDiscoveryResult Discover(CatalogFile? catalog)
    {
        var live = processes.Enumerate();

        // Own session only. A process in another user's session cannot be asked to close from here, and
        // offering to act on one would be offering something Quiesce cannot do. Resolved from the
        // enumeration rather than queried separately so it is the same view of the machine throughout.
        var ownSession = live.FirstOrDefault(p => p.Identity.Pid == Environment.ProcessId)?.SessionId;

        var eligible = live.Where(p =>
                p.Present
                && p.ImagePath is not null
                && (ownSession is null || p.SessionId == ownSession)
                && !(_ownImageName.Length > 0 && Bare(p.ImageName).Equals(_ownImageName, StringComparison.OrdinalIgnoreCase))
                // The one filter that carries the safety: only classes Quiesce would ever act on. Games,
                // launchers, anti-cheat, service hosts, the shell, the compositor and Quiesce's own host
                // never reach the list, so they cannot be added by a user who does not know what they are.
                && classifier.Classify(p) is ProcessClass.Ordinary or ProcessClass.Browser)
            .ToList();

        // Windowed processes per directory across the WHOLE live list, not just the eligible subset, so a
        // candidate can tell "no window here" from "the window here is one Quiesce will not touch".
        var windowedPerDirectory = live
            .Where(p => p.HasVisibleWindow && !string.IsNullOrEmpty(p.ImagePath))
            .GroupBy(p => Path.GetDirectoryName(p.ImagePath!) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var processOps = catalog?.Entries
            .SelectMany(e => e.Ops.OfType<ProcessOpSpec>().Select(op => (e.Id, Op: op)))
            .ToList() ?? [];

        var candidates = new List<AppCandidate>();
        var systemOmitted = 0;

        foreach (var group in eligible.GroupBy(
                     p => Path.GetDirectoryName(p.ImagePath!) ?? string.Empty,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(group.Key))
            {
                continue;
            }

            // MEASURED ON THE REAL MACHINE, and the reason this check exists. Grouping by directory
            // assumes a directory belongs to one application, which is true of an install tree and false
            // of C:\Windows\System32 — where the first live run collected eleven unrelated processes into
            // one "application" named ApplicationFrameHost. Adding that would have pinned
            // C:\Windows\System32\ and asked all eleven to close, including rdpclip.exe (the clipboard of
            // the Remote Desktop session driving the machine), ctfmon, sihost, taskhostw and whatever
            // console was open.
            //
            // Structural rather than a list of process names to spare: the Windows directory is not an
            // application's install root, and nothing under it is a user application. Windows' own
            // components are the service and registry layers' business, where the guardrails understand
            // what they are looking at. Store-packaged applications under WindowsApps are NOT excluded —
            // each package has its own directory, so the grouping assumption holds there.
            if (IsUnderWindowsDirectory(group.Key))
            {
                systemOmitted++;
                continue;
            }

            var fragment = ToDirectoryFragment(group.Key);
            if (fragment is null)
            {
                // A path that cannot be expressed as an anchored fragment cannot be pinned, and an
                // unpinnable target is one Quiesce would refuse at catalog load anyway. Dropped rather
                // than offered as something that will not work.
                continue;
            }

            var members = group.ToList();
            var names = members.Select(p => Bare(p.ImageName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var covered = processOps
                .Where(x => members.Any(p => x.Op.Matches(p.ImageName, p.ImagePath)))
                .Select(x => x.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            // The windowed process is the one the user thinks of as the application; the windowless
            // helpers beside it share its directory and are not what to call the group. With nothing
            // windowed there is no good answer, so the fallback is at least a STABLE one — enumeration
            // order changes between scans, and a row whose name shuffles every rescan reads as a
            // different application each time.
            var display = members.FirstOrDefault(p => p.HasVisibleWindow)?.ImageName
                ?? members.OrderBy(p => p.ImageName, StringComparer.OrdinalIgnoreCase).First().ImageName;

            var eligibleWindowed = members.Count(p => p.HasVisibleWindow);
            windowedPerDirectory.TryGetValue(group.Key, out var allWindowed);

            candidates.Add(new AppCandidate
            {
                InstallDirectory = group.Key,
                DirectoryFragment = fragment,
                ImageNames = names,
                ProcessCount = members.Count,
                WindowedCount = eligibleWindowed,
                WindowedButProtectedCount = Math.Max(0, allWindowed - eligibleWindowed),
                DisplayName = Bare(display),
                CoveredBy = covered,
            });
        }

        // Closable first, then by weight. A 20-process browser matters more than a single-process utility,
        // and the thing the user is most likely looking for is the application they can see.
        return new AppDiscoveryResult
        {
            Candidates =
            [
                .. candidates
                    .OrderByDescending(c => c.CanClose)
                    .ThenBy(c => c.IsCovered)
                    .ThenByDescending(c => c.ProcessCount)
                    .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase),
            ],
            WindowsComponentsOmitted = systemOmitted,
        };
    }

    /// <summary>Whether a directory is the Windows directory or anything beneath it.</summary>
    /// <remarks>
    /// Compared as a path prefix followed by a separator rather than as a substring, for the reason the
    /// catalog's directory fragments are anchored at both ends: a bare Contains on "Windows" would also
    /// match <c>C:\Program Files\WindowsApps\</c>, which is the one place under a Windows-ish name that
    /// must stay eligible.
    /// </remarks>
    public static bool IsUnderWindowsDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar);

        if (windows.Length == 0)
        {
            return false;
        }

        var normalized = directory.TrimEnd(Path.DirectorySeparatorChar);

        return normalized.Equals(windows, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turns a directory path into an anchored catalog fragment, or null when it cannot be one.
    /// </summary>
    /// <remarks>
    /// Keeps the drive. The alternative — stripping it to a drive-relative fragment, as the shipped
    /// catalog uses — would be less specific than the fact being recorded: discovery knows exactly which
    /// directory this application is in, and throwing that away to match the shipped style would widen
    /// what the entry matches for no benefit.
    /// </remarks>
    public static string? ToDirectoryFragment(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var fragment = directory.Replace('/', '\\').TrimEnd('\\') + '\\';

        return CatalogLoader.IsAnchoredDirectoryFragment(fragment)
               && !fragment.Contains("..", StringComparison.Ordinal)
               && fragment.IndexOfAny(['*', '?']) < 0
            ? fragment
            : null;
    }

    private static string Bare(string imageName) =>
        imageName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? imageName[..^4] : imageName;
}
