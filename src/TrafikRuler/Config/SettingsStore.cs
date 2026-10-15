using System.IO;
using System.Text.Json;
using TrafikRuler.Core;

namespace TrafikRuler.Config;

public sealed class AppSettings
{
    public List<Rule> Rules { get; set; } = new();

    /// <summary>Не перенаправлять трафик в локальные подсети исходного адаптера (принтеры, NAS, роутер).</summary>
    public bool ExcludeLocalNetworks { get; set; } = true;

    /// <summary>При включении правила разрывать уже открытые соединения программы.</summary>
    public bool ResetExistingConnections { get; set; } = true;

    /// <summary>Глобальная пауза всех правил.</summary>
    public bool Paused { get; set; }
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrafikRuler");

    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Logger.Write("Не удалось прочитать настройки, используются значения по умолчанию: " + ex.Message);
            TryBackupBroken();
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

    private static void TryBackupBroken()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".broken", overwrite: true);
        }
        catch (IOException) { }
    }
}
