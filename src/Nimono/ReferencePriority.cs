using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Nimono;

/// <summary>基準画像を選ぶときに比べる項目。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ReferencePriorityKey
{
    /// <summary>「フォルダー選択」の並び順。</summary>
    FolderOrder,
    /// <summary>解像度（幅 × 高さの画素数）。</summary>
    Resolution,
    /// <summary>画面に出す日付（EXIF の日時、なければ更新日時）。</summary>
    ImageDate,
    /// <summary>ファイルの更新日時。</summary>
    ModifiedDate,
    /// <summary>ファイルサイズ。</summary>
    FileSize,
    /// <summary>ファイル名（エクスプローラーの「名前」と同じ並び）。</summary>
    FileName,
}

/// <summary>
/// 基準画像の優先順位の 1 項目（設定に保存する）。
/// PreferHigher が true なら値の大きいほう（高い・新しい・大きい・下のフォルダー・名前の降順で先）を優先する。
/// </summary>
internal sealed class ReferencePriorityRule
{
    public ReferencePriorityKey Key { get; set; }
    public bool Enabled { get; set; }
    public bool PreferHigher { get; set; }

    public ReferencePriorityRule() { }

    public ReferencePriorityRule(ReferencePriorityKey key, bool enabled, bool preferHigher)
    {
        Key = key;
        Enabled = enabled;
        PreferHigher = preferHigher;
    }

    public ReferencePriorityRule Clone() => new(Key, Enabled, PreferHigher);

    /// <summary>画面に出す説明（例: 「解像度 — 高いほうを優先」）。</summary>
    public string Describe() => $"{KeyName(Key)} — {DirectionName(Key, PreferHigher)}";

    public static string KeyName(ReferencePriorityKey key) => key switch
    {
        ReferencePriorityKey.FolderOrder => "フォルダーの順番",
        ReferencePriorityKey.Resolution => "解像度",
        ReferencePriorityKey.ImageDate => "日付（EXIF、なければ更新日時）",
        ReferencePriorityKey.ModifiedDate => "更新日時",
        ReferencePriorityKey.FileSize => "ファイルサイズ",
        ReferencePriorityKey.FileName => "ファイル名",
        _ => key.ToString(),
    };

    public static string DirectionName(ReferencePriorityKey key, bool preferHigher) => key switch
    {
        ReferencePriorityKey.FolderOrder => preferHigher ? "下のフォルダーを優先" : "上のフォルダーを優先",
        ReferencePriorityKey.Resolution => preferHigher ? "高いほうを優先" : "低いほうを優先",
        ReferencePriorityKey.ImageDate or ReferencePriorityKey.ModifiedDate =>
            preferHigher ? "新しいほうを優先" : "古いほうを優先",
        ReferencePriorityKey.FileSize => preferHigher ? "大きいほうを優先" : "小さいほうを優先",
        ReferencePriorityKey.FileName => preferHigher ? "降順（Z→A）で先を優先" : "昇順（A→Z）で先を優先",
        _ => preferHigher ? "大きいほうを優先" : "小さいほうを優先",
    };
}

/// <summary>
/// 基準画像を選ぶために比べる、画像 1 枚分の値。読めなかった値は null（その項目では必ず後回しにする）。
/// </summary>
/// <param name="FoundIndex">スキャンで見つかった順番。すべての項目が同じときに使う。</param>
/// <param name="FolderIndex">「フォルダー選択」で何番目のフォルダーの中にあるか。</param>
/// <param name="FileName">ファイル名（フォルダーを除く）。</param>
internal readonly record struct ImageFacts(
    int FoundIndex,
    int? FolderIndex,
    long? Pixels,
    DateTime? ImageDate,
    DateTime? ModifiedDate,
    long? FileSize,
    string? FileName = null);

/// <summary>
/// 利用者が決めた優先順位で、グループの基準画像を選ぶ。
///
/// 有効な項目を上から順に比べ、最初に差がついた項目で決める。
/// どの項目でも差がつかなければ（項目が 1 つも有効でない場合も）、スキャンで見つかった順にする。
/// </summary>
internal static class ReferencePriority
{
    /// <summary>初期設定。どれも無効なので、見つかった順になる。</summary>
    public static List<ReferencePriorityRule> DefaultRules() =>
    [
        new(ReferencePriorityKey.FolderOrder, false, false),
        new(ReferencePriorityKey.Resolution, false, true),
        new(ReferencePriorityKey.FileSize, false, true),
        new(ReferencePriorityKey.ImageDate, false, false),
        new(ReferencePriorityKey.ModifiedDate, false, false),
        new(ReferencePriorityKey.FileName, false, false),
    ];

    /// <summary>
    /// 設定から読んだ並びを整える。重複した項目は先のものだけ残し、足りない項目は無効のまま末尾に足す
    /// （古い設定ファイルや、あとから項目を増やした場合のため）。
    /// </summary>
    public static List<ReferencePriorityRule> Normalize(IEnumerable<ReferencePriorityRule>? rules)
    {
        var result = new List<ReferencePriorityRule>();
        foreach (var rule in rules ?? [])
        {
            if (!Enum.IsDefined(rule.Key) || result.Any(r => r.Key == rule.Key)) continue;
            result.Add(rule.Clone());
        }
        foreach (var rule in DefaultRules())
            if (result.All(r => r.Key != rule.Key)) result.Add(rule);
        return result;
    }

