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

    /// <summary>
    /// Engage is refused while dirty by the engine anyway — disabling it here explains why rather
    /// than letting the user click into an error. Also refused when the state is unknown, which is
    /// the case that has no engine-side check because the engine never gets a chance to run.
    /// </summary>
    /// <remarks>
    /// One expression, read by both <see cref="Render"/> and <see cref="SetBusy"/>. They used to hold
    /// two copies and each copy had dropped a different clause: <c>SetBusy</c> re-enabled Engage in the
    /// UNKNOWN case — the one case with no engine-side check at all — and disabled Restore in the same
    /// case, which is the one action whose worst outcome is discovering there was nothing to undo. Both
    /// wrong, and both wrong in the direction that matters. The duplication was the bug, so there is
    /// now one of each.
    /// </remarks>
    private bool CanEngage =>
        !_state.MachineState.IsDirty && !_state.StateUnknown && _state.Catalog is not null;

    /// <summary>
    /// Restore stays available when the state is unknown: refusing to undo is never the safer error.
    /// </summary>
    private bool CanRestore => _state.MachineState.IsDirty || _state.StateUnknown;

    /// <summary>
    /// Renders lines as a bullet block, or contributes nothing at all when there are none.
    /// </summary>
    /// <remarks>
    /// Returning the empty string rather than an empty block matters: a banner that ends in a
    /// dangling blank line reads as though something was meant to be there and is missing.
    /// </remarks>
    private static string Bullets(IReadOnlyList<string> lines) =>
        lines.Count == 0
            ? string.Empty
            : "\n\n" + string.Join("\n", lines.Select(line => "  • " + line));

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

        EngageButton.IsEnabled = CanEngage;
        RestoreButton.IsEnabled = CanRestore;

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

            Brush brush;
            string message;

            if (result.Success)
            {
                brush = CleanBrush;
                message =
                    $"Engaged. {result.Applied} change{(result.Applied == 1 ? "" : "s")} applied" +
                    (result.SkippedNoop > 0 ? $", {result.SkippedNoop} already lean" : string.Empty) + "." +
                    (result.RebootPendingEntries.Count > 0
                        ? $" {result.RebootPendingEntries.Count} of them need a restart before they do anything — " +
                          "see the banner at the top."
                        : string.Empty);
            }
            else
            {
                // Name the reason, not just the entry: "Windows refused this" and "the app is
                // broken" look identical to a user unless the app says which happened.
                var detail = string.Join("\n", result.RolledBackEntries.Select(id =>
                    $"  • {id}: {(result.Diagnoses.TryGetValue(id, out var d) ? d : "verification failed")}"));

                brush = ProblemBrush;
                message =
                    $"Engaged. {result.Applied} change{(result.Applied == 1 ? "" : "s")} applied. " +
                    $"{result.RolledBackEntries.Count} rolled back — nothing is half-applied:\n" + detail;
            }

            // Notes are neither an applied step nor a failure, and one of the two kinds they carry is
            // the only thing Quiesce does that its undo does not cover: an application that WAS closed
            // and will not be reopened. The other is an application that was asked to close and
            // declined, which is almost always a save-your-work prompt still sitting on screen. Until
            // now the engine wrote both and only the CLI read them, so a GUI Engage that left a browser
            // open — or closed one for good — said nothing about either.
            ShowResult(brush, message + Bullets(result.Notes));

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
                // Bullets(Messages) even on a CLEAN revert. A clean revert still has things to say:
                // "was closed. Quiesce does not relaunch applications - reopen it yourself" is written
                // by the process revert on the happy path, and so is conflict-kept-current, which means
                // a value the user changed after Engage was deliberately left as they set it. Both were
                // journaled and then dropped on the floor here, so the one report that claims the
                // machine is back was the one report that did not mention what had not come back.
                ShowResult(CleanBrush,
                    $"Restored. {result.Reverted} change{(result.Reverted == 1 ? "" : "s")} put back." +
                    (result.RebootPendingEntries.Count > 0
                        ? " The registry is back as it was, but some of these need a restart before the " +
                          "machine behaves that way again — see the banner at the top."
                        : string.Empty) +
                    Bullets(result.Messages));
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

    /// <summary>
    /// Internal rather than private so a test can drive a busy/not-busy cycle and re-read the buttons.
    /// </summary>
    /// <remarks>
    /// Asserting on <see cref="CanEngage"/> and <see cref="CanRestore"/> instead would not catch the
    /// regression that mattered: the bug was that this method computed the button states from its own
    /// second copy of those expressions, so the only assertion that detects it re-diverging is one that
    /// calls this and then reads the buttons.
    /// </remarks>
    internal void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        EngageButton.IsEnabled = !busy && CanEngage;
        RestoreButton.IsEnabled = !busy && CanRestore;
    }

    private void ShowResult(Brush? background, string message)
    {
        ResultBanner.Background = background ?? new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        ResultText.Text = message;
        ResultBanner.Visibility = Visibility.Visible;
    }
}
