using System.IO;
using System.Windows;
using Quiesce.Core.Catalog;
using Quiesce.Core.Platform;
using Quiesce.Core.Startup;

namespace Quiesce.App.Views;

/// <summary>
/// Lists what runs at sign-in and lets the user turn entries off as standing preferences.
/// </summary>
/// <remarks>
/// The one place in Quiesce that authors <see cref="TweakScope.Persistent"/> entries, and the reason is
/// worth keeping in view: a Session-scoped change would be auto-reverted by boot recovery once the boot
/// had passed, which is exactly the moment a "do not start at sign-in" preference needs to still be in
/// force. So these behave differently from everything else in the app — they survive a reboot on purpose.
/// <para>
/// Two steps, as with the running-apps list. This page authors the entry; the entry ships switched OFF, and
/// Features plus the preflight are still the gate. Nothing here writes to the machine's startup
/// configuration — it writes a catalog entry that Engage will later apply through the journal.
/// </para>
/// </remarks>
public partial class StartupPage
{
    private AppState _state;

    public StartupPage(AppState state)
    {
        InitializeComponent();
        _state = state;
        Rescan();
    }

    /// <summary>Raised after the user catalog changes, so the shell can rebuild plans and other pages.</summary>
    public event EventHandler? CatalogChanged;

    private void OnRescan(object sender, RoutedEventArgs e) => Rescan(reloadState: true);

    private void Rescan(bool reloadState = false)
    {
        if (reloadState)
        {
            _state = AppState.Load();
        }

        IReadOnlyList<StartupItem> items;
        try
        {
            items = new StartupItemDiscovery(new Win32StartupInventory()).Discover();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            ShowNote($"Could not read the startup entries: {ex.Message}");
            ItemList.ItemsSource = Array.Empty<StartupRow>();
            return;
        }

        var existing = ExistingPreferences();
        ItemList.ItemsSource = items.Select(i => StartupRow.From(i, existing)).ToList();

        var on = items.Count(i => !i.AlreadyDisabled);
        var unmanageable = items.Count(i => !i.CanDisable);

        CountLabel.Text = $"{items.Count} sign-in entries — {on} still on, {items.Count - on} already off";

        var note = on == 0
            ? "Nothing runs at sign-in that is not already switched off."
            : $"{on} of these still run every time you sign in. Turning one off adds a standing preference " +
              "that starts switched OFF in Features — turn it on there and Engage to apply it.";

        // The honest asterisk. Quiesce switches these off by writing Explorer's approval value, and a
        // scheduled task has no approval value — so an app with both surfaces stays half-handled, and the
        // list has to say which rather than let the count imply completeness.
        if (unmanageable > 0)
        {
            note += $" {unmanageable} are logon scheduled tasks, which Quiesce cannot switch off at all — " +
                    "they are listed so the rest of this list cannot read as the whole picture.";
        }

        ShowNote(note);
    }

