using uDrivers.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace uDrivers;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.Resize(new SizeInt32(1120, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        Nav.SelectedItem = Nav.MenuItems[0];
        _ = ViewModel.InitializeAsync();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected
            ? "settings"
            : (args.SelectedItem as NavigationViewItem)?.Tag as string;

        UpdatesPanel.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        DriversPanel.Visibility = tag == "drivers" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }
}
