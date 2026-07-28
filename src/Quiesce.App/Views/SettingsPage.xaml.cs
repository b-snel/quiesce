using System.IO;
using System.Windows;
using Quiesce.Core.Journal;
using Quiesce.Core.Platform;

namespace Quiesce.App.Views;

/// <summary>
/// The two preferences that exist, the facts about this machine, and the settings that are refused.
/// </summary>
/// <remarks>
/// The refusals are not filler. Until this page existed there was no preferences concept at all, so every
/// absent setting was indistinguishable from an oversight — and two of them (the data root, the drift
/// cadence) are absent for reasons that are actually load-bearing. This is the <c>WontDoPage</c> pattern
/// applied to configuration.
/// </remarks>
public partial class SettingsPage
{
    private readonly AppState _state;
    private readonly ILogonTaskRegistration _logonTask;

    /// <summary>
    /// Suppresses the toggle handlers while the page writes their initial values.
    /// </summary>
    /// <remarks>
    /// A <c>ui:ToggleSwitch</c> raises Checked/Unchecked when it is set in code, so loading the saved state
    /// would immediately fire the handler that saves it — and for the sign-in switch that means registering
    /// or removing a scheduled task on every page construction.
    /// </remarks>
    private bool _loading;

    public SettingsPage(AppState state)
        : this(state, new LogonTaskRegistration())
    {
    }

    /// <param name="logonTask">
    /// Injected so a test can exercise the page without registering a real scheduled task on the machine
    /// running the tests.
    /// </param>
    internal SettingsPage(AppState state, ILogonTaskRegistration logonTask)
    {
        InitializeComponent();

        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logonTask = logonTask ?? throw new ArgumentNullException(nameof(logonTask));

        RefusalList.ItemsSource = Refusals(_state.DataRoot);

        Render();
    }

    private void Render()
    {
        _loading = true;
        try
        {
            QuiesceSettings settings;
            try
            {
                settings = new SettingsStore(_state.DataRoot).Load();
            }
            catch (Exception ex) when (ex is StateUnreadableException or JournalFormatException)
            {
                // Defaults, and say so. Silently showing defaults over an unreadable file would let the user
                // toggle something twice and wonder why it never sticks.
                settings = new QuiesceSettings();
                ShowNote($"Your settings could not be read, so these are the defaults: {ex.Message}");
            }

            CloseToTrayToggle.IsChecked = settings.CloseToNotificationArea;

            // THE SCHEDULER IS THE AUTHORITY, not the settings file. Asked live so that a task the user
            // deleted in Task Scheduler shows as off here rather than as on, and so this page never
            // re-creates something they removed by hand.
            var registered = _logonTask.IsRegistered();
            StartAtSignInToggle.IsChecked = registered;

            if (registered != settings.StartAtSignIn)
            {
                ShowNote(
                    registered
                        ? $"The scheduled task {_logonTask.TaskPath} exists but Quiesce had not recorded it. " +
                          "Showing what the scheduler says, which is what actually runs."
                        : $"Quiesce had recorded that it starts at sign-in, but the scheduled task " +
                          $"{_logonTask.TaskPath} is gone — removed outside Quiesce. Showing what the " +
                          "scheduler says.");
            }

            StartAtSignInDetail.Text =
                "Quiesce needs administrator rights, so a normal startup entry would ask your permission " +
                $"every time you signed in. This registers a scheduled task instead ({_logonTask.TaskPath}) " +
                "that runs with those rights already granted.\n\n" +
                "THIS IS THE ONE CHANGE QUIESCE MAKES THAT IT DOES NOT JOURNAL. Restore does not remove it — " +
                "this switch is the only thing that does. It will also appear in Quiesce's own sign-in list " +
                "as a logon task, which Quiesce cannot switch off from there.";

            var applied = _state.Plan?.Steps.Count(s => s.NoOp) ?? 0;
            var pending = _state.Plan?.EffectiveSteps.Count() ?? 0;

            EnvironmentDetail.Text =
                $"data root       {_state.DataRoot}\n" +
                $"settings        {Path.Combine(_state.DataRoot, SettingsStore.FileName)}\n" +
                $"catalog         {_state.CatalogPath ?? "<none found>"}\n" +
                $"catalog version {_state.Catalog?.CatalogVersion ?? "n/a"}\n" +
                $"entries         {(_state.Catalog is null ? "n/a" : $"{_state.Catalog.Entries.Count} in catalog, {applied} already lean, {pending} available")}\n" +
                $"app version     {AppState.AppVersion()}\n" +
                $"elevated        {(IsElevated() ? "yes" : "NO — Quiesce cannot read its own data root unelevated")}\n" +
                $"session         {(_state.MachineState.ActiveSessionId is { } id ? id.ToString("D") : "none")}" +
                (_state.LoadError is null ? string.Empty : $"\n\nproblem         {_state.LoadError}");
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnCloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Update(
            s => s with { CloseToNotificationArea = CloseToTrayToggle.IsChecked == true },
            CloseToTrayToggle.IsChecked == true
                ? "Closing the window will keep Quiesce running in the notification area."
                : "Closing the window will exit Quiesce. The notification-area icon goes with it.");
    }

    private void OnStartAtSignInChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var wanted = StartAtSignInToggle.IsChecked == true;

        try
        {
            // The task first, the setting second, and only if the task succeeded. Recording an intent the
            // machine does not reflect is how this page would end up lying about what runs at sign-in.
            var message = wanted
                ? _logonTask.Register(Environment.ProcessPath ?? AppContext.BaseDirectory)
                : _logonTask.Unregister();

            Update(s => s with { StartAtSignIn = wanted }, message);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException
                                      or InvalidOperationException
                                      or IOException)
        {
            // Put the switch back where it was. A switch left in the position the user clicked, over a
            // scheduler that refused, is the app claiming something it did not do.
            _loading = true;
            StartAtSignInToggle.IsChecked = !wanted;
            _loading = false;

            ShowNote($"Not changed: {ex.Message}");
        }
    }

