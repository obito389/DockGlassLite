using DockGlassLite.Services;
using DockGlassLite.Views;
using System.Windows;

namespace DockGlassLite;

public partial class App : Application
{
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configService = new ConfigService();
        await configService.LoadAsync();

        var shortcutService = new ShortcutService();
        var foregroundWindowService = new ForegroundWindowService();
        var startupService = new StartupService();

        var dockWindow = new DockWindow(configService, shortcutService, foregroundWindowService);
        dockWindow.Show();

        _mainWindow = new MainWindow(configService, startupService, dockWindow);
        MainWindow = _mainWindow;
        dockWindow.SettingsRequested += (_, _) => ShowSettingsWindow();
    }

    private void ShowSettingsWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }
}
