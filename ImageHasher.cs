using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Nimono;

internal static class ImageHasher
{
    private const int Size = 8;

    public static ulong Compute(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var src = Image.FromStream(fs, false, false);
        return Compute(src);
    }

    public static ulong Compute(Image source)
    {
        using var bmp = Resize(source, Size, Size);
        return BuildHash(bmp);
    }

    public static double Similarity(ulong h1, ulong h2)
    {
        int distance = BitOperations.PopCount(h1 ^ h2);
        return 1.0 - distance / (double)(Size * Size);
    }

    public static int HammingDistance(ulong h1, ulong h2) =>
        BitOperations.PopCount(h1 ^ h2);

    private static ulong BuildHash(Bitmap bmp)
    {
        int total = Size * Size;
        var data = bmp.LockBits(
            new Rectangle(0, 0, Size, Size),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        var raw = new byte[data.Stride * Size];
        Marshal.Copy(data.Scan0, raw, 0, raw.Length);
        bmp.UnlockBits(data);

        int[] gray = new int[total];
        int sum = 0;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int o = y * data.Stride + x * 3;
            // Format24bppRgb: B G R
            int gv = (raw[o + 2] * 299 + raw[o + 1] * 587 + raw[o] * 114) / 1000;
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
