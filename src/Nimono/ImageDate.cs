using System.Globalization;

namespace Nimono;

/// <summary>
/// 画面に出す画像の日付。EXIF の日時があればそれを、なければファイルの更新日時を使う。
/// コピーやダウンロードで更新日時が変わっても、撮影した日で見比べられるようにする。
/// </summary>
internal static class ImageDate
{
    /// <summary>日付として読む EXIF タグ。上から順に採用する。</summary>
    private static readonly int[] ExifDateTagIds =
    {
        0x9003, // DateTimeOriginal（撮影日時）
        0x9004, // DateTimeDigitized（デジタル化日時）
        0x0132, // DateTime（ファイル変更日時）
    };

    /// <summary>ファイルを開いて日付を読む。EXIF が読めなければファイルの更新日時。</summary>
    public static DateTime Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // validateImageData: false でピクセルを展開せずヘッダーだけ読む
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return Read(image, path);
        }
        catch
        {
            // GDI+ が扱えない形式（WebP など）・読み取り失敗は更新日時へ
            return File.GetLastWriteTime(path);
        }
    }

    /// <summary>開いてある画像から日付を読む。EXIF が無ければファイルの更新日時。</summary>
    public static DateTime Read(Image image, string path) =>
        ReadExif(image) ?? File.GetLastWriteTime(path);

    private static DateTime? ReadExif(Image image)
    {
        try
        {
            var ids = image.PropertyIdList;
            foreach (int tagId in ExifDateTagIds)
            {
                if (Array.IndexOf(ids, tagId) < 0) continue;

                var value = image.GetPropertyItem(tagId)?.Value;
                if (value == null) continue;

                // EXIF の日時は "yyyy:MM:dd HH:mm:ss" + NUL 終端の ASCII
                var text = System.Text.Encoding.ASCII.GetString(value).TrimEnd('\0', ' ');
                if (DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken))
                    return taken;
            }
        }
        catch
        {
        }
        return null;
    }
}
