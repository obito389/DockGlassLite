using Microsoft.Win32;

namespace DockGlassLite.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DockGlassLite";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) is string;
    }

    public bool Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            return true;
        }
        catch (Exception ex)
        {
            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Startup enable failed: {ex}\n");
            return false;
        }
    }

    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.DeleteValue(ValueName, false);
            return true;
        }
        catch (Exception ex)
        {
            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Startup disable failed: {ex}\n");
            return false;
        }
    }
}
