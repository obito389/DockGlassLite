namespace DockGlassLite.Services;

public static class AppPaths
{
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DockGlassLite");

    public static string IconCacheDirectory { get; } = Path.Combine(AppDataDirectory, "IconCache");
    public static string ConfigFilePath { get; } = Path.Combine(AppDataDirectory, "config.json");
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "桌面美化",
        "DockGlassLite-Logs");

    public static string LogFilePath { get; } = Path.Combine(LogDirectory, "dockglass-lite.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(IconCacheDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
