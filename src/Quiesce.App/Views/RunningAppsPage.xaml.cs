using System.IO;
using System.Windows;
using Quiesce.Core;
using Quiesce.Core.Catalog;

namespace Quiesce.App.Views;

/// <summary>
/// Lists running applications the user could add to the catalog, and adds them.
/// </summary>
/// <remarks>
/// Adding is authoring, not targeting. The button writes an ordinary catalog entry — image name plus the
/// directory the application was found in — which then goes through the same loader, the same guardrails
/// and the same preflight as everything shipped. Nothing on this page closes anything; the entry it
/// creates ships switched OFF, so even after adding, an Engage does nothing until the user turns it on in
/// Features. Two deliberate steps, because the discovery list is the least considered thing in the app:
/// it is generated from whatever happens to be running.
/// </remarks>
public partial class RunningAppsPage
{
    private AppState _state;

    public RunningAppsPage(AppState state)
    {
        InitializeComponent();
        _state = state;
        Rescan();
    }

    /// <summary>Raised after the user catalog changes, so the shell can rebuild plans and other pages.</summary>
    public event EventHandler? CatalogChanged;

    private void OnRescan(object sender, RoutedEventArgs e) => Rescan(reloadState: true);

    /// <param name="reloadState">
    /// Re-read the catalog before scanning. Required after a write, and the omission of it was a real bug:
    /// this page survives the shell's page rebuild on purpose — tearing down the control still inside its
    /// own click handler would crash — but that also means nothing else refreshes its state. So it went on
    /// comparing the machine against the catalog as it was BEFORE the add, kept showing the app as not
    /// covered, and kept offering the button that adds it. Four presses, four entries.
    /// </param>
    private void Rescan(bool reloadState = false)
    {
        if (reloadState)
        {
            _state = AppState.Load();
        }

        AppDiscoveryResult found;
        try
        {
            found = AppState.CreateDiscovery().Discover(_state.Catalog);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            ShowNote($"Could not enumerate processes: {ex.Message}");
            AppList.ItemsSource = Array.Empty<AppRow>();
            return;
        }

        var candidates = found.Candidates;
        var userIds = UserEntryIds();
        AppList.ItemsSource = candidates.Select(c => AppRow.From(c, userIds)).ToList();

        var addable = candidates.Count(c => !c.IsCovered);
        var covered = candidates.Count - addable;

        CountLabel.Text =
            $"{candidates.Count} running app{(candidates.Count == 1 ? "" : "s")} Quiesce may act on — " +
            $"{addable} not in the catalog, {covered} already covered";

        // Said plainly, because the count is the honest answer to "why didn't Quiesce close my browser":
        // it was never a target. A group Quiesce has not been told about is not refused, it is invisible.
        var note = addable == 0
            ? "Everything running that Quiesce may act on is already covered by the catalog."
            : $"{addable} of these are not in the catalog, so Engage does not look for them and will not " +
              "close them. Adding one creates an entry that starts switched OFF — turn it on in Features.";

        // The omission is stated rather than left to be noticed. A list that quietly shortens is the exact
        // failure this page exists to fix.
        if (found.WindowsComponentsOmitted > 0)
        {
            note += $" ({found.WindowsComponentsOmitted} group(s) inside the Windows folder are not " +
                    "listed — that folder holds many unrelated Windows processes side by side, so it " +
                    "cannot be treated as one application's install location.)";
        }

        ShowNote(note);
    }

