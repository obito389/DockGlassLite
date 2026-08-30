using System.Diagnostics;

namespace DockGlassLite.Services;

public sealed class ForegroundWindowService
{
    public bool IsDockCovered(nint dockHwnd)
    {
        if (dockHwnd == nint.Zero || !NativeMethods.GetWindowRect(dockHwnd, out var dockRect))
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero || foreground == dockHwnd)
        {
            return false;
        }

        var className = NativeMethods.GetWindowClassName(foreground);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        if (IsShellOverlayWindow(foreground, className))
        {
            return false;
        }

        if (!NativeMethods.IsWindowVisible(foreground) ||
            !NativeMethods.GetWindowRect(foreground, out var foregroundRect))
        {
            return false;
        }

        return foregroundRect.Intersects(dockRect);
    }

    private static bool IsShellOverlayWindow(nint hwnd, string className)
    {
        if (className is "TaskListThumbnailWnd" or "TaskSwitcherWnd" or "Xaml_WindowedPopupClass")
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!IsShellProcess(process.ProcessName))
            {
                return false;
            }

            return className is not ("CabinetWClass" or "ExploreWClass");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsShellProcess(string processName)
    {
        return processName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase);
    }
}
