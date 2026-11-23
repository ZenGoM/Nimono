namespace Nimono;

/// <summary>
/// EXIF の向き（Orientation）に従って画像を回す。
///
/// 撮影時に本体を傾けた写真は、ピクセルはそのままで「どう回して見せるか」だけがタグに入っている。
/// サムネイルは <c>GetThumbnailImage</c> で作り直すとこのタグが落ちるため、ここを通してピクセルごと回しておく。
/// </summary>
internal static class ImageOrientation
{
    /// <summary>EXIF の Orientation タグ。</summary>
    public const int TagId = 0x0112;

    /// <summary>向きの値。読めない場合は 1（正立）とみなす。</summary>
    public static int Read(Image image)
    {
        try
        {
            if (Array.IndexOf(image.PropertyIdList, TagId) < 0) return 1;

            var value = image.GetPropertyItem(TagId)?.Value;
            if (value is not { Length: >= 2 }) return 1;

            int orientation = BitConverter.ToUInt16(value, 0);
            return orientation is >= 1 and <= 8 ? orientation : 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>90 度単位で回る向きかどうか（縦横が入れ替わる）。</summary>
    public static bool SwapsSide(int orientation) => orientation is 5 or 6 or 7 or 8;

    /// <summary>向きに合わせて回す。正立（1）または未知の値なら何もしない。</summary>
    public static void Apply(Image image, int orientation)
    {
        var flip = ToFlip(orientation);
        if (flip != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(flip);
    }

    private static RotateFlipType ToFlip(int orientation) => orientation switch
    {
        2 => RotateFlipType.RotateNoneFlipX,
        3 => RotateFlipType.Rotate180FlipNone,
        4 => RotateFlipType.Rotate180FlipX,
        5 => RotateFlipType.Rotate90FlipX,
        6 => RotateFlipType.Rotate90FlipNone,
        7 => RotateFlipType.Rotate270FlipX,
        8 => RotateFlipType.Rotate270FlipNone,
        _ => RotateFlipType.RotateNoneFlipNone,
    };
}