    private HashSet<string> UserEntryIds()
    {
        try
        {
            return new UserCatalogStore(_state.DataRoot).Load()?.Entries
                       .Select(e => e.Id)
                       .ToHashSet(StringComparer.OrdinalIgnoreCase)
                   ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is CatalogException or IOException or UnauthorizedAccessException
                                      or Core.Journal.StateUnreadableException)
        {
            ShowNote($"Your added apps could not be read: {ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void OnAddClose(object sender, RoutedEventArgs e) =>
        Add(sender, ProcessAction.Close, throttleTo: null);

    private void OnAddThrottle(object sender, RoutedEventArgs e) =>
        Add(sender, ProcessAction.Throttle, ThrottleLevel.BelowNormal);

    private void Add(object sender, ProcessAction action, ThrottleLevel? throttleTo)
    {
        if (sender is not FrameworkElement { Tag: AppRow row })
        {
            return;
        }

        try
        {
            var result = new UserCatalogStore(_state.DataRoot)
                .Add(row.Candidate, action, throttleTo, _state.Catalog);

            var what = action == ProcessAction.Close
                ? $"close {row.Candidate.DisplayName}. Restore will not reopen it."
                : $"lower {row.Candidate.DisplayName}'s priority. Restore puts it back exactly.";

            var message = result.Outcome switch
            {
                UserEntryOutcome.Added =>
                    $"Added '{result.EntryId}'. It is switched OFF — go to Features and turn it on for Engage to {what}",

                // Said rather than silently absorbed: the entry the user is looking at now covers more than
                // it did, and which executables changed is the whole content of that.
                UserEntryOutcome.Extended =>
                    $"'{result.EntryId}' already covered this folder, so it was extended rather than " +
                    $"duplicated. Now also covers: {string.Join(", ", result.AddedImageNames.Select(n => n + ".exe"))}.",

                _ => $"'{result.EntryId}' already covers {row.Candidate.DisplayName} completely. Nothing changed.",
            };

            // Rescan first so the row stops offering the button, THEN report — the rescan writes the note
            // itself, so showing the outcome before it would be overwritten by a summary line.
            Rescan(reloadState: true);
            ShowNote(message);
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is CatalogException or IOException or UnauthorizedAccessException
                                      or Core.Journal.StateUnreadableException)
        {
            // A refusal from the catalog validator is the interesting case and is surfaced verbatim: it
            // means a guardrail declined the entry, and the reason is the useful part.
            ShowNote($"Not added: {ex.Message}");
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AppRow row } || row.RemovableEntryIds.Count == 0)
        {
            return;
        }

        try
        {
            // Every entry the user added for this application, not the first one found. The row IS the
            // application, and a Remove that took one of four duplicates away would need pressing four
            // times with no indication that it should be.
            var ids = row.RemovableEntryIds;
            var removed = new UserCatalogStore(_state.DataRoot).Remove([.. ids]);

            var message = removed == 0
                ? $"Nothing to remove: {string.Join(", ", ids)} are not in your added apps."
                : $"Removed {removed} entr{(removed == 1 ? "y" : "ies")} you had added for " +
                  $"{row.Candidate.DisplayName}: {string.Join(", ", ids)}. Quiesce no longer looks for it.";

            Rescan(reloadState: true);
            ShowNote(message);
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is CatalogException or IOException or UnauthorizedAccessException
                                      or Core.Journal.StateUnreadableException)
        {
            ShowNote($"Not removed: {ex.Message}");
        }
    }

    private void ShowNote(string text)
    {
        NoteText.Text = text;
        Note.Visibility = Visibility.Visible;
    }
}

/// <summary>One row of the running-apps list.</summary>
public sealed record AppRow
{
    public required AppCandidate Candidate { get; init; }

    public required string Title { get; init; }

    public required string ProcessLabel { get; init; }

    public required string Detail { get; init; }

    public required string PathLabel { get; init; }

    public required string CloseTip { get; init; }

    public required bool CanClose { get; init; }

    public required Visibility CoveredVisibility { get; init; }

    public required Visibility AddVisibility { get; init; }

    public required Visibility RemoveVisibility { get; init; }

    /// <summary>The covering entries the user added themselves, so removal is theirs to do.</summary>
    public required IReadOnlyList<string> RemovableEntryIds { get; init; }

    public static AppRow From(AppCandidate candidate, IReadOnlySet<string> userEntryIds)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(userEntryIds);

        // Only entries the user added are removable from here. A shipped entry is part of the catalog's
        // reviewed content and removing it through a discovery list would be an undocumented way to edit
        // the catalog.
        var removable = candidate.CoveredBy.Where(userEntryIds.Contains).ToList();

        var names = candidate.ImageNames.Count == 1
            ? candidate.ImageNames[0] + ".exe"
            : $"{candidate.ImageNames.Count} executables: {string.Join(", ", candidate.ImageNames.Select(n => n + ".exe"))}";

        return new AppRow
        {
            Candidate = candidate,
            Title = candidate.DisplayName,
            ProcessLabel = candidate.ProcessCount == 1
                ? "1 process"
                : $"{candidate.ProcessCount} processes, {candidate.WindowedCount} with a window",
            Detail = candidate.IsCovered
                ? $"{names}. Covered by {string.Join(", ", candidate.CoveredBy)}."
                : names + (candidate.CanClose
                    ? "."
                    : ". No window, so it cannot be asked to close — Quiesce has no forceful option. It can still be throttled."),
            PathLabel = candidate.InstallDirectory,
            CanClose = candidate.CanClose,
            CloseTip = candidate.CanClose
                ? "Asks every window to close, exactly as the X button does, and respects a save-your-work prompt. Restore does NOT reopen it."
                : "Nothing here owns a window, so there is nothing to send a close request to.",
            CoveredVisibility = candidate.IsCovered ? Visibility.Visible : Visibility.Collapsed,
            AddVisibility = candidate.IsCovered ? Visibility.Collapsed : Visibility.Visible,
            RemoveVisibility = removable.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
            RemovableEntryIds = removable,
        };
    }
}
