using System.Text.Json;

namespace Nimono;

internal sealed class AppSettings
{
    public List<string> SearchFolders { get; set; } = new();
    public int SimilarityThreshold { get; set; } = 85;
    public int WindowWidth { get; set; } = 1100;
    public int WindowHeight { get; set; } = 750;
    public int CompareFormWidth { get; set; } = 900;
    public int CompareFormHeight { get; set; } = 700;
    public int CompareSplitterDistance { get; set; } = 0;
    // パスごとのサムネイル回転回数（1=90°, 2=180°, 3=270°）。0は保存しない
    public Dictionary<string, int> ThumbnailRotations { get; set; } = new();
}

internal static class SettingsStorage
{
    private static readonly string SettingsFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nimono");

    private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };
            var settings = JsonSerializer.Deserialize<AppSettings>(json, options);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
            };
            var json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // 保存に失敗しても UI には影響を与えない
        }
    }
}
