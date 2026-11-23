namespace Nimono.Tests;

/// <summary>
/// グループキャッシュを使ったグルーピングが、全件を比較し直した場合と同じ結果になることを確かめる。
/// </summary>
[TestClass]
[DoNotParallelize] // CacheManager.MemoryCache を共有するため
public class GroupCacheTests
{
    private const double Threshold = 0.9; // pHash のハミング距離 6 以下

    [TestMethod]
    public void Group_WithCache_MatchesFullRecompute_AfterChangesAddsAndRemovals()
    {
        var random = new Random(12345);
        string prefix = $@"X:\gc-{Guid.NewGuid():N}\";

        // 似た画像のかたまり（元ハッシュから数ビット変えたもの）と、どれにも似ていない画像を混ぜる
        var files = new List<(string Path, ulong Hash)>();
        for (int c = 0; c < 40; c++)
        {
            ulong baseHash = NextHash(random);
            int size = random.Next(1, 5);
            for (int k = 0; k < size; k++)
                files.Add(($"{prefix}c{c}_{k}.jpg", FlipBits(baseHash, random, random.Next(0, 4))));
        }

        var before = Register(files, ticks: 1);
        var firstGroups = ImageGrouper.Group(before, Threshold, null, null, CancellationToken.None);
        var snapshot = ToSnapshot(before, firstGroups, Threshold);

        // 変更: 一部のファイルの中身が変わる（ハッシュも変わる）、一部が消える、新しいファイルが増える。
        // 新しいファイルには既存のかたまりに似たもの（グループに加わる・グループどうしをつなぐ）を含める
        var after = new List<(string Path, ulong Hash)>();
        for (int i = 0; i < files.Count; i++)
        {
            if (i % 17 == 3) continue;                                                  // 削除
            if (i % 11 == 5) { after.Add((files[i].Path, NextHash(random))); continue; } // 変更
            after.Add(files[i]);
        }
        for (int k = 0; k < 15; k++)
            after.Add(($"{prefix}new{k}.jpg", FlipBits(files[random.Next(files.Count)].Hash, random, 2)));

        var changed = new HashSet<string>(
            files.Where((f, i) => i % 11 == 5).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var current = Register(after, ticks: 1, changed);

        var withCache = ImageGrouper.Group(current, Threshold, snapshot, null, CancellationToken.None);
        var full = ImageGrouper.Group(current, Threshold, null, null, CancellationToken.None);

        CollectionAssert.AreEquivalent(Partition(full), Partition(withCache));
        Assert.IsTrue(full.Count > 0, "テストデータにグループができていない");
    }

    [TestMethod]
    public void Group_WithCacheOfDifferentThreshold_RecomputesEverything()
    {
        var random = new Random(7);
        string prefix = $@"X:\gc-{Guid.NewGuid():N}\";
        ulong a = NextHash(random);
        var files = new List<(string, ulong)>
        {
            ($"{prefix}a1.jpg", a),
            ($"{prefix}a2.jpg", FlipBits(a, random, 5)), // 0.9 では同じグループ、0.95 では別
        };
        var entries = Register(files, ticks: 1);

        var loose = ImageGrouper.Group(entries, 0.9, null, null, CancellationToken.None);
        Assert.AreEqual(1, loose.Count);

        // 0.9 のときのキャッシュを 0.95 で使っても、前回のグループを持ち越さない
        var strict = ImageGrouper.Group(entries, 0.95, ToSnapshot(entries, loose, 0.9), null, CancellationToken.None);
        Assert.AreEqual(0, strict.Count);
    }

    private static List<ImageEntry> Register(
        IEnumerable<(string Path, ulong Hash)> files, long ticks, ISet<string>? changed = null)
    {
        var entries = new List<ImageEntry>();
        foreach (var (path, hash) in files)
        {
            long t = changed != null && changed.Contains(path) ? ticks + 1 : ticks;
            CacheManager.MemoryCache[path] = new CacheEntry(100, t, hash, null);
            entries.Add(new ImageEntry(path, hash));
        }
        return entries;
    }

    private static GroupCacheSnapshot ToSnapshot(
        IReadOnlyList<ImageEntry> entries, IReadOnlyList<ImageGroup> groups, double threshold)
    {
        var keys = groups.SelectMany(g => g.Paths.Select(p => (p, g.Id)))
                         .ToDictionary(x => x.p, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var files = entries.ToDictionary(
            e => e.Path,
            e =>
            {
                var cached = CacheManager.MemoryCache[e.Path];
                return new GroupCacheFile(cached.FileSize, cached.LastWriteTimeTicks,
                                          keys.TryGetValue(e.Path, out int k) ? k : null);
            },
            StringComparer.OrdinalIgnoreCase);
        return new GroupCacheSnapshot(threshold, files);
    }

    /// <summary>グループ ID や並び順に左右されない比較用の形（各グループのパスを並べて連結）。</summary>
    private static List<string> Partition(IReadOnlyList<ImageGroup> groups) =>
        groups.Select(g => string.Join("|", g.Paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))).ToList();

    private static ulong NextHash(Random random)
    {
        Span<byte> bytes = stackalloc byte[8];
        random.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    private static ulong FlipBits(ulong hash, Random random, int count)
    {
        var bits = Enumerable.Range(0, 64).OrderBy(_ => random.Next()).Take(count);
        foreach (int bit in bits) hash ^= 1UL << bit;
        return hash;
    }
}
