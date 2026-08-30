using DockGlassLite.Models;
using DockGlassLite.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DockGlassLite.Views;

public sealed partial class DockWindow : Window
{
    private readonly ConfigService _configService;
    private readonly ShortcutService _shortcutService;
    private readonly ForegroundWindowService _foregroundWindowService;
    private readonly DispatcherTimer _coverTimer;
    private readonly DispatcherTimer _magnificationTimer;
    private readonly DispatcherTimer _displaySettingsTimer;
    private const string HoverScaleTargetResourceKey = "HoverScaleTarget";
    private const string IsSettingsButtonResourceKey = "IsSettingsButton";
    private const string OpacityTargetResourceKey = "OpacityTarget";
    private const double MaxMagnificationScale = 1.34;
    private const double MagnificationRadiusFactor = 3.8;
    private const double MinMagnificationRadius = 160;
    private const double MagnificationFalloffContrast = 1.25;
    private const double SettingsIdleScale = 0.7;
    private const double SettingsActiveScale = 0.96;
    private const double SettingsIdleOpacity = 0.32;
    private const double SettingsActiveOpacity = 0.74;
    private const double SettingsRadiusFactor = 1.55;
    private const double MinSettingsRadius = 72;
    private const double HandoffZoneFactor = 0.34;
    private const double MinHandoffZoneWidth = 18;
    private const double MaxHandoffZoneWidth = 26;
    private const double DockHorizontalScreenPadding = 48;
    private const double DockBottomScreenPadding = 12;
    private nint _hwnd;
    private double _layoutIconSize;
    private bool _isInactive;
    private bool _isEditMode;

    public event EventHandler? SettingsRequested;

    public ObservableCollection<DockItem> DockItems { get; } = [];

    public DockWindow(
        ConfigService configService,
        ShortcutService shortcutService,
        ForegroundWindowService foregroundWindowService)
    {
        InitializeComponent();

        _configService = configService;
        _shortcutService = shortcutService;
        _foregroundWindowService = foregroundWindowService;
        _layoutIconSize = _configService.Current.Dock.IconSize;

        _magnificationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _magnificationTimer.Tick += (_, _) => UpdateGlobalMouseMagnification();

        _displaySettingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _displaySettingsTimer.Tick += DisplaySettingsTimer_Tick;
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _displaySettingsTimer.Stop();
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ConfigureWindow();
            MoveToBottomCenter();
            _magnificationTimer.Start();
        };

        LoadItems();
        RenderItems();
        _ = BackfillMissingIconsAsync();

