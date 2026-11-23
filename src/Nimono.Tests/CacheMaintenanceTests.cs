using Nimono;

namespace Nimono.Tests;

/// <summary>
/// 見つからなかったファイルの行の削除と、キャッシュを使えなかったときの記録を確かめる。
/// </summary>
[TestClass]
[DoNotParallelize] // CacheManager の場所と MemoryCache を共有するため
public class CacheMaintenanceTests
{
    private string _root = null!;
    private string _cacheFolder = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "NimonoCacheMaintenanceTests_" + Guid.NewGuid().ToString("N"));
        _cacheFolder = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_cacheFolder);
        CacheManager.UseFolder(_cacheFolder);
        CacheManager.ResetProblems();
    }

    [TestCleanup]
    public void Cleanup()
    {
        CacheManager.UseFolder(null);
        CacheManager.MemoryCache.Clear();
        CacheManager.ResetProblems();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task SaveAsync(params string[] paths)
    {
        foreach (var path in paths)
            CacheManager.MemoryCache[path] = new CacheEntry(1, 1, 42, null);
        await CacheManager.SaveCacheAsync(paths);
    }

    private static async Task<bool> IsCachedAsync(string path)
    {
        await CacheManager.LoadCacheAsync([path]);
        return CacheManager.MemoryCache.ContainsKey(path);
    }

    [TestMethod]
    public async Task PruneMissing_RemovesOnlyMissingFilesInsideScannedFolders()
    {
        var scanned = Directory.CreateDirectory(Path.Combine(_root, "photos")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        var unplugged = Path.Combine(_root, "unplugged"); // 作らない（外れたドライブの代わり）

        var kept = Path.Combine(scanned, "kept.jpg");
        var deleted = Path.Combine(scanned, "sub", "deleted.jpg");
        var outside = Path.Combine(other, "outside.jpg");
        var offline = Path.Combine(unplugged, "offline.jpg");
        // スキャンしたフォルダーと名前の前半が同じだけの別フォルダー
        var similarName = Path.Combine(_root, "photos2", "x.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(similarName)!);
        await SaveAsync(kept, deleted, outside, offline, similarName);

        int removed = await CacheManager.PruneMissingAsync([scanned, unplugged], [kept.ToUpperInvariant()]);

        Assert.AreEqual(1, removed);
        Assert.IsTrue(await IsCachedAsync(kept));
        Assert.IsFalse(await IsCachedAsync(deleted));
        Assert.IsTrue(await IsCachedAsync(outside), "スキャンしなかったフォルダーの行は残す");
        Assert.IsTrue(await IsCachedAsync(offline), "見つからないフォルダーの行は残す");
        Assert.IsTrue(await IsCachedAsync(similarName), "名前の前半が同じだけのフォルダーの行は残す");
        Assert.AreEqual(0, CacheManager.Problems.Count);
    }

    [TestMethod]
    public async Task LoadCache_CorruptDatabase_IsReportedAsProblem()
    {
        File.WriteAllText(Path.Combine(_cacheFolder, "cache.db"), "これは SQLite のファイルではない");

        await CacheManager.LoadCacheAsync([@"C:\x\1.jpg"]);

        Assert.AreEqual(1, CacheManager.Problems.Count);
        StringAssert.StartsWith(CacheManager.Problems[0], "読み込みに失敗しました");
    }

    [TestMethod]
    public async Task MissingDrive_IsReportedOnceAndNothingIsWritten()
    {
        var freeLetter = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(c => $"{(char)c}:\\")
            .FirstOrDefault(root => !Directory.Exists(root));
        if (freeLetter is null) Assert.Inconclusive("使われていないドライブ文字がない");

        CacheManager.UseFolder(Path.Combine(freeLetter, "NimonoCache"));

        await CacheManager.LoadCacheAsync([@"C:\x\1.jpg"]);
        await SaveAsync(@"C:\x\1.jpg");

        Assert.AreEqual(1, CacheManager.Problems.Count, "同じ問題は 1 回だけ記録する");
        StringAssert.Contains(CacheManager.Problems[0], "ドライブが見つかりません");
    }

    [TestMethod]
    public async Task ResetProblems_ClearsPreviousScan()
    {
        File.WriteAllText(Path.Combine(_cacheFolder, "cache.db"), "壊れた DB");
        await CacheManager.LoadCacheAsync([@"C:\x\1.jpg"]);
        Assert.AreEqual(1, CacheManager.Problems.Count);

        CacheManager.ResetProblems();

        Assert.AreEqual(0, CacheManager.Problems.Count);
    }
}
