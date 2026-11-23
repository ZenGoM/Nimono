using Nimono;

namespace Nimono.Tests;

[TestClass]
[DoNotParallelize] // CacheManager の場所と MemoryCache を共有するため
public class CacheFolderTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "NimonoCacheFolderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        CacheManager.UseFolder(null);
        CacheManager.MemoryCache.Clear();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task SaveOneAsync(string path, ulong hash)
    {
        CacheManager.MemoryCache[path] = new CacheEntry(123, 456, hash, null);
        await CacheManager.SaveCacheAsync([path]);
    }

    private static async Task<ulong?> LoadHashAsync(string path)
    {
        await CacheManager.LoadCacheAsync([path]);
        return CacheManager.MemoryCache.TryGetValue(path, out var e) ? e.PHash : null;
    }

    [TestMethod]
    public async Task ChangeFolder_Move_CarriesCacheToNewFolder()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        CacheManager.UseFolder(a);
        await SaveOneAsync(@"C:\x\1.jpg", 42);

        CacheManager.ChangeFolder(b, moveExisting: true);

        Assert.AreEqual(b, CacheManager.DbFolder);
        Assert.IsTrue(CacheManager.HasCacheFileIn(b));
        Assert.IsFalse(CacheManager.HasCacheFileIn(a));
        Assert.AreEqual(42UL, await LoadHashAsync(@"C:\x\1.jpg"));
    }

    [TestMethod]
    public async Task ChangeFolder_NoMove_UsesDestinationCacheAndKeepsOld()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        CacheManager.UseFolder(b);
        await SaveOneAsync(@"C:\x\1.jpg", 7);
        CacheManager.ChangeFolder(a, moveExisting: false);
        await SaveOneAsync(@"C:\x\1.jpg", 99);

        CacheManager.ChangeFolder(b, moveExisting: false);

        Assert.IsTrue(CacheManager.HasCacheFileIn(a));
        Assert.AreEqual(7UL, await LoadHashAsync(@"C:\x\1.jpg"));
    }
}