    /// <summary>解像度や画像の日付（ファイルを開かないと分からない値）を使う項目が有効か。</summary>
    public static bool NeedsImageHeader(IEnumerable<ReferencePriorityRule> rules) =>
        rules.Any(r => r.Enabled && r.Key is ReferencePriorityKey.Resolution or ReferencePriorityKey.ImageDate);

    /// <summary>
    /// <paramref name="path"/> が <paramref name="folders"/> の何番目のフォルダーの中にあるか。
    /// 入れ子のフォルダーを両方選んでいる場合は、並びで先のフォルダーとする（スキャンで先に見つかる側）。どれにも無ければ null。
    /// </summary>
    public static int? FolderIndexOf(string path, IReadOnlyList<string> folders)
    {
        for (int i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];
            var prefix = folder.EndsWith('\\') ? folder : folder + '\\';
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return null;
    }

    /// <summary>
    /// どちらを基準として優先するか。負なら <paramref name="a"/>、正なら <paramref name="b"/> を優先する。
    /// </summary>
    public static int Compare(in ImageFacts a, in ImageFacts b, IReadOnlyList<ReferencePriorityRule> rules)
    {
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            int c = rule.Key switch
            {
                ReferencePriorityKey.FolderOrder => CompareValues(a.FolderIndex, b.FolderIndex, rule.PreferHigher),
                ReferencePriorityKey.Resolution => CompareValues(a.Pixels, b.Pixels, rule.PreferHigher),
                ReferencePriorityKey.ImageDate => CompareValues(ToSeconds(a.ImageDate), ToSeconds(b.ImageDate), rule.PreferHigher),
                ReferencePriorityKey.ModifiedDate => CompareValues(ToSeconds(a.ModifiedDate), ToSeconds(b.ModifiedDate), rule.PreferHigher),
                ReferencePriorityKey.FileSize => CompareValues(a.FileSize, b.FileSize, rule.PreferHigher),
                ReferencePriorityKey.FileName => CompareNames(a.FileName, b.FileName, rule.PreferHigher),
                _ => 0,
            };
            if (c != 0) return c;
        }
        return a.FoundIndex.CompareTo(b.FoundIndex);
    }

    /// <summary>優先順位で最も優先される画像を返す。</summary>
    public static string Pick(
        IReadOnlyList<string> paths, IReadOnlyList<ReferencePriorityRule> rules, Func<string, ImageFacts> factsOf)
    {
        string best = paths[0];
        var bestFacts = factsOf(best);
        for (int i = 1; i < paths.Count; i++)
        {
            var facts = factsOf(paths[i]);
            if (Compare(facts, bestFacts, rules) < 0)
                (best, bestFacts) = (paths[i], facts);
        }
        return best;
    }

    /// <summary>
    /// 優先順位で基準を選び直したグループを返す（類似度も新しい基準で計算し直す）。
    /// <paramref name="factsOf"/> はファイルを読むことがあるため、1 枚につき 1 回だけ呼ぶ。
    /// </summary>
    public static ImageGroup Apply(
        ImageGroup group, IReadOnlyList<ReferencePriorityRule> rules, Func<string, ImageFacts> factsOf)
    {
        var facts = group.Paths.Distinct().ToDictionary(p => p, factsOf);
        var reference = Pick(group.Paths, rules, p => facts[p]);
        return ImageGrouper.WithReference(group, reference, p => facts[p].FoundIndex);
    }

    // 日時は秒単位で比べる（ファイルシステムによって秒未満の精度が違い、コピーしただけで差がつくのを避ける）
    private static long? ToSeconds(DateTime? value) => value is { } v ? v.Ticks / TimeSpan.TicksPerSecond : null;

    /// <summary>
    /// ファイル名をエクスプローラーの「名前」と同じ順で比べる（数字は数として比べ、大文字小文字は区別しない）。
    /// まず拡張子を除いた名前で比べるため、「IMG.jpg」は「IMG(1).jpg」より前になる。同じなら拡張子を含めて比べる。
    /// </summary>
    public static int CompareFileNames(string a, string b)
    {
        int c = StrCmpLogicalW(Path.GetFileNameWithoutExtension(a), Path.GetFileNameWithoutExtension(b));
        if (c != 0) return Math.Sign(c);
        c = StrCmpLogicalW(a, b);
        return c != 0 ? Math.Sign(c) : string.CompareOrdinal(a, b);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string a, string b);

    // 名前の無いほうは、向きによらず後回しにする
    private static int CompareNames(string? a, string? b, bool preferHigher)
    {
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        int c = CompareFileNames(a, b);
        return preferHigher ? -c : c;
    }

    // 値の無いほうは、向きによらず後回しにする
    private static int CompareValues<T>(T? a, T? b, bool preferHigher) where T : struct, IComparable<T>
    {
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        int c = a.Value.CompareTo(b.Value);
        return preferHigher ? -c : c;
    }
}
