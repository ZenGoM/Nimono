using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Nimono;

internal record ImageEntry(string Path, ulong Hash);

internal record EmbeddingEntry(string Path, float[] Embedding);

internal record ImageGroup(
    int Id,
    IReadOnlyList<string> Paths,
    IReadOnlyDictionary<string, double> Similarities,
    IReadOnlyDictionary<string, ulong> Hashes,
    IReadOnlyDictionary<string, float[]>? Embeddings = null);

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

    // ── pHash ─────────────────────────────────────────────────────────

    public static (IReadOnlyList<ImageEntry> Entries, IReadOnlyList<string> NewPaths) ComputeHashes(
        IReadOnlyList<string> files,
        IProgress<GroupingProgress>? progress,
        CancellationToken token)
    {
        var entries = new ConcurrentBag<ImageEntry>();
        var newPaths = new ConcurrentBag<string>();
        int done = 0;
        int failed = 0;
        int loggedErrors = 0;
        const int MaxLoggedErrors = 5;
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
                    var fi = new FileInfo(file);
                    long size = fi.Length;
                    long ticks = fi.LastWriteTimeUtc.Ticks;

                    if (CacheManager.MemoryCache.TryGetValue(file, out var cache) &&
                        cache.FileSize == size &&
                        cache.LastWriteTimeTicks == ticks &&
                        cache.PHash.HasValue)
                    {
                        entries.Add(new ImageEntry(file, cache.PHash.Value));
                    }
                    else
                    {
                        var hash = ImageHasher.Compute(file);
                        entries.Add(new ImageEntry(file, hash));
                        CacheManager.MemoryCache[file] = new CacheEntry(size, ticks, hash, cache?.Embedding);
                        newPaths.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    if (Interlocked.Increment(ref loggedErrors) <= MaxLoggedErrors)
                        ScanLogger.Log($"pHash計算失敗: {ex.GetType().Name}: {ex.Message} ({file})");
                }
                int n = Interlocked.Increment(ref done);
                if (n % 8 == 0 || n == total)
                    progress?.Report(new GroupingProgress("ハッシュ計算中", n, total));
            });

        if (failed > 0)
            ScanLogger.Log($"pHash計算失敗合計: {failed}件 / {total}件中");

        return (entries.ToList(), newPaths.ToList());
    }

    public static IReadOnlyList<ImageGroup> Group(
        IReadOnlyList<ImageEntry> entries,
        double similarityThreshold,
        IProgress<GroupingProgress>? progress,
        CancellationToken token)
    {
        int n = entries.Count;
        int maxDistance = (int)Math.Floor(64 * (1.0 - similarityThreshold));
        int processed = 0;

        // 並列フェーズ: 一致ペアをスレッドローカルリストに収集
        var allEdges = new List<List<(int, int)>>();
        object edgeLock = new();

        Parallel.For(0, n,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            () => new List<(int, int)>(),
            (i, state, localEdges) =>
            {
                if (token.IsCancellationRequested) { state.Stop(); return localEdges; }
                var hi = entries[i].Hash;
                for (int j = i + 1; j < n; j++)
                    if (ImageHasher.HammingDistance(hi, entries[j].Hash) <= maxDistance)
                        localEdges.Add((i, j));
                int p = Interlocked.Increment(ref processed);
                if (p % 16 == 0 || p == n)
                    progress?.Report(new GroupingProgress("グルーピング中", p, n));
                return localEdges;
            },
            localEdges => { lock (edgeLock) allEdges.Add(localEdges); });

        // シリアルフェーズ: Union-Find にエッジを適用
        var uf = new UnionFind(n);
        foreach (var list in allEdges)
            foreach (var (a, b) in list)
                uf.Union(a, b);

        return BuildHashGroups(entries, uf);
    }

    private static IReadOnlyList<ImageGroup> BuildHashGroups(
        IReadOnlyList<ImageEntry> entries, UnionFind uf)
    {
        var buckets = BuildBuckets(entries.Count, uf);
        var groups = new List<ImageGroup>();
        int id = 1;
        foreach (var kv in buckets.Where(b => b.Value.Count >= 2)
                                  .OrderByDescending(b => b.Value.Count))
        {
            var sortedByPath = kv.Value.OrderBy(i => entries[i].Path).ToList();
            var hashes = sortedByPath.ToDictionary(i => entries[i].Path, i => entries[i].Hash);
            ulong refHash = entries[sortedByPath[0]].Hash;
            var similarities = new Dictionary<string, double>();
            foreach (var idx in sortedByPath)
                similarities[entries[idx].Path] = ImageHasher.Similarity(refHash, entries[idx].Hash);
            var paths = sortedByPath
                .OrderByDescending(i => similarities[entries[i].Path])
                .Select(i => entries[i].Path)
                .ToList();
            groups.Add(new ImageGroup(id++, paths, similarities, hashes));
        }
        return groups;
    }

    // ── DINOv2 ────────────────────────────────────────────────────────

    private sealed record PreprocessedItem(
        string Path, DenseTensor<float> Tensor, long FileSize, long Ticks, ulong? CachedPHash);

    public static (IReadOnlyList<EmbeddingEntry> Entries, IReadOnlyList<string> NewPaths) ComputeEmbeddings(
        IReadOnlyList<string> files,
        DINOv2Embedder embedder,
        IProgress<GroupingProgress>? progress,
        CancellationToken token)
    {
        var entries = new ConcurrentBag<EmbeddingEntry>();
        var newPaths = new ConcurrentBag<string>();
        int done = 0;
        int failed = 0;
        int loggedErrors = 0;
        int sampledBatches = 0;
        const int MaxLoggedErrors = 5;
        const int MaxSampleBatches = 5;
        // バッチサイズ: GPU の per-Run overhead を分散させる。8 にすると DirectML で 1.5〜2× 高速化。
        // CPU EP では Run 自体が軽いのでバッチ不要（バッチサイズ=1）。
        int batchSize = embedder.IsGpu ? 8 : 1;
        int total = files.Count;

        // Producer-Consumer:
        //   - N 個の producer スレッドが画像をロード・前処理して queue に push
        //   - 1 個の consumer スレッドが queue から最大 batchSize 件まとめて pull → InferBatch
        // キャッシュヒットは GPU 不要なので producer 内で直接 entries に追加する（queue を経由しない）。
        // queue 容量はメモリと GPU starvation のバランス。32 件 ≈ 32 × 600KB = 19MB 程度。
        var queue = new BlockingCollection<PreprocessedItem>(boundedCapacity: 32);
        int parallelism = Math.Max(1, Environment.ProcessorCount / 2);

        // ── Producer ────────────────────────────────────────────────
        var producerTask = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(files,
                    new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = parallelism },
                    file =>
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            long size = fi.Length;
                            long ticks = fi.LastWriteTimeUtc.Ticks;

                            if (CacheManager.MemoryCache.TryGetValue(file, out var cache) &&
                                cache.FileSize == size &&
                                cache.LastWriteTimeTicks == ticks &&
                                cache.Embedding != null)
                            {
                                entries.Add(new EmbeddingEntry(file, cache.Embedding));
                                int n = Interlocked.Increment(ref done);
                                if (n % 4 == 0 || n == total)
                                    progress?.Report(new GroupingProgress("特徴量計算中 (DINOv2)", n, total));
                            }
                            else
                            {
                                var tensor = embedder.Preprocess(file);
                                queue.Add(new PreprocessedItem(file, tensor, size, ticks, cache?.PHash), token);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failed);
                            if (Interlocked.Increment(ref loggedErrors) <= MaxLoggedErrors)
                                ScanLogger.Log($"DINOv2前処理失敗: {ex.GetType().Name}: {ex.Message} ({file})");
                            int n = Interlocked.Increment(ref done);
                            if (n % 4 == 0 || n == total)
                                progress?.Report(new GroupingProgress("特徴量計算中 (DINOv2)", n, total));
                        }
                    });
            }
            catch (OperationCanceledException) { /* キャンセル時の通常パス */ }
            finally
            {
                queue.CompleteAdding();
            }
        }, token);

        // ── Consumer (GPU、単一スレッド) ──────────────────────────────
        var consumerTask = Task.Run(() =>
        {
            var batch = new List<PreprocessedItem>(batchSize);
            try
            {
                // 1 件目は無期限ブロック、以降は短時間（15ms）待って追加アイテムを集める。
                // これで producer が GDI+ ロックで遅くなっていても、わずかな待ちでバッチを満杯にできる。
                // producer が完全に停滞しているなら短時間でタイムアウトして部分バッチで処理（待っても無駄）。
                // ペナルティ最大 = (batchSize-1) × FillTimeoutMs = 7 × 15 = 105ms/バッチ、
                // それ以上に batching で稼げる時間の方が大きい。
                const int FillTimeoutMs = 15;
                while (queue.TryTake(out var first, Timeout.Infinite, token))
                {
                    batch.Add(first);
                    while (batch.Count < batchSize && queue.TryTake(out var next, FillTimeoutMs))
                        batch.Add(next);

                    bool sample = Interlocked.Increment(ref sampledBatches) <= MaxSampleBatches;
                    var sw = sample ? System.Diagnostics.Stopwatch.StartNew() : null;

                    try
                    {
                        var tensors = new DenseTensor<float>[batch.Count];
                        for (int i = 0; i < batch.Count; i++) tensors[i] = batch[i].Tensor;
                        var embs = embedder.InferBatch(tensors);
                        for (int i = 0; i < batch.Count; i++)
                        {
                            entries.Add(new EmbeddingEntry(batch[i].Path, embs[i]));
                            CacheManager.MemoryCache[batch[i].Path] =
                                new CacheEntry(batch[i].FileSize, batch[i].Ticks, batch[i].CachedPHash, embs[i]);
                            newPaths.Add(batch[i].Path);
                            int n = Interlocked.Increment(ref done);
                            if (n % 4 == 0 || n == total)
                                progress?.Report(new GroupingProgress("特徴量計算中 (DINOv2)", n, total));
                        }

                        if (sample)
                            ScanLogger.Log($"DINOv2バッチ #{sampledBatches}: batch={batch.Count}枚 infer={sw!.ElapsedMilliseconds}ms ({(double)sw.ElapsedMilliseconds / batch.Count:F1}ms/枚)");
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Add(ref failed, batch.Count);
                        if (Interlocked.Increment(ref loggedErrors) <= MaxLoggedErrors)
                            ScanLogger.Log($"DINOv2バッチ推論失敗: batch={batch.Count}枚 {ex.GetType().Name}: {ex.Message}");
                        int n = Interlocked.Add(ref done, batch.Count);
                        progress?.Report(new GroupingProgress("特徴量計算中 (DINOv2)", n, total));
                    }
                    batch.Clear();
                }
            }
            catch (OperationCanceledException) { /* キャンセル */ }
        }, token);

        try { Task.WaitAll([producerTask, consumerTask], token); }
        catch (OperationCanceledException) { }
        catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException)) { }

        var entryList = entries.ToList();
        ScanLogger.Log($"DINOv2埋め込み内訳: 成功={entryList.Count}件, 失敗={failed}件, 列挙={total}件");
        return (entryList, newPaths.ToList());
    }

    public static IReadOnlyList<ImageGroup> GroupByEmbedding(
        IReadOnlyList<EmbeddingEntry> entries,
        double similarityThreshold,
        IProgress<GroupingProgress>? progress,
        CancellationToken token)
    {
        int n = entries.Count;
        float threshold = (float)similarityThreshold;
        int processed = 0;

        // 並列フェーズ: 一致ペアをスレッドローカルリストに収集
        var allEdges = new List<List<(int, int)>>();
        object edgeLock = new();

        Parallel.For(0, n,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            () => new List<(int, int)>(),
            (i, state, localEdges) =>
            {
                if (token.IsCancellationRequested) { state.Stop(); return localEdges; }
                var ei = entries[i].Embedding;
                for (int j = i + 1; j < n; j++)
                    if (DINOv2Embedder.CosineSimilarity(ei, entries[j].Embedding) >= threshold)
                        localEdges.Add((i, j));
                int p = Interlocked.Increment(ref processed);
                if (p % 16 == 0 || p == n)
                    progress?.Report(new GroupingProgress("グルーピング中", p, n));
                return localEdges;
            },
            localEdges => { lock (edgeLock) allEdges.Add(localEdges); });

        // シリアルフェーズ: Union-Find にエッジを適用
        var uf = new UnionFind(n);
        foreach (var list in allEdges)
            foreach (var (a, b) in list)
                uf.Union(a, b);

        return BuildEmbeddingGroups(entries, uf);
    }

    private static IReadOnlyList<ImageGroup> BuildEmbeddingGroups(
        IReadOnlyList<EmbeddingEntry> entries, UnionFind uf)
    {
        var buckets = BuildBuckets(entries.Count, uf);
        var groups = new List<ImageGroup>();
        int id = 1;
        foreach (var kv in buckets.Where(b => b.Value.Count >= 2)
                                  .OrderByDescending(b => b.Value.Count))
        {
            var sortedByPath = kv.Value.OrderBy(i => entries[i].Path).ToList();
            var refEmb = entries[sortedByPath[0]].Embedding;

            var similarities = new Dictionary<string, double>();
            foreach (var idx in sortedByPath)
                similarities[entries[idx].Path] =
                    DINOv2Embedder.CosineSimilarity(refEmb, entries[idx].Embedding);

            var paths = sortedByPath
                .OrderByDescending(i => similarities[entries[i].Path])
                .Select(i => entries[i].Path)
                .ToList();

            var embeddings = sortedByPath.ToDictionary(
                i => entries[i].Path, i => entries[i].Embedding);

            groups.Add(new ImageGroup(
                id++, paths, similarities,
                new Dictionary<string, ulong>(), // Hashes は DINOv2 では不使用
                embeddings));
        }
        return groups;
    }

    // ── 共通ユーティリティ ────────────────────────────────────────────

    private static Dictionary<int, List<int>> BuildBuckets(int count, UnionFind uf)
    {
        var buckets = new Dictionary<int, List<int>>();
        for (int i = 0; i < count; i++)
        {
            int root = uf.Find(i);
            if (!buckets.TryGetValue(root, out var list))
            {
                list = new List<int>();
                buckets[root] = list;
            }
            list.Add(i);
        }
        return buckets;
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
