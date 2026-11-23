using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Nimono;

/// <summary>
/// 画像からサムネイルを作る。
///
/// JPEG に EXIF の埋め込みサムネイルがあれば <c>GetThumbnailImage</c> がそれを使うため速い。
/// ただし本体と縦横比が違う埋め込みを持つ機種があり、そのまま任せると絵が歪むため
/// （→ <see cref="CanUseEmbedded"/>）、使えるかどうかを確かめてから任せる。
/// </summary>
internal static class Thumbnail
{
    /// <summary>EXIF の埋め込みサムネイル本体（JPEG のバイト列）。</summary>
    private const int ThumbnailDataTag = 0x501B;

    /// <summary>縦横比の食い違いをどこまで同じとみなすか。</summary>
    private const double AspectTolerance = 0.02;

    /// <summary>
    /// 縦幅を <paramref name="height"/> に合わせ、横幅は縦横比なりにしたサムネイル。
    /// EXIF の向きに合わせて回した状態で返す。
    /// </summary>
    public static Bitmap Create(Image source, int height)
    {
        int orientation = ImageOrientation.Read(source);

        // 90 度回す写真は、回したあとの縦幅が height になるよう縦横を入れ替えて考える
        var (width, scaledHeight) = ImageOrientation.SwapsSide(orientation)
            ? (height, ScaleSide(source.Height, source.Width, height))
            : (ScaleSide(source.Width, source.Height, height), height);

        var thumbnail = CanUseEmbedded(source, width, scaledHeight)
            ? ToBitmap(source.GetThumbnailImage(width, scaledHeight, null, IntPtr.Zero))
            : Scale(source, width, scaledHeight);

        ImageOrientation.Apply(thumbnail, orientation);
        return thumbnail;
    }

    /// <summary>一辺を <paramref name="target"/> にしたときの、もう一辺の長さ。</summary>
    private static int ScaleSide(int side, int fittedSide, int target) =>
        fittedSide <= 0 ? target : Math.Max(1, (int)Math.Round((double)side * target / fittedSide));

    /// <summary>
    /// 埋め込みサムネイルに任せてよいか。
    ///
    /// 4:3 の枠に縦位置の写真を黒帯付きで収めた埋め込みを持つ機種（携帯電話など）があり、
    /// <c>GetThumbnailImage</c> はそれを要求サイズまで引き伸ばすため、黒帯ごと歪んだ絵になる。
    /// 要求より小さい埋め込みも、引き伸ばすと粗くなるため本体から作り直す。
    /// </summary>
    private static bool CanUseEmbedded(Image source, int width, int height)
    {
        try
        {
            if (Array.IndexOf(source.PropertyIdList, ThumbnailDataTag) < 0) return false;
            if (source.GetPropertyItem(ThumbnailDataTag)?.Value is not byte[] jpeg) return false;
            if (ReadJpegSize(jpeg) is not (int embeddedWidth, int embeddedHeight)) return false;
            if (embeddedWidth <= 0 || embeddedHeight <= 0 || source.Height == 0) return false;

            double sourceAspect = (double)source.Width / source.Height;
            double embeddedAspect = (double)embeddedWidth / embeddedHeight;
            if (Math.Abs(sourceAspect - embeddedAspect) / sourceAspect > AspectTolerance) return false;

            return embeddedWidth >= width && embeddedHeight >= height;
        }
        catch
        {
            // 読めない埋め込みは信用しない（本体から作れば必ず正しい絵になる）
            return false;
        }
    }

    /// <summary>GetThumbnailImage の戻り値を Bitmap にそろえる。</summary>
    private static Bitmap ToBitmap(Image image)
    {
        if (image is Bitmap bitmap) return bitmap;
        using (image) return new Bitmap(image);
    }

    /// <summary>本体を縮小した複製。</summary>
    private static Bitmap Scale(Image source, int width, int height)
    {
        var bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height));
        using var graphics = Graphics.FromImage(bitmap);

        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // 既定の折り返しのままだと、縮小時に外周へ半透明の縁が出ることがある
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);

        graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                           0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return bitmap;
    }

    /// <summary>JPEG のヘッダーから縦横を読む。展開しないので速い。読めない場合は null。</summary>
    private static (int Width, int Height)? ReadJpegSize(byte[] jpeg)
    {
        // [0xFF][マーカー][長さ 2 バイト][中身…] の並び。SOF に縦横が入っている
        int position = 2;
        while (position + 9 < jpeg.Length)
        {
            if (jpeg[position] != 0xFF) { position++; continue; }

            byte marker = jpeg[position + 1];
            if (marker == 0xFF) { position++; continue; }                 // 詰め物
            if (marker == 0xD9) return null;                              // 画像の終わり（SOF が無かった）
            // 長さを持たないマーカー（SOI・TEM・RSTn）は読み飛ばす
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { position += 2; continue; }

            int length = (jpeg[position + 2] << 8) | jpeg[position + 3];
            if (length < 2) return null;

            // SOF0〜SOF15。ただし DHT(C4)・DNL(C8)・DAC(CC) は別物
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                return ((jpeg[position + 7] << 8) | jpeg[position + 8],
                        (jpeg[position + 5] << 8) | jpeg[position + 6]);

            position += 2 + length;
        }
        return null;
    }
}
