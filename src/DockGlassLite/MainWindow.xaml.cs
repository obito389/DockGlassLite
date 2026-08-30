using DockGlassLite.Services;
using DockGlassLite.Views;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DockGlassLite;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan IconSizeCommitDelay = TimeSpan.FromMilliseconds(220);
    private const double IconSizeAdjustmentStep = 1;

    private readonly ConfigService _configService;
    private readonly StartupService _startupService;
    private readonly DockWindow _dockWindow;
    private readonly DispatcherTimer _iconSizeCommitTimer;
    private double? _pendingIconSize;
    private bool _isIconSizeDragActive;
    private bool _loading = true;
    private bool _editMode;

    public MainWindow(
        ConfigService configService,
        StartupService startupService,
        DockWindow dockWindow)
    {
        InitializeComponent();

        _configService = configService;
        _startupService = startupService;
        _dockWindow = dockWindow;
        _iconSizeCommitTimer = new DispatcherTimer { Interval = IconSizeCommitDelay };
        _iconSizeCommitTimer.Tick += IconSizeCommitTimer_Tick;
        IconSizeSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(IconSizeSlider_DragStarted));
        IconSizeSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(IconSizeSlider_DragCompleted));

        LoadSettings();
        _loading = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyLiquidGlassBackdrop();
    }

    private void ApplyLiquidGlassBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            ApplyOpaqueFallback();
            return;
        }

        // 深色标题栏（旧版系统用 19，新版用 20，两个都试）
        int enabled = 1;
        var hrDark = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        if (hrDark != 0)
        {
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }

        // 圆角窗口
        int round = NativeMethods.DwmwcpRound;
        var hrRound = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaWindowCornerPreference, ref round, sizeof(int));

        // 液态玻璃：SetWindowCompositionAttribute + Acrylic 模糊，自定义深色染色
        // （系统背景材质 DWMSBT_TRANSIENTWINDOW 的色调跟随系统主题、偏浅，
        //   无法控制；此 API 从 Win10 1803 起稳定可用，可指定暗色 tint）
        // GradientColor 为 ABGR：Alpha=80（50% 染色），颜色 #14181F
        var accent = new AccentPolicy
        {
            AccentState = NativeMethods.AccentEnableAcrylicBlurBehind,
            AccentFlags = 0,
            GradientColor = 0x801F1814,
            AnimationId = 0
        };

        var accentHandle = GCHandle.Alloc(accent, GCHandleType.Pinned);
        int accentResult;
        try
        {
            var data = new WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WcaAccentPolicy,
                Data = accentHandle.AddrOfPinnedObject(),
                SizeOfData = Marshal.SizeOf<AccentPolicy>()
            };
            accentResult = NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            accentHandle.Free();
        }

        LogGlassDiagnostic($"hwnd=0x{hwnd:X} os={Environment.OSVersion.Version} dark=0x{hrDark:X8} round=0x{hrRound:X8} accent={accentResult}");

        if (accentResult == 0)
        {
            ApplyOpaqueFallback();
        }
    }

    private static void LogGlassDiagnostic(string message)
    {
        try
        {
            AppPaths.EnsureDirectories();
            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} GLASS {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志失败不影响主流程
        }
    }

    private void ApplyOpaqueFallback()
    {
        // 旧系统不支持背景材质时，回退为原来的不透明深色底
        var fallback = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x17));
        Background = fallback;
        RootGrid.Background = fallback;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        FlushPendingIconSizeChange();
        Hide();
    }

    private void LoadSettings()
    {
        var config = _configService.Current;
        StartupSwitch.IsChecked = _startupService.IsEnabled();
        IconSizeSlider.Value = config.Dock.IconSize;
        UpdateEditModeButton();
    }

    private async void StartupSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = StartupSwitch.IsChecked == true;
        var success = enabled ? _startupService.Enable() : _startupService.Disable();

        _configService.Current.StartWithWindows = enabled && success;
        await _configService.SaveAsync();

        ShowStatus(success ? "开机自启设置已保存。" : "开机自启设置失败。", success);
    }

    private void IconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _dockWindow.PreviewIconSize(e.NewValue);
        _pendingIconSize = e.NewValue;
        ScheduleIconSizeCommit();
    }

    private void DecreaseIconSizeButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustIconSize(-IconSizeAdjustmentStep);
    }

    private void IncreaseIconSizeButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustIconSize(IconSizeAdjustmentStep);
    }

    private void AdjustIconSize(double delta)
    {
        var current = Math.Round(IconSizeSlider.Value);
        IconSizeSlider.Value = Math.Clamp(current + delta, IconSizeSlider.Minimum, IconSizeSlider.Maximum);
    }

    private void IconSizeSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isIconSizeDragActive = true;
        _iconSizeCommitTimer.Stop();
    }

    private async void IconSizeSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isIconSizeDragActive = false;
        _iconSizeCommitTimer.Stop();
        await CommitPendingIconSizeChangeAsync();
    }

    private void ScheduleIconSizeCommit()
    {
        if (_isIconSizeDragActive)
        {
            return;
        }

        _iconSizeCommitTimer.Stop();
        _iconSizeCommitTimer.Start();
    }

    private async void IconSizeCommitTimer_Tick(object? sender, EventArgs e)
    {
        _iconSizeCommitTimer.Stop();
        await CommitPendingIconSizeChangeAsync();
    }

    private async Task CommitPendingIconSizeChangeAsync()
    {
        if (_pendingIconSize is not { } iconSize)
        {
            return;
        }

        _pendingIconSize = null;
        _configService.Current.Dock.IconSize = iconSize;
        _dockWindow.ApplySettings();
        await _configService.SaveAsync();
    }

    private void FlushPendingIconSizeChange()
    {
        if (_pendingIconSize is not { } iconSize)
        {
            return;
        }

        _iconSizeCommitTimer.Stop();
        _pendingIconSize = null;
        _configService.Current.Dock.IconSize = iconSize;
        _dockWindow.ApplySettings();
        _ = _configService.SaveAsync();
    }

    private void EditModeButton_Click(object sender, RoutedEventArgs e)
    {
        _editMode = !_editMode;
        _dockWindow.SetEditMode(_editMode);
        UpdateEditModeButton();
    }

    private void UpdateEditModeButton()
    {
        EditModeButtonText.Text = _editMode ? "退出编辑模式" : "进入编辑模式";
    }

    private void LocalProfileLink_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            AppPaths.EnsureDirectories();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.AppDataDirectory,
                UseShellExecute = true
            });
            ShowStatus("本地文件夹已打开。", true);
        }
        catch (Exception ex)
        {
            LogLocalProfileOpenFailure(ex);
            ShowStatus("打开本地文件夹失败，请查看日志。", false);
        }
    }

    private static void LogLocalProfileOpenFailure(Exception ex)
    {
        try
        {
            AppPaths.EnsureDirectories();
            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Open local profile folder failed: {ex}{Environment.NewLine}");
        }
        catch
        {
            // Nothing else to report if the local paths cannot be created.
        }
    }

    private void ShowStatus(string message, bool success)
    {
        StatusText.Foreground = success
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.Khaki;
        StatusText.Text = message;
    }
}
