using DockGlassLite.Services;
using DockGlassLite.Views;
using System.Windows;

namespace DockGlassLite;

public partial class App : Application
{
    private ConfigService? _configService;
    private StartupService? _startupService;
    private DockWindow? _dockWindow;
    private MainWindow? _settingsWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _configService = new ConfigService();
        await _configService.LoadAsync();

        var shortcutService = new ShortcutService();
        var foregroundWindowService = new ForegroundWindowService();
        _startupService = new StartupService();

        _dockWindow = new DockWindow(_configService, shortcutService, foregroundWindowService);
        _dockWindow.Show();
        _dockWindow.SettingsRequested += (_, _) => ShowSettingsWindow();
    }

    private void ShowSettingsWindow()
    {
        if (_configService is null || _startupService is null || _dockWindow is null)
        {
            return;
        }

        // 设置窗口懒创建：首次打开时才初始化，减少常驻内存
        _settingsWindow ??= new MainWindow(_configService, _startupService, _dockWindow);
        MainWindow = _settingsWindow;

        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }
}
