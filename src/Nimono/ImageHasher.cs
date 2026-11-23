using System.Numerics;

namespace Nimono;

internal static class ImageHasher
{
    private const int Size = 8;

    public static ulong Compute(string filePath)
    {
        using var src = Image.FromFile(filePath);
        // GetThumbnailImage reads the embedded Exif thumbnail for JPEG (fast path);
        // falls back to full-image resize for PNG/BMP/etc.
        using var thumb = src.GetThumbnailImage(Size, Size, () => false, IntPtr.Zero);
        using var bmp = new Bitmap(thumb);
        // EXIF の向きに合わせて回す。8×8 に縮めてから回しても、回してから縮めたのと同じになる
        // （埋め込みサムネイルも本体と同じく回す前の向きで入っている）
        ImageOrientation.Apply(bmp, ImageOrientation.Read(src));
        return BuildHash(bmp);
    }

    public static ulong Compute(Image source)
    {
        using var bmp = Resize(source, Size, Size);
        ImageOrientation.Apply(bmp, ImageOrientation.Read(source));
        return BuildHash(bmp);
    }

    public static double Similarity(int distance) => 1.0 - distance / (double)(Size * Size);

    /// <summary>
    /// 8×8 の格子を右に 90° 回したハッシュ（<see cref="RotateFlipType.Rotate90FlipNone"/> と同じ向き）。
    /// 縮めてから回しても回してから縮めても同じになるため、画像を読み直さずに回した画像のハッシュが得られる。
    /// </summary>
    public static ulong Rotate90(ulong hash)
    {
        ulong result = 0;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            // (x, y) は右に 90° 回すと (Size - 1 - y, x) に移る
            if (((hash >> (y * Size + x)) & 1) != 0)
                result |= 1UL << (x * Size + (Size - 1 - y));
        }
        return result;
    }

    /// <summary>0°, 90°, 180°, 270°（右回り）に回したハッシュ。</summary>
    public static ulong[] Rotations(ulong hash)
    {
        var rotations = new ulong[4];
        rotations[0] = hash;
        for (int r = 1; r < rotations.Length; r++)
            rotations[r] = Rotate90(rotations[r - 1]);
        return rotations;
    }

    /// <summary>
    /// 向きが違うだけの画像を同じとみなして比べる。<paramref name="h2"/> を 4 つの向きに回し、いちばん近いものを返す。
    /// Rotation は <paramref name="h2"/> の画像を右に何回 90° 回すと <paramref name="h1"/> の向きにそろうか（同じ距離なら少ない回数）。
    /// </summary>
    public static (int Distance, int Rotation) BestMatch(ulong h1, ulong h2)
    {
        int bestDistance = int.MaxValue, bestRotation = 0;
        ulong rotated = h2;
        for (int r = 0; r < 4; r++)
        {
            int distance = BitOperations.PopCount(h1 ^ rotated);
            if (distance < bestDistance) (bestDistance, bestRotation) = (distance, r);
            rotated = Rotate90(rotated);
        }
        return (bestDistance, bestRotation);
    }

    private static ulong BuildHash(Bitmap bmp)
    {
        int total = Size * Size;
        int[] gray = new int[total];
        int sum = 0;

        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            var c = bmp.GetPixel(x, y);
            int gv = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            gray[y * Size + x] = gv;
            sum += gv;
        }

        int avg = sum / total;
        ulong hash = 0;
        for (int i = 0; i < total; i++)
            if (gray[i] >= avg)
                hash |= 1UL << i;

        return hash;
    }

    private static Bitmap Resize(Image src, int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, w, h);
        return bmp;
    }
}
