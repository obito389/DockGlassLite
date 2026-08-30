using System.Diagnostics;
using System.Runtime.InteropServices;
using DockGlassLite.Models;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DockGlassLite.Services;

public sealed class ShortcutService
{
    private const int IconCacheSize = 256;
    private const string IconCacheVersion = "v2";
    private static readonly TimeSpan LaunchSuppressionWindow = TimeSpan.FromSeconds(4);
    private readonly Dictionary<string, DateTimeOffset> _recentLaunches = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _launchSync = new();

    public async Task<DockItem?> CreateDockItemAsync(string path)
    {
        if (Directory.Exists(path))
        {
            return await CreateFolderDockItemAsync(path);
        }

        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            return await CreateShortcutDockItemAsync(path);
        }

        return null;
    }

    private async Task<DockItem> CreateShortcutDockItemAsync(string shortcutPath)
    {
        var details = ReadShortcut(shortcutPath);
        var name = Path.GetFileNameWithoutExtension(shortcutPath);
        var item = new DockItem
        {
            Kind = DockItemKind.Shortcut,
            Name = string.IsNullOrWhiteSpace(details.Name) ? name : details.Name,
            SourcePath = shortcutPath,
            ShortcutPath = shortcutPath,
            TargetPath = details.TargetPath,
            Arguments = details.Arguments,
            IconLocation = details.IconLocation
        };

        item.IconCachePath = await CacheIconAsync(item);
        return item;
    }

    private async Task<DockItem> CreateFolderDockItemAsync(string folderPath)
    {
        var directory = new DirectoryInfo(folderPath);
        var item = new DockItem
        {
            Kind = DockItemKind.Folder,
            Name = string.IsNullOrWhiteSpace(directory.Name) ? folderPath : directory.Name,
            SourcePath = folderPath,
            TargetPath = folderPath
        };

        item.IconCachePath = await CacheIconAsync(item);
        return item;
    }

    public async Task<bool> EnsureIconAsync(DockItem item)
    {
        if (HasCurrentIconCache(item))
        {
            return false;
        }

        var previousIconPath = item.IconCachePath;
        var refreshedIconPath = await CacheIconAsync(item);
        if (string.IsNullOrWhiteSpace(refreshedIconPath))
        {
            return false;
        }

        item.IconCachePath = refreshedIconPath;
        return !string.Equals(previousIconPath, refreshedIconPath, StringComparison.OrdinalIgnoreCase);
    }

    public bool Launch(DockItem item)
    {
        TryRelocateShortcut(item);

        if (item.IsMissing)
        {
            return false;
        }

        var hasExecutablePath = TryGetExecutablePath(item, out var executablePath);
        if (hasExecutablePath && TryUseExistingInstance(executablePath))
        {
            return true;
        }

        if (hasExecutablePath && WasRecentlyLaunched(executablePath))
        {
            return true;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = item.LaunchPath,
                UseShellExecute = true
            };

            if (!string.Equals(item.LaunchPath, item.EffectivePath, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.Arguments))
            {
                startInfo.Arguments = item.Arguments;
            }

            if (hasExecutablePath)
            {
                MarkLaunchRequested(executablePath);
            }

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            if (hasExecutablePath)
            {
                ClearLaunchRequest(executablePath);
            }

            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Launch failed: {ex}\n");
            return false;
        }
    }

    private static bool TryUseExistingInstance(string executablePath)
    {
        var processName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var matchingProcesses = Process.GetProcessesByName(processName)
            .Where(process => IsMatchingProcess(process, executablePath))
            .ToArray();
        if (matchingProcesses.Length == 0)
        {
            return false;
        }

        var windowHandle = FindMainWindowHandle(matchingProcesses);
        if (windowHandle != nint.Zero)
        {
            ActivateWindow(windowHandle);
        }

        return true;
    }

    private bool WasRecentlyLaunched(string executablePath)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_launchSync)
        {
            PruneRecentLaunches(now);
            return _recentLaunches.TryGetValue(executablePath, out var requestedAt)
                && now - requestedAt < LaunchSuppressionWindow;
        }
    }

    private void MarkLaunchRequested(string executablePath)
    {
        lock (_launchSync)
        {
            _recentLaunches[executablePath] = DateTimeOffset.UtcNow;
        }
    }

    private void ClearLaunchRequest(string executablePath)
    {
        lock (_launchSync)
        {
            _recentLaunches.Remove(executablePath);
        }
    }

    private void PruneRecentLaunches(DateTimeOffset now)
    {
        foreach (var launch in _recentLaunches.ToArray())
        {
            if (now - launch.Value > LaunchSuppressionWindow)
            {
                _recentLaunches.Remove(launch.Key);
            }
        }
    }

    private static bool TryGetExecutablePath(DockItem item, out string executablePath)
    {
        executablePath = "";
        var candidates = new[] { item.TargetPath, item.LaunchPath };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)
                || !Path.GetExtension(candidate).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(candidate))
            {
                continue;
            }

            executablePath = Path.GetFullPath(candidate);
            return true;
        }

        return false;
    }

    private static bool IsMatchingProcess(Process process, string executablePath)
    {
        try
        {
            return !process.HasExited
                && process.MainModule?.FileName is { } processPath
                && string.Equals(Path.GetFullPath(processPath), executablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return !process.HasExited;
        }
    }

    private static nint FindMainWindowHandle(IReadOnlyCollection<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                process.Refresh();
                if (process.MainWindowHandle != nint.Zero && NativeMethods.IsWindowVisible(process.MainWindowHandle))
                {
                    return process.MainWindowHandle;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        var processIds = processes.Select(process => process.Id).ToHashSet();
        var foundWindow = nint.Zero;
        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(windowHandle))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
            if (!processIds.Contains((int)processId))
            {
                return true;
            }

            foundWindow = windowHandle;
            return false;
        }, nint.Zero);

        return foundWindow;
    }

    private static void ActivateWindow(nint windowHandle)
    {
        if (NativeMethods.IsIconic(windowHandle))
        {
            NativeMethods.ShowWindow(windowHandle, NativeMethods.SwRestore);
        }

        NativeMethods.SetForegroundWindow(windowHandle);
    }

    public bool TryRelocateShortcut(DockItem item)
    {
        if (item.Kind != DockItemKind.Shortcut || File.Exists(item.EffectivePath))
        {
            return false;
        }

        var fileName = GetShortcutFileName(item);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        foreach (var candidatePath in FindDesktopShortcutCandidates(fileName))
        {
            var details = ReadShortcut(candidatePath);
            if (!IsMatchingRelocatedShortcut(item, details.TargetPath))
            {
                continue;
            }

            item.SourcePath = candidatePath;
            item.ShortcutPath = candidatePath;
            if (!string.IsNullOrWhiteSpace(details.TargetPath))
            {
                item.TargetPath = details.TargetPath;
            }

            item.Arguments = details.Arguments;
            item.IconLocation = details.IconLocation;
            return true;
        }

        return false;
    }

    private static (string Name, string TargetPath, string Arguments, string IconLocation) ReadShortcut(string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return ("", "", "", "");
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath]);

            var type = shortcut!.GetType();
            var targetPath = type.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)?.ToString() ?? "";
            var arguments = type.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)?.ToString() ?? "";
            var description = type.InvokeMember("Description", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)?.ToString() ?? "";
            var iconLocation = type.InvokeMember("IconLocation", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)?.ToString() ?? "";
            return (description, targetPath, arguments, iconLocation);
        }
        catch (Exception ex)
        {
            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Shortcut read failed: {ex}\n");
            return ("", "", "", "");
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static string GetShortcutFileName(DockItem item)
    {
        var path = !string.IsNullOrWhiteSpace(item.ShortcutPath) ? item.ShortcutPath : item.SourcePath;
        return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path);
    }

    private static bool IsMatchingRelocatedShortcut(DockItem item, string relocatedTargetPath)
    {
        return string.IsNullOrWhiteSpace(item.TargetPath)
            || string.IsNullOrWhiteSpace(relocatedTargetPath)
            || string.Equals(item.TargetPath, relocatedTargetPath, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> FindDesktopShortcutCandidates(string fileName)
    {
        var desktopRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var desktopRoot in desktopRoots)
        {
            foreach (var candidatePath in EnumerateShortcutCandidates(desktopRoot, fileName))
            {
                yield return candidatePath;
            }
        }
    }

    private static IEnumerable<string> EnumerateShortcutCandidates(string desktopRoot, string fileName)
    {
        if (!Directory.Exists(desktopRoot))
        {
            yield break;
        }

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(desktopRoot, fileName, SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var candidate in candidates)
        {
            yield return candidate;
        }
    }

    private static Task<string> CacheIconAsync(DockItem item)
    {
        AppPaths.EnsureDirectories();

        var iconSources = GetIconSources(item).ToArray();
        if (iconSources.Length == 0)
        {
            return Task.FromResult("");
        }

        var iconPath = Path.Combine(AppPaths.IconCacheDirectory, GetIconCacheFileName(item));

        try
        {
            if (item.Kind == DockItemKind.Folder)
            {
                foreach (var iconSource in iconSources)
                {
                    if (TryCacheShellImage(iconSource.Path, iconPath) || TryCacheAssociatedIcon(iconSource.Path, iconPath))
                    {
                        return Task.FromResult(iconPath);
                    }
                }

                File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Icon extract failed: no usable folder icon source for {item.Name}.\n");
                return Task.FromResult("");
            }

            foreach (var iconSource in iconSources)
            {
                if (TryCacheResourceIcon(iconSource, iconPath))
                {
                    return Task.FromResult(iconPath);
                }
            }

            foreach (var iconSource in iconSources)
            {
                if (TryCacheAssociatedIcon(iconSource.Path, iconPath))
                {
                    return Task.FromResult(iconPath);
                }
            }

            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Icon extract failed: no usable icon source for {item.Name}.\n");
            return Task.FromResult("");
        }
        catch (Exception ex)
        {
            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Icon cache failed for {item.Name}: {ex}\n");
            return Task.FromResult("");
        }
    }

    private static bool TryCacheResourceIcon(IconSource iconSource, string iconPath)
    {
        var icons = new nint[1];
        var iconIds = new uint[1];

        try
        {
            var extracted = NativeMethods.PrivateExtractIcons(
                iconSource.Path,
                iconSource.Index,
                IconCacheSize,
                IconCacheSize,
                icons,
                iconIds,
                1,
                0);

            if (extracted == 0 || icons[0] == nint.Zero)
            {
                return false;
            }

            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                icons[0],
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(IconCacheSize, IconCacheSize));
            bitmap.Freeze();

            SavePng(bitmap, iconPath);
            return true;
        }
        catch (Exception ex)
        {
            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Resource icon extract failed for {iconSource.Path}: {ex}\n");
            return false;
        }
        finally
        {
            if (icons[0] != nint.Zero)
            {
                NativeMethods.DestroyIcon(icons[0]);
            }
        }
    }

    private static bool TryCacheAssociatedIcon(string iconSource, string iconPath)
    {
        try
        {
            const uint shgfiIcon = 0x000000100;
            const uint shgfiLargeIcon = 0x000000000;
            var result = NativeMethods.SHGetFileInfo(
                iconSource,
                0,
                out var info,
                (uint)Marshal.SizeOf<ShFileInfo>(),
                shgfiIcon | shgfiLargeIcon);

            if (result == nint.Zero || info.hIcon == nint.Zero)
            {
                File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Icon extract failed for {iconSource}: SHGetFileInfo returned no icon.\n");
                return false;
            }

            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(IconCacheSize, IconCacheSize));
                bitmap.Freeze();

                SavePng(bitmap, iconPath);
                return true;
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Associated icon extract failed for {iconSource}: {ex}\n");
            return false;
        }
    }

    private static bool TryCacheShellImage(string iconSource, string iconPath)
    {
        IShellItemImageFactory? imageFactory = null;
        var hBitmap = nint.Zero;

        try
        {
            var interfaceId = typeof(IShellItemImageFactory).GUID;
            NativeMethods.SHCreateItemFromParsingName(
                iconSource,
                nint.Zero,
                ref interfaceId,
                out imageFactory);

            imageFactory.GetImage(
                new NativeSize(IconCacheSize, IconCacheSize),
                ShellItemImageFactoryFlags.IconOnly | ShellItemImageFactoryFlags.BiggerSizeOk | ShellItemImageFactoryFlags.ScaleUp,
                out hBitmap);

            if (hBitmap == nint.Zero)
            {
                return false;
            }

            var bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                nint.Zero,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(IconCacheSize, IconCacheSize));
            bitmap.Freeze();

            SavePng(bitmap, iconPath);
            return true;
        }
        catch (Exception ex)
        {
            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} High resolution icon extract failed for {iconSource}: {ex}\n");
            return false;
        }
        finally
        {
            if (hBitmap != nint.Zero)
            {
                NativeMethods.DeleteObject(hBitmap);
            }

            if (imageFactory is not null && Marshal.IsComObject(imageFactory))
            {
                Marshal.FinalReleaseComObject(imageFactory);
            }
        }
    }

    private static void SavePng(BitmapSource bitmap, string iconPath)
    {
        using var stream = File.Create(iconPath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private static IEnumerable<IconSource> GetIconSources(DockItem item)
    {
        if (item.Kind == DockItemKind.Folder)
        {
            var folderPath = item.EffectivePath;
            if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            {
                yield return new IconSource(folderPath, 0);
            }

            yield break;
        }

        var iconLocation = GetIconLocationSource(item.IconLocation);
        if (iconLocation is not null)
        {
            yield return iconLocation.Value;
        }

        if (!string.IsNullOrWhiteSpace(item.TargetPath) && File.Exists(item.TargetPath))
        {
            yield return new IconSource(item.TargetPath, 0);
        }

        if (!string.IsNullOrWhiteSpace(item.ShortcutPath) && File.Exists(item.ShortcutPath))
        {
            yield return new IconSource(item.ShortcutPath, 0);
        }

        if (!string.IsNullOrWhiteSpace(item.SourcePath)
            && File.Exists(item.SourcePath)
            && !string.Equals(item.SourcePath, item.ShortcutPath, StringComparison.OrdinalIgnoreCase))
        {
            yield return new IconSource(item.SourcePath, 0);
        }
    }

    private static IconSource? GetIconLocationSource(string iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(iconLocation.Trim().Trim('"'));
        var iconIndex = 0;
        var commaIndex = expanded.LastIndexOf(',');
        if (commaIndex > 0 && int.TryParse(expanded[(commaIndex + 1)..], out _))
        {
            iconIndex = int.Parse(expanded[(commaIndex + 1)..]);
            expanded = expanded[..commaIndex].Trim().Trim('"');
        }

        return File.Exists(expanded)
            ? new IconSource(expanded, iconIndex)
            : null;
    }

    private static bool HasCurrentIconCache(DockItem item)
    {
        return !string.IsNullOrWhiteSpace(item.IconCachePath)
            && File.Exists(item.IconCachePath)
            && string.Equals(Path.GetFileName(item.IconCachePath), GetIconCacheFileName(item), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetIconCacheFileName(DockItem item)
    {
        return $"{item.Id}-{IconCacheSize}-{IconCacheVersion}.png";
    }

    private readonly record struct IconSource(string Path, int Index);
}
