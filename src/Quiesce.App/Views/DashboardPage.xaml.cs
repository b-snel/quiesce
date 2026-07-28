using System.Windows.Media;

namespace Quiesce.App.Views;

public partial class DashboardPage
{
    public DashboardPage(AppState state)
    {
        InitializeComponent();

        if (state.MachineState.IsDirty)
        {
            StateBanner.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xD2, 0x99, 0x22));
            StateHeadline.Text = "Engaged";
            StateDetail.Text =
                $"Session {state.MachineState.ActiveSessionId:D} is active. " +
                "Restore puts everything back exactly as it was.";
        }
        else
        {
            StateBanner.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x3F, 0xB9, 0x50));
            StateHeadline.Text = "Machine is clean";
            StateDetail.Text = "No Quiesce changes are active. Everything is as Windows left it.";
        }

        var applied = state.Plan?.Steps.Count(s => s.NoOp) ?? 0;
        var pending = state.Plan?.EffectiveSteps.Count() ?? 0;

        EnvironmentDetail.Text =
            $"data root   {state.DataRoot}\n" +
            $"catalog     {state.CatalogPath ?? "<none found>"}\n" +
            $"tweaks      {(state.Catalog is null ? "n/a" : $"{state.Catalog.Entries.Count} in catalog, {applied} already lean, {pending} available")}\n" +
            $"version     {AppState.AppVersion()}" +
            (state.LoadError is null ? string.Empty : $"\n\nproblem     {state.LoadError}");
    }
}