    /// <summary>Registry targets the user already has a startup preference for, keyed to an entry id.</summary>
    private Dictionary<string, string> ExistingPreferences()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var user = new UserCatalogStore(_state.DataRoot).Load();
            foreach (var entry in user?.Entries ?? [])
            {
                if (entry.Ops.Count == 1 && entry.Ops[0] is RegistryOpSpec op)
                {
                    map[$"{op.Hive}|{op.Subkey}|{op.Value}"] = entry.Id;
                }
            }
        }
        catch (Exception ex) when (ex is CatalogException or IOException or UnauthorizedAccessException
                                      or Core.Journal.StateUnreadableException)
        {
            ShowNote($"Your added preferences could not be read: {ex.Message}");
        }

        return map;
    }

    private void OnDisable(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StartupRow row })
        {
            return;
        }

        try
        {
            var result = new UserCatalogStore(_state.DataRoot).AddStartupDisable(row.Item, _state.Catalog);

            var message = result.Outcome switch
            {
                UserEntryOutcome.Added =>
                    $"Added '{result.EntryId}'. It is switched OFF — turn it on in Features, then Engage, and " +
                    $"{row.Title} will stop starting at sign-in. This one stays in force across reboots.",
                UserEntryOutcome.Extended =>
                    $"'{result.EntryId}' already covered {row.Title} and was refreshed against its current state.",
                _ => $"'{result.EntryId}' already covers {row.Title}. Nothing changed.",
            };

            Rescan(reloadState: true);
            ShowNote(message);
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is CatalogException or IOException or UnauthorizedAccessException
                                      or Core.Journal.StateUnreadableException)
        {
            ShowNote($"Not added: {ex.Message}");
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StartupRow row } || row.PreferenceEntryId is not { } id)
        {
            return;
        }

        try
        {
            var removed = new UserCatalogStore(_state.DataRoot).Remove(id);

            // Said explicitly, because it is the obvious wrong assumption: deleting the preference removes
            // Quiesce's INTENTION, not its effect. If the change is currently applied, Restore is what puts
            // the machine back — and with the entry gone, the journal is what remembers how.
            var message = removed > 0
                ? $"Removed '{id}'. If it is currently applied, the machine is unchanged until you Restore — " +
                  "the journal still holds the original value."
                : $"'{id}' was not in your added preferences.";

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

/// <summary>One row of the startup list.</summary>
public sealed record StartupRow
{
    public required StartupItem Item { get; init; }

    public required string Title { get; init; }

    public required string LocationLabel { get; init; }

    public required string StateLabel { get; init; }

    public required string Command { get; init; }

    public required Visibility OnMarkerVisibility { get; init; }

    public required Visibility AdminVisibility { get; init; }

    public required Visibility UnmanageableVisibility { get; init; }

    public required Visibility ActionVisibility { get; init; }

    public required Visibility RemoveVisibility { get; init; }

    public required string? PreferenceEntryId { get; init; }

    public static StartupRow From(StartupItem item, IReadOnlyDictionary<string, string> existing)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(existing);

        var approval = Win32StartupInventory.ApprovalKeyDescription(item.Location);
        var key = approval is null ? null : $"{approval}|{item.Name}";
        var preference = key is not null && existing.TryGetValue(key, out var id) ? id : null;

        return new StartupRow
        {
            Item = item,
            Title = item.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? item.Name[..^4] : item.Name,
            LocationLabel = Describe(item.Location),
            StateLabel = !item.CanDisable
                ? "A scheduled task with a logon trigger. Quiesce switches entries off by writing Explorer's " +
                  "approval value, and a task has none — so this is shown for completeness and cannot be changed here."
                : item.AlreadyDisabled
                    ? "Already off. Nothing to do — Engage would skip it."
                    : "Runs at every sign-in.",
            Command = item.Command,
            OnMarkerVisibility = item is { AlreadyDisabled: false, CanDisable: true }
                ? Visibility.Visible
                : Visibility.Hidden,
            AdminVisibility = item.NeedsAdmin ? Visibility.Visible : Visibility.Collapsed,
            UnmanageableVisibility = item.CanDisable ? Visibility.Collapsed : Visibility.Visible,
            // Offered only where there is something to do: manageable, still on, and not already covered.
            ActionVisibility = item.CanDisable && !item.AlreadyDisabled && preference is null
                ? Visibility.Visible
                : Visibility.Collapsed,
            RemoveVisibility = preference is null ? Visibility.Collapsed : Visibility.Visible,
            PreferenceEntryId = preference,
        };
    }

    private static string Describe(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => "your Run key",
        StartupLocation.UserStartupFolder => "your Startup folder",
        StartupLocation.MachineRun => "all-users Run key",
        StartupLocation.MachineRun32 => "all-users Run key (32-bit)",
        StartupLocation.MachineStartupFolder => "all-users Startup folder",
        StartupLocation.LogonTask => "logon scheduled task",
        _ => location.ToString(),
    };
}
