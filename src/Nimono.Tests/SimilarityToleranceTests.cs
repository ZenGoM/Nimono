using Microsoft.Data.Sqlite;
using Nimono;

namespace Nimono.Tests;

/// <summary>
/// DINOv2 の類似判定が丸め誤差で完全に同じ画像を見逃さないこと、
/// 判定方法を変える前のグループキャッシュを使わないことを確かめる。
/// </summary>
[TestClass]
[DoNotParallelize] // CacheManager の場所を共有するため
public class SimilarityToleranceTests
{
    private const int Dim = 384;

    /// <summary>正規化したベクトルを 4 つの向きの分だけ並べた、埋め込みと同じ形の配列（向きはどれも同じ）。</summary>
    private static float[] Embedding(Random random)
    {
        var v = new float[Dim];
        double norm = 0;
        for (int i = 0; i < Dim; i++) { v[i] = (float)(random.NextDouble() * 2 - 1); norm += v[i] * v[i]; }
        float inv = (float)(1 / Math.Sqrt(norm));
        for (int i = 0; i < Dim; i++) v[i] *= inv;
        var e = new float[Dim * DINOv2Embedder.Rotations];
        for (int r = 0; r < DINOv2Embedder.Rotations; r++) v.CopyTo(e, r * Dim);
        return e;
    }

    [TestMethod]
    public void IdenticalEmbedding_IsSimilarAt100Percent_EvenWhenDotFallsBelowOne()
    {
        // 実データと同じく、自分自身との内積が丸め誤差で 1.0 を下回るベクトルを探す
        var random = new Random(1);
        float[]? found = null;
        for (int k = 0; k < 1000 && found is null; k++)
        {
            var e = Embedding(random);
            if (DINOv2Embedder.BestMatch(e, e).Similarity < 1.0f) found = e;
        }
        Assert.IsNotNull(found, "内積が 1.0 未満になるベクトルが見つからない");

        var copy = (float[])found.Clone();
        Assert.IsTrue(DINOv2Embedder.IsSimilar(found, copy, 1.0f), "完全に同じ画像はしきい値 100% でも似ている");
    }

    [TestMethod]
    public void Tolerance_DoesNotChangeOrdinarySimilarityDecisions()
    {
        var random = new Random(2);
        var a = Embedding(random);
        var b = Embedding(random);
        // b を a に少し寄せて、類似度がはっきり 1 未満の組を作る
        for (int i = 0; i < b.Length; i++) b[i] = 0.9f * a[i] + 0.1f * b[i];
        float s = DINOv2Embedder.BestMatch(a, b).Similarity;
        Assert.IsTrue(s < 0.999f, $"テストデータの類似度が高すぎる: {s}");

        Assert.IsTrue(DINOv2Embedder.IsSimilar(a, b, s - 0.001f));
        Assert.IsFalse(DINOv2Embedder.IsSimilar(a, b, s + 0.001f), "許容幅は 0.1% の差を埋めない");
        Assert.IsFalse(DINOv2Embedder.IsSimilar(a, b, 1.0f));
    }

    [TestMethod]
    public async Task GroupCache_FromOlderGroupingVersion_IsIgnoredAndRemovedOnSave()
    {
        var folder = Path.Combine(Path.GetTempPath(), "NimonoToleranceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CacheManager.UseFolder(folder);
            CacheManager.Initialize();
            var db = Path.Combine(folder, "cache.db");

            // 判定方法の版が無かったころの書き方（Mode が方式名だけ）
            using (var conn = new SqliteConnection($"Data Source={db}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO GroupCacheInfo (Mode, Threshold) VALUES ('DINOv2', 1.0);
                    INSERT INTO GroupCache (Mode, FilePath, FileSize, LastWriteTimeTicks, GroupKey) VALUES ('DINOv2', 'C:\x\a.jpg', 1, 1, NULL);
                ";
                cmd.ExecuteNonQuery();
            }

            Assert.IsNull(await CacheManager.LoadGroupCacheAsync("DINOv2"), "古い版のグループキャッシュは使わない");

            const string path = @"C:\x\b.jpg";
            CacheManager.MemoryCache[path] = new CacheEntry(1, 1, null, null);
            await CacheManager.SaveGroupCacheAsync("DINOv2", 1.0, [path], []);

            var snapshot = await CacheManager.LoadGroupCacheAsync("DINOv2");
            Assert.IsNotNull(snapshot);
            CollectionAssert.AreEquivalent(new[] { path }, snapshot.Files.Keys.ToList());

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM GroupCache WHERE Mode = 'DINOv2'";
                Assert.AreEqual(0L, (long)cmd.ExecuteScalar()!, "古い版の行は保存のときに消す");
                cmd.CommandText = "SELECT COUNT(*) FROM GroupCacheInfo WHERE Mode = 'DINOv2'";
                Assert.AreEqual(0L, (long)cmd.ExecuteScalar()!);
            }
        }
        finally
        {
            CacheManager.UseFolder(null);
            CacheManager.MemoryCache.Clear();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
}
