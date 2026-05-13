using System.Numerics;

namespace Nimono;

internal static class ImageHasher
{
    private const int Size = 8;

    public static ulong Compute(string filePath)
    {
        using var src = Image.FromFile(filePath);
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
        int[] gray = new int[total];
        int sum = 0;

        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            var c = bmp.GetPixel(x, y);
            int g = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
            gray[y * Size + x] = g;
            sum += g;
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