        _coverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _coverTimer.Tick += (_, _) => UpdateInactiveState();
        _coverTimer.Start();
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ScheduleDisplaySettingsRefresh();
            return;
        }

        Dispatcher.BeginInvoke(ScheduleDisplaySettingsRefresh);
    }

    private void ScheduleDisplaySettingsRefresh()
    {
        _displaySettingsTimer.Stop();
        _displaySettingsTimer.Start();
    }

    private void DisplaySettingsTimer_Tick(object? sender, EventArgs e)
    {
        _displaySettingsTimer.Stop();
        ClearIconSizePreview();
        MoveToBottomCenter();
        UpdateInactiveState();
    }

    public void ApplySettings()
    {
        _layoutIconSize = _configService.Current.Dock.IconSize;
        ClearIconSizePreview();
        RenderItems();
        MoveToBottomCenter(_layoutIconSize);
    }

    public void PreviewIconSize(double iconSize)
    {
        var previewIconSize = Math.Clamp(iconSize, 32, 72);
        var layoutIconSize = _layoutIconSize > 0 ? _layoutIconSize : _configService.Current.Dock.IconSize;
        var scale = previewIconSize / layoutIconSize;

        DockLayoutHost.RenderTransformOrigin = new Point(0.5, 1.0);
        DockLayoutHost.RenderTransform = Math.Abs(scale - 1) < 0.001
            ? Transform.Identity
            : new ScaleTransform(scale, scale);

        MoveToBottomCenter(previewIconSize);
    }

    public void SetEditMode(bool isEditMode)
    {
        if (_isEditMode == isEditMode)
        {
            return;
        }

        _isEditMode = isEditMode;
        RootGrid.AllowDrop = _isEditMode;
        ClearIconSizePreview();
        RenderItems();
        MoveToBottomCenter();
    }

    private void ConfigureWindow()
    {
        if (_hwnd == nint.Zero)
        {
            return;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle).ToInt64();
        var newExStyle = new nint(exStyle | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GwlExStyle, newExStyle);

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HwndNoTopMost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
    }

    private void LoadItems()
    {
        DockItems.Clear();
        foreach (var item in _configService.Current.DockItems)
        {
            DockItems.Add(item);
        }
    }

    private void RenderItems()
    {
        ItemsHost.Children.Clear();
        ToolsHost.Children.Clear();

        foreach (var item in DockItems)
        {
            ItemsHost.Children.Add(CreateDockButton(item));
        }

        ToolsHost.Children.Add(CreateSettingsButton());
        if (_isEditMode)
        {
            ToolsHost.Children.Add(CreateAddShortcutButton());
        }

        UpdateToolBalanceWidth(_configService.Current.Dock.IconSize);
    }

    private Button CreateDockButton(DockItem item)
    {
        var iconSize = _configService.Current.Dock.IconSize;
        var hoverHeadroom = GetHoverHeadroom(iconSize);
        var reflectionHeight = GetReflectionHeight(iconSize);
        var sideRoom = GetHoverSideRoom(iconSize);
        var cellWidth = GetDockCellWidth(iconSize);
        var itemMargin = GetDockItemMargin(iconSize);
        var initial = string.IsNullOrWhiteSpace(item.Name) ? "?" : item.Name[..1].ToUpperInvariant();

        var glyph = new Border
        {
            Width = iconSize,
            Height = iconSize,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Child = CreateIconContent(item, iconSize, initial)
        };

        var content = new Grid
        {
            Width = cellWidth,
            Height = hoverHeadroom + iconSize + reflectionHeight,
            Margin = new Thickness(itemMargin, 0, itemMargin, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            ClipToBounds = true
        };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(hoverHeadroom) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(iconSize) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(reflectionHeight) });

        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Bottom;
        var hoverScaleTarget = CreateHoverScaleHost(glyph, iconSize);
        Grid.SetRow(hoverScaleTarget, 1);

        var reflection = CreateIconReflection(glyph, iconSize, reflectionHeight, item.IsMissing);
        Grid.SetRow(reflection, 2);

        content.Children.Add(hoverScaleTarget);
        content.Children.Add(reflection);
        if (_isEditMode)
        {
            content.Children.Add(CreateDeleteBadge(item, iconSize, sideRoom));
        }

        var button = new Button
        {
            Tag = item,
            Content = content,
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            IsEnabled = !item.IsMissing || _isEditMode,
            ToolTip = item.IsMissing ? GetMissingItemToolTip(item) : item.Name
        };

        button.Resources[HoverScaleTargetResourceKey] = hoverScaleTarget;
        button.Click += DockButton_Click;
        return button;
    }

    private Border CreateDeleteBadge(DockItem item, double iconSize, double sideRoom)
    {
        var badgeSize = Math.Clamp(iconSize * 0.34, 16, 22);
        var badge = new Border
        {
            Tag = item,
            Width = badgeSize,
            Height = badgeSize,
            CornerRadius = new CornerRadius(badgeSize / 2),
            Background = new SolidColorBrush(Color.FromRgb(226, 72, 84)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, Math.Max(1, sideRoom - 2), 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "x",
                Foreground = Brushes.White,
                FontSize = Math.Max(10, badgeSize * 0.62),
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };

        Grid.SetRow(badge, 1);
        badge.PreviewMouseLeftButtonDown += DeleteBadge_PreviewMouseLeftButtonDown;
        return badge;
    }

    private Button CreateAddShortcutButton()
    {
        var iconSize = _configService.Current.Dock.IconSize;
        var hoverHeadroom = GetHoverHeadroom(iconSize);
        var reflectionHeight = GetReflectionHeight(iconSize);
        var cellWidth = GetDockCellWidth(iconSize);
        var itemMargin = GetDockItemMargin(iconSize);
        var glyph = CreateCircleIconContent(iconSize, "+", new SolidColorBrush(Color.FromArgb(52, 255, 255, 255)), Brushes.White, 0.58);

        var content = new Grid
        {
            Width = cellWidth,
            Height = hoverHeadroom + iconSize + reflectionHeight,
            Margin = new Thickness(itemMargin, 0, itemMargin, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            ClipToBounds = true
        };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(hoverHeadroom) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(iconSize) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(reflectionHeight) });

        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetRow(glyph, 1);
        content.Children.Add(glyph);

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            ToolTip = "\u6DFB\u52A0\u5FEB\u6377\u65B9\u5F0F"
        };

        button.Resources[HoverScaleTargetResourceKey] = glyph;
        button.Click += AddShortcutButton_Click;
        return button;
    }

    private Button CreateSettingsButton()
    {
        var iconSize = _configService.Current.Dock.IconSize;
        var hoverHeadroom = GetHoverHeadroom(iconSize);
        var reflectionHeight = GetReflectionHeight(iconSize);
        var cellWidth = GetDockCellWidth(iconSize);
        var itemMargin = GetDockItemMargin(iconSize);

        var glyph = CreateSettingsIconContent(iconSize);

        var content = new Grid
        {
            Width = cellWidth,
            Height = hoverHeadroom + iconSize + reflectionHeight,
            Margin = new Thickness(itemMargin, 0, itemMargin, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            ClipToBounds = true
        };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(hoverHeadroom) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(iconSize) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(reflectionHeight) });

        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetRow(glyph, 1);

        content.Children.Add(glyph);

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            ToolTip = "设置"
        };

        button.Resources[IsSettingsButtonResourceKey] = true;
        button.Resources[HoverScaleTargetResourceKey] = glyph;
        button.Resources[OpacityTargetResourceKey] = glyph;
        ApplySettingsButtonVisual(button, SettingsIdleScale, SettingsIdleOpacity);
        button.Click += SettingsButton_Click;
        return button;
    }

    private static Grid CreateHoverScaleHost(UIElement child, double iconSize)
    {
        var host = new Grid
        {
            Width = iconSize,
            Height = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransformOrigin = new Point(0.5, 1.0)
        };

        host.Children.Add(child);
        return host;
    }

    private static System.Windows.Shapes.Rectangle CreateIconReflection(Visual source, double iconSize, double reflectionHeight, bool isMissing)
    {
        var mirrorTransform = new TransformGroup();
        mirrorTransform.Children.Add(new ScaleTransform(1, -1, 0.5, 0.5));

        return new System.Windows.Shapes.Rectangle
        {
            Width = iconSize,
            Height = reflectionHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = isMissing ? 0.14 : 0.32,
            Fill = new VisualBrush(source)
            {
                Stretch = Stretch.Fill,
                RelativeTransform = mirrorTransform
            },
            OpacityMask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(210, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(72, 255, 255, 255), 0.42),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)
                ]
            }
        };
    }

    private static UIElement CreateIconContent(DockItem item, double iconSize, string fallbackText)
    {
        if (!string.IsNullOrWhiteSpace(item.IconCachePath) && File.Exists(item.IconCachePath))
        {
            var image = new Image
            {
                Source = LoadIconBitmap(item.IconCachePath),
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        return new TextBlock
        {
            Text = fallbackText,
            Foreground = Brushes.White,
            FontSize = Math.Max(16, iconSize * 0.42),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    // Material 风格的八齿齿轮（24x24 视图框）
    private const string SettingsGearPathData =
        "M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.07-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61" +
        "l-1.92-3.32c-0.12-0.22-0.37-0.29-0.59-0.22l-2.39,0.96c-0.5-0.38-1.03-0.7-1.62-0.94L14.4,2.81c-0.04-0.24-0.24-0.41-0.48-0.41" +
        "h-3.84c-0.24,0-0.43,0.17-0.47,0.41L9.25,5.35C8.66,5.59,8.12,5.92,7.63,6.29L5.24,5.33c-0.22-0.08-0.47,0-0.59,0.22L2.74,8.87" +
        "C2.62,9.08,2.66,9.34,2.86,9.48l2.03,1.58C4.84,11.36,4.8,11.69,4.8,12s0.02,0.64,0.07,0.94l-2.03,1.58" +
        "c-0.18,0.14-0.23,0.41-0.12,0.61l1.92,3.32c0.12,0.22,0.37,0.29,0.59,0.22l2.39-0.96c0.5,0.38,1.03,0.7,1.62,0.94l0.36,2.54" +
        "c0.05,0.24,0.24,0.41,0.48,0.41h3.84c0.24,0,0.44-0.17,0.47-0.41l0.36-2.54c0.59-0.24,1.13-0.56,1.62-0.94l2.39,0.96" +
        "c0.22,0.08,0.47,0,0.59-0.22l1.92-3.32c0.12-0.22,0.07-0.47-0.12-0.61L19.14,12.94z " +
        "M12,15.6c-1.98,0-3.6-1.62-3.6-3.6s1.62-3.6,3.6-3.6s3.6,1.62,3.6,3.6S13.98,15.6,12,15.6z";

    private static FrameworkElement CreateSettingsIconContent(double iconSize)
    {
        var icon = new Grid
        {
            Width = iconSize,
            Height = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var gearSize = Math.Max(20, iconSize * 0.62);
        icon.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(SettingsGearPathData),
            Fill = new SolidColorBrush(Color.FromRgb(226, 233, 244)),
            Stretch = Stretch.Uniform,
            Width = gearSize,
            Height = gearSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                BlurRadius = 4,
                ShadowDepth = 1,
                Opacity = 0.35,
                Color = Colors.Black
            }
        });

        return icon;
    }

    private static FrameworkElement CreateCircleIconContent(
        double iconSize,
        string glyph,
        Brush fill,
        Brush foreground,
        double fontScale,
        string? fontFamily = null)
    {
        var circle = new Grid
        {
            Width = iconSize,
            Height = iconSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        circle.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = iconSize,
            Height = iconSize,
            Fill = fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        var textBlock = new TextBlock
        {
            Text = glyph,
            Foreground = foreground,
            FontSize = Math.Max(18, iconSize * fontScale),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            textBlock.FontFamily = new FontFamily(fontFamily);
        }

        circle.Children.Add(textBlock);

        return circle;
    }

    private static BitmapImage LoadIconBitmap(string iconCachePath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(iconCachePath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async Task BackfillMissingIconsAsync()
    {
        var changed = false;
        foreach (var item in DockItems)
        {
            changed |= _shortcutService.TryRelocateShortcut(item);
            changed |= await _shortcutService.EnsureIconAsync(item);
        }

        if (!changed)
        {
            return;
        }

        await _configService.SaveAsync();
        RenderItems();
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (_isInactive || !_isEditMode || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Handled = true;
            return;
        }

        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files is null)
        {
            e.Handled = true;
            return;
        }

        await AddDockItemPathsAsync(files);
        e.Handled = true;
    }

    private async Task AddDockItemPathsAsync(IEnumerable<string> paths)
    {
        var changed = false;
        foreach (var path in paths)
        {
            if (!IsSupportedDockItemPath(path))
            {
                continue;
            }

            if (DockItems.Any(existing => IsSameDockItemPath(existing, path)))
            {
                continue;
            }

            var item = await _shortcutService.CreateDockItemAsync(path);
            if (item is null)
            {
                continue;
            }

            DockItems.Add(item);
            _configService.Current.DockItems.Add(item);
            changed = true;
        }

        if (changed)
        {
            await _configService.SaveAsync();
            RenderItems();
            MoveToBottomCenter();
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_isInactive && _isEditMode && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInactive || _isEditMode || sender is not Button { Tag: DockItem item })
        {
            return;
        }

        var relocated = _shortcutService.TryRelocateShortcut(item);
        _shortcutService.Launch(item);
        if (relocated)
        {
            await _configService.SaveAsync();
            RenderItems();
        }
    }

    private async void AddShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInactive)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "\u9009\u62E9\u5FEB\u6377\u65B9\u5F0F",
            Filter = "\u5FEB\u6377\u65B9\u5F0F (*.lnk)|*.lnk",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await AddDockItemPathsAsync(dialog.FileNames);
        }
    }

    private static bool IsSupportedDockItemPath(string path)
    {
        return Directory.Exists(path)
            || (File.Exists(path) && Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameDockItemPath(DockItem item, string path)
    {
        return string.Equals(item.EffectivePath, path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.ShortcutPath, path, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMissingItemToolTip(DockItem item)
    {
        return item.Kind == DockItemKind.Folder
            ? $"{item.Name} 文件夹已失效"
            : $"{item.Name} 快捷方式已失效";
    }

    private async void DeleteBadge_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isInactive || !_isEditMode || sender is not FrameworkElement { Tag: DockItem item })
        {
            return;
        }

        await RemoveDockItemAsync(item);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInactive)
        {
            return;
        }

        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task RemoveDockItemAsync(DockItem item)
    {
        DockItems.Remove(item);
        _configService.Current.DockItems.RemoveAll(existing => existing.Id == item.Id);
        await _configService.SaveAsync();
        RenderItems();
        MoveToBottomCenter();
    }

    private void UpdateGlobalMouseMagnification()
    {
        if (_isInactive || _isEditMode || !IsVisible || !GetDockButtons().Any())
        {
            ResetMagnification();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursorPoint))
        {
            ResetMagnification();
            return;
        }

        ApplyProximityMagnification(RootGrid.PointFromScreen(new Point(cursorPoint.X, cursorPoint.Y)));
    }

    private void ApplyProximityMagnification(Point mousePoint)
    {
        var iconSize = _configService.Current.Dock.IconSize;
        var radius = Math.Max(MinMagnificationRadius, iconSize * MagnificationRadiusFactor);
        var handoffProgress = CalculateHandoffProgress(mousePoint, iconSize);
        var settingsInfluence = handoffProgress > 0.001
            ? ApplySettingsButtonsProximity(mousePoint, iconSize, handoffProgress)
            : ResetSettingsButtons();
        var shortcutInfluence = 1.0 - Math.Max(handoffProgress, settingsInfluence);

        foreach (var button in ItemsHost.Children.OfType<Button>())
        {
            var target = GetHoverScaleTarget(button);
            if (!target.IsVisible || target.ActualWidth <= 0)
            {
                continue;
            }

            var center = target.TransformToAncestor(RootGrid).Transform(new Point(target.ActualWidth / 2, target.ActualHeight));
            var distance = GetDistance(mousePoint, center);
            var rawScale = CalculateMagnificationScale(distance, radius);
            var scale = Lerp(1.0, rawScale, shortcutInfluence);

            target.RenderTransformOrigin = new Point(0.5, 1.0);
            target.RenderTransform = scale <= 1.001
                ? Transform.Identity
                : new ScaleTransform(scale, scale);
        }
    }

    private double ApplySettingsButtonsProximity(Point mousePoint, double iconSize, double handoffProgress)
    {
        var strongestInfluence = 0.0;
        foreach (var button in ToolsHost.Children.OfType<Button>().Where(IsSettingsButton))
        {
            var target = GetHoverScaleTarget(button);
            if (!target.IsVisible || target.ActualWidth <= 0)
            {
                continue;
            }

            var center = target.TransformToAncestor(RootGrid).Transform(new Point(target.ActualWidth / 2, target.ActualHeight));
            var distance = GetDistance(mousePoint, center);
            strongestInfluence = Math.Max(strongestInfluence, ApplySettingsButtonProximity(button, distance, iconSize, handoffProgress));
        }

        return strongestInfluence;
    }

    private double ResetSettingsButtons()
    {
        foreach (var button in ToolsHost.Children.OfType<Button>().Where(IsSettingsButton))
        {
            ApplySettingsButtonVisual(button, SettingsIdleScale, SettingsIdleOpacity);
        }

        return 0.0;
    }

    private double CalculateHandoffProgress(Point mousePoint, double iconSize)
    {
        if (!TryGetElementBounds(DockLayoutHost, out var layoutBounds)
            || !TryGetElementBounds(ToolsHost, out var toolsBounds))
        {
            return 0.0;
        }

        var verticalPadding = Math.Max(8, iconSize * 0.16);
        if (mousePoint.Y < layoutBounds.Top - verticalPadding
            || mousePoint.Y > layoutBounds.Bottom + verticalPadding)
        {
            return 0.0;
        }

        var zoneWidth = Math.Clamp(iconSize * HandoffZoneFactor, MinHandoffZoneWidth, MaxHandoffZoneWidth);
        var start = toolsBounds.Left - zoneWidth;
        var end = toolsBounds.Left + zoneWidth;
        return SmoothStep((mousePoint.X - start) / (end - start));
    }

    private static double GetDistance(Point first, Point second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static double CalculateMagnificationScale(double distance, double radius)
    {
        var influence = CalculateProximityInfluence(distance, radius);
        return 1.0 + (MaxMagnificationScale - 1.0) * influence;
    }

    private static double CalculateProximityInfluence(double distance, double radius)
    {
        if (distance >= radius)
        {
            return 0.0;
        }

        var normalized = Math.Clamp(distance / radius, 0, 1);
        var influence = 0.5 + 0.5 * Math.Cos(normalized * Math.PI);
        return Math.Clamp(
            0.5 + ((influence - 0.5) * MagnificationFalloffContrast),
            0,
            1);
    }

    private void ResetMagnification()
    {
        foreach (var child in GetDockButtons())
        {
            child.RenderTransform = Transform.Identity;
            if (IsSettingsButton(child))
            {
                ApplySettingsButtonVisual(child, SettingsIdleScale, SettingsIdleOpacity);
            }
            else
            {
                GetHoverScaleTarget(child).RenderTransform = Transform.Identity;
            }
        }
    }

    private IEnumerable<Button> GetDockButtons()
    {
        return ItemsHost.Children.OfType<Button>().Concat(ToolsHost.Children.OfType<Button>());
    }

    private static bool IsSettingsButton(Button button)
    {
        return button.Resources.Contains(IsSettingsButtonResourceKey)
            && button.Resources[IsSettingsButtonResourceKey] is true;
    }

    private bool TryGetElementBounds(FrameworkElement element, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var topLeft = element.TransformToAncestor(RootGrid).Transform(new Point());
            bounds = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static double ApplySettingsButtonProximity(Button button, double distance, double iconSize, double handoffProgress)
    {
        var radius = Math.Max(MinSettingsRadius, iconSize * SettingsRadiusFactor);
        var influence = CalculateSettingsProximityInfluence(distance, radius) * handoffProgress;
        var scale = Lerp(SettingsIdleScale, SettingsActiveScale, influence);
        var opacity = Lerp(SettingsIdleOpacity, SettingsActiveOpacity, influence);

        ApplySettingsButtonVisual(button, scale, opacity);
        return influence;
    }

    private static double CalculateSettingsProximityInfluence(double distance, double radius)
    {
        if (distance >= radius)
        {
            return 0.0;
        }

        return SmoothStep(1.0 - (distance / radius));
    }

    private static void ApplySettingsButtonVisual(Button button, double scale, double opacity)
    {
        var target = GetHoverScaleTarget(button);
        target.RenderTransformOrigin = new Point(0.5, 1.0);
        target.RenderTransform = new ScaleTransform(scale, scale);

        if (button.Resources.Contains(OpacityTargetResourceKey)
            && button.Resources[OpacityTargetResourceKey] is UIElement opacityTarget)
        {
            opacityTarget.Opacity = opacity;
        }
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * amount);
    }

    private static double SmoothStep(double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return t * t * (3 - (2 * t));
    }

    private static FrameworkElement GetHoverScaleTarget(Button button)
    {
        return button.Resources.Contains(HoverScaleTargetResourceKey)
            && button.Resources[HoverScaleTargetResourceKey] is FrameworkElement target
                ? target
                : button;
    }

    private void UpdateInactiveState()
    {
        if (_hwnd == nint.Zero)
        {
            return;
        }

        var covered = _foregroundWindowService.IsDockCovered(_hwnd);
        if (covered == _isInactive)
        {
            return;
        }

        _isInactive = covered;
        DockSurface.Opacity = _isInactive ? 0.42 : 1.0;
        ResetMagnification();
    }

    private void MoveToBottomCenter()
    {
        MoveToBottomCenter(_configService.Current.Dock.IconSize);
    }

    private void MoveToBottomCenter(double iconSize)
    {
        var hoverHeadroom = GetHoverHeadroom(iconSize);
        var reflectionHeight = GetReflectionHeight(iconSize);
        var shortcutTrackWidth = GetDockTrackWidth(iconSize, Math.Max(1, DockItems.Count));
        var toolTrackWidth = GetToolTrackWidth(iconSize);
        var screenLeft = 0;
        var screenTop = 0;
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        var bottomReservedHeight = GetBottomScreenReservedHeight(screenHeight);
        var maxWidth = Math.Max(220, screenWidth - DockHorizontalScreenPadding);
        var width = Math.Clamp((int)(shortcutTrackWidth + (toolTrackWidth * 2) + 52), 220, (int)maxWidth);
        var height = Math.Clamp((int)(hoverHeadroom + iconSize + reflectionHeight + 28), 104, 188);

        UpdateToolBalanceWidth(iconSize);

        Width = width;
        Height = height;

        Left = screenLeft + (screenWidth - width) / 2;
        Top = screenTop + screenHeight - bottomReservedHeight - height - DockBottomScreenPadding;
    }

    private double GetBottomScreenReservedHeight(double screenHeight)
    {
        if (!NativeMethods.TryGetTaskbarPosition(out var taskbarPosition)
            || taskbarPosition.Edge != NativeMethods.AbeBottom
            || taskbarPosition.Rect.Height <= 0)
        {
            return 0;
        }

        var height = DeviceHeightToDip(taskbarPosition.Rect.Height);
        return Math.Clamp(height, 0, screenHeight / 3);
    }

    private double DeviceHeightToDip(double height)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return height;
        }

        return source.CompositionTarget.TransformFromDevice.Transform(new Point(0, height)).Y;
    }

    private void ClearIconSizePreview()
    {
        DockLayoutHost.RenderTransform = Transform.Identity;
        UpdateToolBalanceWidth(_layoutIconSize);
    }

    private static double GetReflectionHeight(double iconSize)
    {
        return Math.Clamp(iconSize * 0.38, 14, 28);
    }

    private static double GetHoverHeadroom(double iconSize)
    {
        return Math.Clamp(iconSize * (MaxMagnificationScale - 1.0) + 2, 14, 30);
    }

    private static double GetHoverSideRoom(double iconSize)
    {
        return Math.Max(12, (iconSize * (MaxMagnificationScale - 1.0) / 2) + 6);
    }

    private static double GetDockCellWidth(double iconSize)
    {
        return iconSize + (GetHoverSideRoom(iconSize) * 2);
    }

    private static double GetDockItemMargin(double iconSize)
    {
        return Math.Clamp(iconSize * 0.05, 4, 8);
    }

    private double GetToolTrackWidth(double iconSize)
    {
        var toolCount = 1 + (_isEditMode ? 1 : 0);
        return GetDockTrackWidth(iconSize, toolCount);
    }

    private void UpdateToolBalanceWidth(double iconSize)
    {
        ToolBalanceSpacer.Width = GetToolTrackWidth(iconSize);
    }

    private static double GetDockTrackWidth(double iconSize, int itemCount)
    {
        var itemOuterWidth = GetDockCellWidth(iconSize) + (GetDockItemMargin(iconSize) * 2);
        return itemOuterWidth * itemCount;
    }

    private static double GetBaselineTransformOriginY(double iconSize)
    {
        var reflectionHeight = GetReflectionHeight(iconSize);
        return iconSize / (iconSize + reflectionHeight);
    }
}
