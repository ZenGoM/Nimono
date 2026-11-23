using System.Collections.Concurrent;
using System.Numerics;
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

/// <summary>
/// 基準と比べた結果。Rotation は、その画像を右に何回 90° 回すと基準の向きにそろうか（0〜3）。
/// </summary>
internal readonly record struct ReferenceMatch(double Similarity, int Rotation);

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
        GroupCacheSnapshot? cache,
        IProgress<GroupingProgress>? progress,
        CancellationToken token)
    {
        int maxDistance = (int)Math.Floor(64 * (1.0 - similarityThreshold));
        var plan = PlanWithCache(entries.Select(e => e.Path).ToList(), cache, similarityThreshold);
        var uf = Connect(entries.Count, new HashSimilarity(entries, maxDistance), plan, progress, token);
        return BuildHashGroups(entries, uf);
    }

    /// <summary>向きが違うだけの画像も似ているとみなす。回したハッシュは組ごとに作らず、先に全件分作っておく。</summary>
    private readonly struct HashSimilarity : ISimilarity
    {
        private readonly ulong[] _hashes;
        private readonly ulong[] _rotated; // [i * 4 + r] = i 番目を右に r 回 90° 回したハッシュ
        private readonly int _maxDistance;

        public HashSimilarity(IReadOnlyList<ImageEntry> entries, int maxDistance)
        {
            _hashes = new ulong[entries.Count];
            _rotated = new ulong[entries.Count * 4];
            for (int i = 0; i < entries.Count; i++)
            {
                _hashes[i] = entries[i].Hash;
                ImageHasher.Rotations(entries[i].Hash).CopyTo(_rotated, i * 4);
            }
            _maxDistance = maxDistance;
        }

        public bool IsSimilar(int i, int j)
        {
            ulong hash = _hashes[i];
            for (int r = 0; r < 4; r++)
                if (BitOperations.PopCount(hash ^ _rotated[j * 4 + r]) <= _maxDistance) return true;
            return false;
        }
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
            var similarities = MatchToReference(entries[sortedByPath[0]].Path, hashes.Keys, hashes, null)!
                .ToDictionary(kv => kv.Key, kv => kv.Value.Similarity);
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
        string Path, DenseTensor<float>[] Tensors, long FileSize, long Ticks, ulong? CachedPHash);

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
        // バッチサイズ（画像の枚数）: GPU の per-Run overhead を分散させる。テンソル 8 個にすると DirectML で 1.5〜2× 高速化。
        // 1 枚あたり 4 つの向き（DINOv2Embedder.Rotations）を推論するので、GPU は 2 枚 = テンソル 8 個。
        // CPU EP では Run 自体が軽いので 1 枚ずつ（テンソル 4 個）。
        int batchSize = embedder.IsGpu ? 8 / DINOv2Embedder.Rotations : 1;
        int total = files.Count;

        // Producer-Consumer:
        //   - N 個の producer スレッドが画像をロード・前処理して queue に push
        //   - 1 個の consumer スレッドが queue から最大 batchSize 件まとめて pull → InferBatch
        // キャッシュヒットは GPU 不要なので producer 内で直接 entries に追加する（queue を経由しない）。
        // queue 容量はメモリと GPU starvation のバランス。1 件に 4 つの向きのテンソルが入るため、8 件 ≈ 8 × 4 × 600KB = 19MB 程度。
        var queue = new BlockingCollection<PreprocessedItem>(boundedCapacity: 8);
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
                                var tensors = embedder.Preprocess(file);
                                queue.Add(new PreprocessedItem(file, tensors, size, ticks, cache?.PHash), token);
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
                        var tensors = batch.SelectMany(item => item.Tensors).ToArray();
                        var embs = embedder.InferBatch(tensors);
                        for (int i = 0; i < batch.Count; i++)
                        {
                            var emb = DINOv2Embedder.Concat(embs, i * DINOv2Embedder.Rotations);
                            entries.Add(new EmbeddingEntry(batch[i].Path, emb));
                            CacheManager.MemoryCache[batch[i].Path] =
                                new CacheEntry(batch[i].FileSize, batch[i].Ticks, batch[i].CachedPHash, emb);
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
        GroupCacheSnapshot? cache,
        IProgress<GroupingProgress>? progress,
        CancellationToken token)
    {
        var plan = PlanWithCache(entries.Select(e => e.Path).ToList(), cache, similarityThreshold);
        var uf = Connect(entries.Count, new EmbeddingSimilarity(entries, (float)similarityThreshold), plan, progress, token);
        return BuildEmbeddingGroups(entries, uf);
    }

    private readonly struct EmbeddingSimilarity(IReadOnlyList<EmbeddingEntry> entries, float threshold) : ISimilarity
    {
        public bool IsSimilar(int i, int j) =>
            DINOv2Embedder.IsSimilar(entries[i].Embedding, entries[j].Embedding, threshold);
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
            var embeddings = sortedByPath.ToDictionary(
                i => entries[i].Path, i => entries[i].Embedding);

            var similarities = MatchToReference(
                    entries[sortedByPath[0]].Path, embeddings.Keys, new Dictionary<string, ulong>(), embeddings)!
                .ToDictionary(kv => kv.Key, kv => kv.Value.Similarity);

            var paths = sortedByPath
                .OrderByDescending(i => similarities[entries[i].Path])
                .Select(i => entries[i].Path)
                .ToList();

            groups.Add(new ImageGroup(
                id++, paths, similarities,
                new Dictionary<string, ulong>(), // Hashes は DINOv2 では不使用
                embeddings));
        }
        return groups;
    }

    // ── 共通ユーティリティ ────────────────────────────────────────────

    /// <summary>
    /// 基準と各画像を比べる。向きが違うだけの画像は、基準の向きにそろえて比べる（類似度は向きによらない）。
    /// 埋め込みに基準があれば埋め込みで、なければハッシュで比べる。どちらにも基準が無ければ null。
    /// グルーピング・メイン画面での基準変更・比較画面で同じ類似度になるよう、ここで計算する。
    /// </summary>
    public static Dictionary<string, ReferenceMatch>? MatchToReference(
        string reference,
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, ulong> hashes,
        IReadOnlyDictionary<string, float[]>? embeddings)
    {
        if (embeddings is not null && embeddings.TryGetValue(reference, out var refEmb))
        {
            return paths.ToDictionary(p => p, p =>
            {
                var (similarity, rotation) = DINOv2Embedder.BestMatch(refEmb, embeddings[p]);
                return new ReferenceMatch(similarity, rotation);
            });
        }
        if (hashes.TryGetValue(reference, out ulong refHash))
        {
            return paths.ToDictionary(p => p, p =>
            {
                var (distance, rotation) = ImageHasher.BestMatch(refHash, hashes[p]);
                return new ReferenceMatch(ImageHasher.Similarity(distance), rotation);
            });
        }
        return null;
    }

    /// <summary>
    /// <paramref name="reference"/> を基準にしたグループを返す。基準を先頭に置き、ほかは新しい基準との類似度が高い順に並べる。
    /// 類似度が同じ画像は <paramref name="tieOrder"/> の小さい順（指定がなければ今の並び順）にする。
    /// 類似度を計算できない（特徴量が無い）場合はグループをそのまま返す。
    /// </summary>
    public static ImageGroup WithReference(ImageGroup group, string reference, Func<string, int>? tieOrder = null)
    {
        var matches = MatchToReference(reference, group.Paths, group.Hashes, group.Embeddings);
        if (matches is null) return group;
        var similarities = matches.ToDictionary(kv => kv.Key, kv => kv.Value.Similarity);

        var currentOrder = group.Paths.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        tieOrder ??= p => currentOrder[p];
        var paths = group.Paths
            .Where(p => p != reference)
            .OrderByDescending(p => similarities[p])
            .ThenBy(tieOrder)
            .Prepend(reference)
            .ToList();
        return group with { Paths = paths, Similarities = similarities };
    }

    private interface ISimilarity
    {
        bool IsSimilar(int i, int j);
    }

    /// <summary>
    /// グループキャッシュから決めた、今回のグルーピングの進め方。
    /// </summary>
    /// <param name="Dirty">比較し直すファイル（index ごと）。</param>
    /// <param name="Seeds">そのまま 1 つのグループとして使う、前回のグループ（index の並び）。</param>
    private sealed record GroupingPlan(bool[] Dirty, IReadOnlyList<int[]> Seeds)
    {
        public int DirtyCount => Dirty.Count(d => d);
    }

    /// <summary>
    /// 前回のグルーピング結果のうち、使える部分を決める。
    ///
    /// - しきい値が違う・キャッシュが無い場合は全件を比較し直す。
    /// - 前回に無かったファイル・サイズか更新日時が変わったファイルは比較し直す。
    /// - グループは、メンバー全員が今回も対象で変わっていない場合だけそのまま使う。
    ///   1 人でも欠けた・変わった場合は、そのグループのメンバー全員を比較し直す
    ///   （欠けたファイルが橋渡しになっていた場合、グループが割れうるため）。
    /// - 前回どのグループにも属さず変わっていないファイルは、変わっていないファイル同士では似ていないと分かっているため、
    ///   比較し直すファイルとの間だけ比べる。
    ///
    /// こうすると、比較するのは「比較し直すファイル × 全ファイル」だけで、結果は全件を比較し直した場合と同じになる。
    /// </summary>
    private static GroupingPlan PlanWithCache(
        IReadOnlyList<string> paths, GroupCacheSnapshot? cache, double threshold)
    {
        var dirty = new bool[paths.Count];
        if (cache is null || Math.Abs(cache.Threshold - threshold) > 1e-9)
        {
            Array.Fill(dirty, true);
            return new GroupingPlan(dirty, []);
        }

        var indexOf = new Dictionary<string, int>(paths.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < paths.Count; i++)
        {
            indexOf[paths[i]] = i;
            dirty[i] = !(cache.Files.TryGetValue(paths[i], out var cached) &&
                         CacheManager.MemoryCache.TryGetValue(paths[i], out var current) &&
                         cached.FileSize == current.FileSize &&
                         cached.LastWriteTimeTicks == current.LastWriteTimeTicks);
        }

        var seeds = new List<int[]>();
        var cachedGroups = cache.Files
            .Where(f => f.Value.GroupKey.HasValue)
            .GroupBy(f => f.Value.GroupKey!.Value, f => f.Key);
        foreach (var group in cachedGroups)
        {
            var members = group.Select(p => indexOf.TryGetValue(p, out int i) ? i : -1).ToArray();
            if (members.All(i => i >= 0 && !dirty[i]))
            {
                seeds.Add(members);
            }
            else
            {
                foreach (int i in members)
                    if (i >= 0) dirty[i] = true;
            }
        }
        return new GroupingPlan(dirty, seeds);
    }

    /// <summary>
    /// 似ているファイル同士をつなぐ。<paramref name="plan"/> の Seeds はそのままつなぎ、
    /// 比較し直すファイル（Dirty）だけを全ファイルと比べる。
    /// </summary>
    private static UnionFind Connect<TSimilarity>(
        int n, TSimilarity similarity, GroupingPlan plan,
        IProgress<GroupingProgress>? progress, CancellationToken token)
        where TSimilarity : struct, ISimilarity
    {
        var uf = new UnionFind(n);
        foreach (var seed in plan.Seeds)
            for (int k = 1; k < seed.Length; k++)
                uf.Union(seed[0], seed[k]);

        var dirty = plan.Dirty;
        var rows = Enumerable.Range(0, n).Where(i => dirty[i]).ToArray();
        int total = rows.Length;
        int processed = 0;
        ScanLogger.Log($"グループキャッシュ: 再利用={plan.Seeds.Count}グループ, 比較し直し={total}件 / {n}件");

        // 並列フェーズ: 一致ペアをスレッドローカルリストに収集
        var allEdges = new List<List<(int, int)>>();
        object edgeLock = new();

        Parallel.For(0, total,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            () => new List<(int, int)>(),
            (r, state, localEdges) =>
            {
                if (token.IsCancellationRequested) { state.Stop(); return localEdges; }
                int i = rows[r];
                // 比較し直すファイル同士は i < j の側だけで比べる（同じ組を二度比べない）
                for (int j = 0; j < n; j++)
                {
                    if (j == i || (dirty[j] && j < i)) continue;
                    if (similarity.IsSimilar(i, j))
                        localEdges.Add((i, j));
                }
                int p = Interlocked.Increment(ref processed);
                if (p % 16 == 0 || p == total)
                    progress?.Report(new GroupingProgress("グルーピング中", p, total));
                return localEdges;
            },
            localEdges => { lock (edgeLock) allEdges.Add(localEdges); });

        token.ThrowIfCancellationRequested();

        // シリアルフェーズ: Union-Find にエッジを適用
        foreach (var list in allEdges)
            foreach (var (a, b) in list)
                uf.Union(a, b);

        return uf;
    }

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
