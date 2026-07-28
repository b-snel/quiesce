using System.Windows.Controls;
using Quiesce.App.Views;

namespace Quiesce.App;

public partial class MainWindow
{
    private readonly Dictionary<string, UserControl> _pages;

    public MainWindow()
    {
        InitializeComponent();

        var state = AppState.Load();

        _pages = new Dictionary<string, UserControl>
        {
            ["Dashboard"] = new DashboardPage(state),
            ["Features"] = new FeaturesPage(state),
            ["Services"] = new ServicesPage(),
            ["What Quiesce won't do"] = new WontDoPage(),
        };

        foreach (var name in _pages.Keys)
        {
            Nav.Items.Add(name);
        }

        Nav.SelectedIndex = 0;
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is string name && _pages.TryGetValue(name, out var page))
        {
            PageHost.Content = page;
        }
    }
}
