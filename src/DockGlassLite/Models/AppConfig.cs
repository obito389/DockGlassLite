using System.Text.Json.Serialization;

namespace DockGlassLite.Models;

public sealed class AppConfig
{
    public bool StartWithWindows { get; set; }
    public DockConfig Dock { get; set; } = new();
    public List<DockItem> DockItems { get; set; } = [];
}

public sealed class DockConfig
{
    public double IconSize { get; set; } = 48;
    public string Position { get; set; } = "BottomCenter";
}

public sealed class DockItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DockItemKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string ShortcutPath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string IconLocation { get; set; } = "";
    public string IconCachePath { get; set; } = "";

    [JsonIgnore]
    public string EffectivePath => !string.IsNullOrWhiteSpace(SourcePath) ? SourcePath : ShortcutPath;

    [JsonIgnore]
    public string LaunchPath
    {
        get
        {
            if (Kind == DockItemKind.Folder)
            {
                return EffectivePath;
            }

            return File.Exists(EffectivePath) ? EffectivePath : TargetPath;
        }
    }

    [JsonIgnore]
    public bool IsMissing => Kind switch
    {
        DockItemKind.Folder => string.IsNullOrWhiteSpace(EffectivePath) || !Directory.Exists(EffectivePath),
        _ => !File.Exists(EffectivePath) && !File.Exists(TargetPath)
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DockItemKind
{
    Shortcut,
    Folder
}
