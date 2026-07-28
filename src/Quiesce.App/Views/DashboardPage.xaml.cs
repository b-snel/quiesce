using System.IO;
using System.Windows;
using System.Windows.Media;
using Quiesce.Core.Engine;
using Quiesce.Core.Platform;

namespace Quiesce.App.Views;

public partial class DashboardPage
{
    private static readonly Brush CleanBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x3F, 0xB9, 0x50));
    private static readonly Brush EngagedBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xD2, 0x99, 0x22));
    private static readonly Brush ProblemBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xF8, 0x51, 0x49));

    private AppState _state;

    public DashboardPage(AppState state)
    {
        InitializeComponent();
        _state = state;
        Render();
    }

    /// <summary>Raised after a successful engage or restore so the shell can refresh other pages.</summary>
    public event EventHandler? StateChanged;

    private void Render()
    {
        if (_state.StateUnknown)
        {
            // Never rendered as clean. "Not dirty" and "cannot tell" are different facts, and showing the
            // reassuring one for both is exactly how a tool ends up lying about the only thing it is for.
            StateBanner.Background = EngagedBrush;
            StateHeadline.Text = "Unknown";
            StateDetail.Text =
                "Quiesce cannot read its own state file, so it does not know whether this machine is " +
                "modified. Engage is disabled: engaging over an already-engaged machine would capture the " +
                "first session's changes as if they were your original settings.";
        }
        else if (_state.MachineState.IsDirty)
        {
            StateBanner.Background = EngagedBrush;
            StateHeadline.Text = "Engaged";
            StateDetail.Text =
                $"Session {_state.MachineState.ActiveSessionId:D} is active. " +
                "Restore puts everything back exactly as it was.";
        }
        else
        {
            StateBanner.Background = CleanBrush;
            StateHeadline.Text = "Machine is clean";
            StateDetail.Text = "No Quiesce changes are active. Everything is as Windows left it.";
        }

        // Engage is refused while dirty by the engine anyway - disabling it here explains why
        // rather than letting the user click into an error. Also refused when the state is unknown,
        // which is the case that has no engine-side check because the engine never gets a chance to run.
        EngageButton.IsEnabled = !_state.MachineState.IsDirty && !_state.StateUnknown && _state.Catalog is not null;

        // Restore stays available when the state is unknown: it is the one action whose worst case is
        // discovering there was nothing to undo, and refusing to undo is never the safer error.
        RestoreButton.IsEnabled = _state.MachineState.IsDirty || _state.StateUnknown;

        var applied = _state.Plan?.Steps.Count(s => s.NoOp) ?? 0;
        var pending = _state.Plan?.EffectiveSteps.Count() ?? 0;

        EnvironmentDetail.Text =
            $"data root   {_state.DataRoot}\n" +
            $"catalog     {_state.CatalogPath ?? "<none found>"}\n" +
            $"tweaks      {(_state.Catalog is null ? "n/a" : $"{_state.Catalog.Entries.Count} in catalog, {applied} already lean, {pending} available")}\n" +
            $"version     {AppState.AppVersion()}" +
            (_state.LoadError is null ? string.Empty : $"\n\nproblem     {_state.LoadError}");
    }

    private async void OnEngage(object sender, RoutedEventArgs e)
    {
        if (_state.Catalog is null)
        {
            ShowResult(ProblemBrush, "No catalog is loaded, so there is nothing to apply.");
            return;
        }

        SetBusy(true);
        try
        {
            var engine = AppState.CreateEngine();

            // Re-plan against live state rather than reusing the plan from page load: the machine
            // may have changed since, and the preflight must show what is true now.
            var plan = await Task.Run(() => engine.Plan(_state.Catalog, "default", new Quiesce.Core.Catalog.ProfileStore(new QuiescePaths().DataRoot).ActiveEnabled()));

            if (!plan.EffectiveSteps.Any())
            {
                ShowResult(CleanBrush, "Nothing to do — every enabled tweak is already at its lean value.");
                return;
            }

            var restorePoint = await Task.Run(() => new SystemRestore().TryCreate("Before Quiesce engage"));

            var dialog = new PreflightDialog(plan, restorePoint) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true)
            {
                ShowResult(null, "Cancelled. Nothing was changed.");
                return;
            }

            var result = await Task.Run(() => engine.Engage(plan, FaultInjector.None));

            if (result.Success)
            {
                ShowResult(CleanBrush,
                    $"Engaged. {result.Applied} change{(result.Applied == 1 ? "" : "s")} applied" +
                    (result.SkippedNoop > 0 ? $", {result.SkippedNoop} already lean" : string.Empty) + "." +
                    (result.RebootPendingEntries.Count > 0
                        ? $" {result.RebootPendingEntries.Count} of them need a restart before they do anything — " +
                          "see the banner at the top."
                        : string.Empty));
            }
            else
            {
                // Name the reason, not just the entry: "Windows refused this" and "the app is
                // broken" look identical to a user unless the app says which happened.
                var detail = string.Join("\n", result.RolledBackEntries.Select(id =>
                    $"  • {id}: {(result.Diagnoses.TryGetValue(id, out var d) ? d : "verification failed")}"));

                ShowResult(ProblemBrush,
                    $"Engaged. {result.Applied} change{(result.Applied == 1 ? "" : "s")} applied. " +
                    $"{result.RolledBackEntries.Count} rolled back — nothing is half-applied:\n" + detail);
            }

            Refresh();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowResult(ProblemBrush, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            if (_state.MachineState.ActiveSessionId is not { } sessionId)
            {
                ShowResult(null, "No active session to restore.");
                return;
            }

            var engine = AppState.CreateEngine();
            var result = await Task.Run(() => engine.RevertSession(sessionId, "restore"));

            if (result.Clean)
            {
                ShowResult(CleanBrush,
                    $"Restored. {result.Reverted} change{(result.Reverted == 1 ? "" : "s")} put back." +
                    (result.RebootPendingEntries.Count > 0
                        ? " The registry is back as it was, but some of these need a restart before the " +
                          "machine behaves that way again — see the banner at the top."
                        : string.Empty));
            }
            else
            {
                // Deferred and failed are different failures and the user needs to know which.
                var parts = new List<string> { $"{result.Reverted} reverted" };
                if (result.Deferred > 0)
                {
                    parts.Add($"{result.Deferred} deferred (another user's settings — sign in as that user and Quiesce will finish)");
                }

                if (result.Failed > 0)
                {
                    parts.Add($"{result.Failed} failed");
                }

                ShowResult(ProblemBrush,
                    string.Join("; ", parts) + ". The machine is still marked engaged until every change is back." +
                    (result.Messages.Count > 0 ? "\n\n" + string.Join("\n", result.Messages) : string.Empty));
            }

            Refresh();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowResult(ProblemBrush, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Refresh()
    {
        _state = AppState.Load();
        Render();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        EngageButton.IsEnabled = !busy && !_state.MachineState.IsDirty && _state.Catalog is not null;
        RestoreButton.IsEnabled = !busy && _state.MachineState.IsDirty;
    }

    private void ShowResult(Brush? background, string message)
    {
        ResultBanner.Background = background ?? new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        ResultText.Text = message;
        ResultBanner.Visibility = Visibility.Visible;
    }
}