    private void Update(Func<QuiesceSettings, QuiesceSettings> change, string message)
    {
        try
        {
            var store = new SettingsStore(_state.DataRoot);
            store.Save(change(store.Load()));
            ShowNote(message);
        }
        catch (Exception ex) when (ex is StateUnreadableException or JournalFormatException
                                      or IOException or UnauthorizedAccessException)
        {
            ShowNote($"Not saved: {ex.Message}");
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        // Copy, not "open in Explorer". Explorer will not launch elevated from here, so the folder it opened
        // would be one it could not read - a button that appears to work and does not.
        try
        {
            Clipboard.SetText(_state.DataRoot);
            ShowNote($"Copied {_state.DataRoot} to the clipboard. Paste it into an ELEVATED prompt — the " +
                     "data root is restricted to Administrators, so an ordinary one reads it as empty.");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // The clipboard is a shared machine resource and another process can hold it open.
            ShowNote($"Could not copy: {ex.Message}. The path is {_state.DataRoot}");
        }
    }

    private void ShowNote(string text)
    {
        NoteText.Text = text;
        Note.Visibility = Visibility.Visible;
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// The settings that are refused, each with the reason it is a decision and not a gap.
    /// </summary>
    /// <remarks>
    /// Internal so a test can assert the reasons are present without constructing a window — the same
    /// treatment the tray's menu decisions get.
    /// </remarks>
    internal static IReadOnlyList<SettingRefusalRow> Refusals(string dataRoot) =>
    [
        new()
        {
            Title = "How often Quiesce checks whether the machine drifted",
            Reason =
                "It doesn't. There is no background check and there is not going to be one: a periodic " +
                "sweep would enumerate every process and query the service manager on a schedule, " +
                "including while a game is fullscreen — which is the one time Quiesce should be doing " +
                "nothing at all. The dashboard has a Re-check button and both it and the warning banner " +
                "say when they last looked.",
        },
        new()
        {
            Title = "Where the journal lives",
            Reason =
                $"It is {dataRoot}, and QUIESCE_DATA_ROOT moves it. That is an environment variable on " +
                "purpose rather than a setting: the recovery task, the command line and this app all have " +
                "to agree about where the undo is, and a value this app could rewrite at runtime is one " +
                "more way for them to disagree — at the exact moment one of them is trying to put your " +
                "machine back.",
        },
        new()
        {
            Title = "Theme",
            Reason =
                "Dark only, for now. It is not a preference yet, so it is not offered as one — several " +
                "colours are still written into the pages rather than resolved from the palette, and a " +
                "light theme that half-applied would be worse than none.",
        },
        new()
        {
            Title = "Which changes Engage applies",
            Reason =
                "On the Features page, not here. What Quiesce writes to your machine is the product, not " +
                "a setting about the product, and it belongs where each row can carry its own evidence " +
                "rating, its risk tier and what it breaks.",
        },
    ];
}

/// <summary>One row of the not-configurable list.</summary>
public sealed record SettingRefusalRow
{
    public required string Title { get; init; }

    public required string Reason { get; init; }
}
