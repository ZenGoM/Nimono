using System.Text.Json;

namespace Nimono;

internal sealed class AppSettings
{
    public List<string> SearchFolders { get; set; } = new();
    public int SimilarityThreshold { get; set; } = 85;
    public int WindowWidth { get; set; } = 980;
    public int WindowHeight { get; set; } = 700;
    public bool MainWindowMaximized { get; set; } = false;
    public int CompareFormWidth { get; set; } = 950;
    public int CompareFormHeight { get; set; } = 700;
    public bool CompareWindowMaximized { get; set; } = false;
    public int CompareSplitterDistance { get; set; } = 0;
    // パスごとのサムネイル回転回数（1=90°, 2=180°, 3=270°）。0は保存しない
    public Dictionary<string, int> ThumbnailRotations { get; set; } = new();
    public string SimilarityMethod { get; set; } = "pHash"; // "pHash", "DINOv2", "DINOv2_DML"（旧 "DINOv2_CUDA" も DML として扱う）
    public string DINOv2ModelPath { get; set; } = "";
    public List<int> CompareListColumnWidths { get; set; } = new();
    // キャッシュ DB を置くフォルダー。空なら既定の場所（%LOCALAPPDATA%\Nimono）
    public string CacheFolder { get; set; } = "";
    // ゴミ箱への移動を拒否するクラウド同期プロバイダー名。削除時に最初から完全削除の確認を出す。
    // 移動が 0x17B で失敗したプロバイダーはここに追加される
    public List<string> NoRecycleProviders { get; set; } = new() { "iCloudPhotos" };
    // 基準画像の優先順位（上から順に比べる）。どれも無効なら見つかった順
    public List<ReferencePriorityRule> ReferencePriorities { get; set; } = ReferencePriority.DefaultRules();
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
