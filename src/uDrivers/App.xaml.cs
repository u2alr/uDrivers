using Microsoft.UI.Xaml;

namespace uDrivers;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Services.Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
