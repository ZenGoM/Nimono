using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace Nimono.Tests;

/// <summary>
/// 向きが違うだけの画像を同じとみなして比べられること、そろえるための回転の向きが表示（RotateFlip）と合っていることを確かめる。
/// </summary>
[TestClass]
public class RotationMatchTests
{
    [TestMethod]
    public void HashRotate90_MatchesBitmapRotateFlip()
    {
        using var bmp = RandomBlackWhite(8, 8, new Random(1));
        ulong hash = HashOf(bmp);

        bmp.RotateFlip(RotateFlipType.Rotate90FlipNone);

        Assert.AreEqual(HashOf(bmp), ImageHasher.Rotate90(hash));
    }

    [TestMethod]
    public void HashRotate90_FourTimesIsIdentity()
    {
        ulong hash = 0x0123_4567_89AB_CDEFUL;
        var rotations = ImageHasher.Rotations(hash);
        Assert.AreEqual(hash, ImageHasher.Rotate90(rotations[3]));
    }

    [TestMethod]
    public void HashBestMatch_ReturnsRotationThatAlignsToReference()
    {
        ulong reference = 0x0123_4567_89AB_CDEFUL;
        var rotations = ImageHasher.Rotations(reference);
        for (int k = 0; k < 4; k++)
        {
            // 基準を右に k 回回した画像は、右に (4 - k) % 4 回回すと基準の向きに戻る
            var (distance, rotation) = ImageHasher.BestMatch(reference, rotations[k]);
            Assert.AreEqual(0, distance);
            Assert.AreEqual((4 - k) % 4, rotation);
        }
    }

    [TestMethod]
    public void EmbeddingBestMatch_ReturnsRotationThatAlignsToReference()
    {
        // 4 つの向きのベクトル（互いに直交する単位ベクトル）
        const int dim = 8;
        var vectors = Enumerable.Range(0, 4).Select(r =>
        {
            var v = new float[dim];
            v[r] = 1f;
            return v;
        }).ToArray();
        var reference = DINOv2Embedder.Concat(vectors, 0);

        for (int k = 0; k < 4; k++)
        {
            // 基準を右に k 回回した画像の「右に s 回回した」ベクトルは、基準の (s + k) 回分
            var rotated = Enumerable.Range(0, 4).Select(s => vectors[(s + k) % 4]).ToArray();
            var (similarity, rotation) = DINOv2Embedder.BestMatch(reference, DINOv2Embedder.Concat(rotated, 0));
            Assert.AreEqual(1f, similarity, 1e-6f);
            Assert.AreEqual((4 - k) % 4, rotation);
            Assert.IsTrue(DINOv2Embedder.IsSimilar(reference, DINOv2Embedder.Concat(rotated, 0), 0.99f));
        }
    }

    [TestMethod]
    public void MatchToReference_TreatsRotatedHashAsSameImage()
    {
        ulong reference = 0x0123_4567_89AB_CDEFUL;
        var hashes = new Dictionary<string, ulong>
        {
            ["a.jpg"] = reference,
            ["b.jpg"] = ImageHasher.Rotate90(reference),
        };

        var matches = ImageGrouper.MatchToReference("a.jpg", hashes.Keys, hashes, null)!;

        Assert.AreEqual(new ReferenceMatch(1.0, 0), matches["a.jpg"]);
        Assert.AreEqual(new ReferenceMatch(1.0, 3), matches["b.jpg"]);
    }

    [TestMethod]
    public void Group_PutsRotatedImagesTogether()
    {
        string prefix = $@"X:\rot-{Guid.NewGuid():N}\";
        ulong hash = 0x0123_4567_89AB_CDEFUL;
        var entries = new List<ImageEntry>
        {
            new($"{prefix}a.jpg", hash),
            new($"{prefix}b.jpg", ImageHasher.Rotate90(ImageHasher.Rotate90(hash))),
        };
        foreach (var e in entries)
            CacheManager.MemoryCache[e.Path] = new CacheEntry(100, 1, e.Hash, null);

        var groups = ImageGrouper.Group(entries, 0.9, null, null, CancellationToken.None);

        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(1.0, groups[0].Similarities[$"{prefix}b.jpg"]);
    }

    [TestMethod]
    public void HashCompute_AppliesExifOrientation()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nimono-rot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // 縦長の画像を、「EXIF で右に 90° 回して見せる」ファイルと「画素ごと右に 90° 回した」ファイルにする
            using var bmp = RandomBlackWhite(48, 64, new Random(2), block: 8);
            string tagged = Path.Combine(dir, "tagged.jpg");
            string rotated = Path.Combine(dir, "rotated.jpg");

            bmp.SetPropertyItem(OrientationItem(6));
            bmp.Save(tagged, ImageFormat.Jpeg);
            bmp.RemovePropertyItem(ImageOrientation.TagId);
            bmp.RotateFlip(RotateFlipType.Rotate90FlipNone);
            bmp.Save(rotated, ImageFormat.Jpeg);

            var (distance, rotation) = ImageHasher.BestMatch(ImageHasher.Compute(rotated), ImageHasher.Compute(tagged));
            Assert.IsTrue(distance <= 2, $"distance={distance}");
            Assert.AreEqual(0, rotation, "EXIF の向きを反映していれば、回さずに一致する");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>ImageHasher と同じ並び（bit = y * 8 + x、白なら 1）で、白黒の 8×8 画像からハッシュを作る。</summary>
    private static ulong HashOf(Bitmap bmp)
    {
        ulong hash = 0;
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            if (bmp.GetPixel(x, y).R > 127)
                hash |= 1UL << (y * 8 + x);
        return hash;
    }

    private static Bitmap RandomBlackWhite(int width, int height, Random random, int block = 1)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        for (int by = 0; by < height; by += block)
        for (int bx = 0; bx < width; bx += block)
        {
            var color = random.Next(2) == 0 ? Color.Black : Color.White;
            for (int y = by; y < by + block && y < height; y++)
            for (int x = bx; x < bx + block && x < width; x++)
                bmp.SetPixel(x, y, color);
        }
        return bmp;
    }

    private static PropertyItem OrientationItem(ushort orientation)
    {
        // PropertyItem は公開コンストラクターが無いため、空のインスタンスを作って埋める
        var item = (PropertyItem)RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
        item.Id = ImageOrientation.TagId;
        item.Type = 3; // SHORT
        item.Len = 2;
        item.Value = BitConverter.GetBytes(orientation);
        return item;
    }
}
