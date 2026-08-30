using System.Text.Json;
using DockGlassLite.Models;

namespace DockGlassLite.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppConfig Current { get; private set; } = new();

    public async Task<AppConfig> LoadAsync()
    {
        AppPaths.EnsureDirectories();

        if (!File.Exists(AppPaths.ConfigFilePath))
        {
            Current = new AppConfig();
            await SaveAsync();
            return Current;
        }

        try
        {
            var json = await File.ReadAllTextAsync(AppPaths.ConfigFilePath);
            Current = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            File.AppendAllText(AppPaths.LogFilePath, $"{DateTimeOffset.Now:u} Failed to load config: {ex}\n");
            Current = new AppConfig();
        }

        return Current;
    }

    public async Task SaveAsync()
    {
        AppPaths.EnsureDirectories();
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        await File.WriteAllTextAsync(AppPaths.ConfigFilePath, json);
    }
}
