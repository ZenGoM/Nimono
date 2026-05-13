using System.Collections.Concurrent;

namespace Nimono;

internal record ImageEntry(string Path, ulong Hash);

internal record ImageGroup(int Id, IReadOnlyList<string> Paths);

internal record GroupingProgress(string Phase, int Current, int Total);

internal static class ImageGrouper
{
    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp" };
    private static readonly string[] SkipDirs =
        { "windows", "program files", "program files (x86)", "$recycle.bin", "system volume information" };

    public static IReadOnlyList<string> EnumerateImages(
        IEnumerable<string> folders, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var folder in folders)
        {
            if (token.IsCancellationRequested) break;
            if (!Directory.Exists(folder)) continue;
            CollectImages(folder, result, seen, token);
        }
        return result;
    }

    private static void CollectImages(
        string dir, List<string> result, HashSet<string> seen, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        var name = Path.GetFileName(dir).ToLowerInvariant();
        if (SkipDirs.Contains(name)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (token.IsCancellationRequested) return;
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!ImageExtensions.Contains(ext)) continue;
                var full = Path.GetFullPath(file);
                if (seen.Add(full)) result.Add(full);
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
                CollectImages(sub, result, seen, token);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    public static IReadOnlyList<ImageEntry> ComputeHashes(
        IReadOnlyList<string> files,
        IProgress<GroupingProgress>? progress,
        CancellationToken token)
    {
        var entries = new ConcurrentBag<ImageEntry>();
        int done = 0;
        int total = files.Count;

        Parallel.ForEach(
            files,
            new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
            },
            file =>
            {
                try
                {
                    var hash = ImageHasher.Compute(file);
                    entries.Add(new ImageEntry(file, hash));
                }
                catch { }
                int n = Interlocked.Increment(ref done);
                if (n % 8 == 0 || n == total)
                    progress?.Report(new GroupingProgress("ハッシュ計算中", n, total));
            });

        return entries.ToList();
    }

    public static IReadOnlyList<ImageGroup> Group(
        IReadOnlyList<ImageEntry> entries,
        double similarityThreshold,
        IProgress<GroupingProgress>? progress,
        CancellationToken token)
    {
        int n = entries.Count;
        var uf = new UnionFind(n);
        // 64bit ハッシュなので、距離 = 64 * (1 - sim)
        int maxDistance = (int)Math.Floor(64 * (1.0 - similarityThreshold));

        int processed = 0;
        for (int i = 0; i < n; i++)
        {
            if (token.IsCancellationRequested) break;
            var hi = entries[i].Hash;
            for (int j = i + 1; j < n; j++)
            {
                if (ImageHasher.HammingDistance(hi, entries[j].Hash) <= maxDistance)
                    uf.Union(i, j);
            }
            processed++;
            if (processed % 16 == 0 || processed == n)
                progress?.Report(new GroupingProgress("グルーピング中", processed, n));
        }

        var buckets = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = uf.Find(i);
            if (!buckets.TryGetValue(root, out var list))
            {
                list = new List<int>();
                buckets[root] = list;
            }
            list.Add(i);
        }

        var groups = new List<ImageGroup>();
        int id = 1;
        foreach (var kv in buckets.Where(b => b.Value.Count >= 2)
                                  .OrderByDescending(b => b.Value.Count))
        {
            var paths = kv.Value.Select(i => entries[i].Path).OrderBy(p => p).ToList();
            groups.Add(new ImageGroup(id++, paths));
        }

        return groups;
    }

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _rank;

        public UnionFind(int size)
        {
            _parent = new int[size];
            _rank = new int[size];
            for (int i = 0; i < size; i++) _parent[i] = i;
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            if (_rank[ra] == _rank[rb]) _rank[ra]++;
        }
    }
}
