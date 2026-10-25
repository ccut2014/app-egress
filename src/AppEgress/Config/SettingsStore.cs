using System.IO;
using System.Text.Json;
using AppEgress.Core;
using AppEgress.Localization;

namespace AppEgress.Config;

public sealed class AppSettings
{
    public List<Rule> Rules { get; set; } = new();

    /// <summary>Не перенаправлять трафик в локальные подсети исходного адаптера (принтеры, NAS, роутер).</summary>
    public bool ExcludeLocalNetworks { get; set; } = true;

    /// <summary>При включении правила разрывать уже открытые соединения программы.</summary>
    public bool ResetExistingConnections { get; set; } = true;

    /// <summary>Глобальная пауза всех правил.</summary>
    public bool Paused { get; set; }

    /// <summary>Язык интерфейса: "auto" (по языку Windows) или код из Loc.Languages.</summary>
    public string Language { get; set; } = Loc.Auto;
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppIdentity.Name);

    public static string LegacyDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppIdentity.LegacyName);

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        // Prefer the new file even if it is broken; never silently reactivate an older routing configuration.
        string path = File.Exists(FilePath) ? FilePath : Path.Combine(LegacyDirectory, "settings.json");
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
                if (!string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
                    Logger.Write(Loc.T("LogLegacySettingsLoaded"));
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logger.Write(Loc.T("LogSettingsReadFailed", ex.Message));
            TryBackupBroken(path);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static void TryBackupBroken(string path)
    {
        try
        {
            File.Copy(path, path + ".broken", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
