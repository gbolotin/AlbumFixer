using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WpfFoundation.Theming;

namespace AlbumFixer.App;

/// <summary>
/// Remembers the theme choice between runs in <c>%LOCALAPPDATA%\Album Fixer\settings.json</c>. A missing or
/// unreadable file means System, and a failed save is logged without interrupting the user.
/// </summary>
public sealed class ThemePreferenceStore(ILogger<ThemePreferenceStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Album Fixer",
        "settings.json");

    public ThemePreference Load() => Load(FilePath);

    public ThemePreference Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return ThemePreference.System;
            }

            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions);
            return settings is not null && Enum.IsDefined(settings.Theme) ? settings.Theme : ThemePreference.System;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(error, "Could not read the theme choice from {SettingsPath}; using System", path);
            return ThemePreference.System;
        }
    }

    public void Save(ThemePreference theme) => Save(FilePath, theme);

    public void Save(string path, ThemePreference theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(theme), JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Could not save the theme choice to {SettingsPath}", path);
        }
    }

    private sealed record Settings(ThemePreference Theme);
}
