using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Nimono;

internal record CacheEntry(long FileSize, long LastWriteTimeTicks, ulong? PHash, float[]? Embedding);

/// <summary>グループキャッシュの 1 ファイル分。GroupKey が null ならどのグループにも属さない。</summary>
internal record GroupCacheFile(long FileSize, long LastWriteTimeTicks, int? GroupKey);

/// <summary>前回スキャン時のグルーピング結果（方式ごとに 1 つ）。</summary>
internal record GroupCacheSnapshot(double Threshold, IReadOnlyDictionary<string, GroupCacheFile> Files);

internal static class CacheManager
{
    private const string DbFileName = "cache.db";

    /// <summary>DB 本体と一緒に動かす必要があるファイル（WAL モードの作業ファイル）の接尾辞。</summary>
    private static readonly string[] DbFileSuffixes = ["", "-wal", "-shm"];

    /// <summary>キャッシュの場所を指定しないときの保存先。</summary>
    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nimono");

    /// <summary>今キャッシュ DB を置いているフォルダー。</summary>
    public static string DbFolder { get; private set; } = DefaultFolder;

    private static string DbPath => Path.Combine(DbFolder, DbFileName);
    private static string ConnectionString => $"Data Source={DbPath}";

    /// <summary>設定に保存された場所を使う。空なら既定の場所。ファイルは動かさない。</summary>
    public static void UseFolder(string? folder)
    {
        DbFolder = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder;
    }

    /// <summary>指定したフォルダーに既にキャッシュ DB があるか。</summary>
    public static bool HasCacheFileIn(string folder) => File.Exists(Path.Combine(folder, DbFileName));

    public static bool IsSameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// キャッシュの場所を変える。<paramref name="moveExisting"/> が true なら今の DB を移動先へ移す
    /// （移動先に DB があれば上書きする）。false なら移動先の DB をそのまま使い、今の DB は残す。
    /// </summary>
    public static void ChangeFolder(string newFolder, bool moveExisting)
    {
        if (IsSameFolder(newFolder, DbFolder)) return;

        if (moveExisting && File.Exists(DbPath))
        {
            Directory.CreateDirectory(newFolder);
            // プールに残った接続が DB を開いたままだと移動できないため、先にすべて閉じる
            SqliteConnection.ClearAllPools();
            foreach (var suffix in DbFileSuffixes)
            {
                var src = DbPath + suffix;
                var dst = Path.Combine(newFolder, DbFileName + suffix);
                if (File.Exists(src))
                    File.Move(src, dst, overwrite: true);
                else if (File.Exists(dst))
                    File.Delete(dst); // 移動先に残った別 DB の作業ファイルが、移した DB に混ざらないようにする
            }
        }

        SqliteConnection.ClearAllPools();
        MemoryCache.Clear();
        DbFolder = newFolder;
    }

    // インメモリキャッシュ（スキャン中に使用）
    public static ConcurrentDictionary<string, CacheEntry> MemoryCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize()
    {
        Directory.CreateDirectory(DbFolder);
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        ApplyPragmas(connection);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS ImageCache (
                FilePath TEXT PRIMARY KEY,
                FileSize INTEGER NOT NULL,
                LastWriteTimeTicks INTEGER NOT NULL,
                PHash INTEGER,
                Embedding BLOB
            )
        ";
        command.ExecuteNonQuery();

        // グルーピング結果。前回スキャンの全ファイル（グループなしを含む）を方式ごとに持つ
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS GroupCache (
                Mode TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                FileSize INTEGER NOT NULL,
                LastWriteTimeTicks INTEGER NOT NULL,
                GroupKey INTEGER,
                PRIMARY KEY (Mode, FilePath)
            )
        ";
        command.ExecuteNonQuery();

        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS GroupCacheInfo (
                Mode TEXT PRIMARY KEY,
                Threshold REAL NOT NULL
            )
        ";
        command.ExecuteNonQuery();

        // 計算方法が変わったキャッシュは使えないため捨てる（グルーピング結果も、その値で決めたものなので捨てる）
        command.CommandText = "PRAGMA user_version";
        if ((long)(command.ExecuteScalar() ?? 0L) < SchemaVersion)
        {
            command.CommandText = $@"
                DELETE FROM ImageCache;
                DELETE FROM GroupCache;
                DELETE FROM GroupCacheInfo;
                PRAGMA user_version = {SchemaVersion};
            ";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// キャッシュの計算方法の版。上げると、次に開いたときに古いキャッシュを捨てて計算し直す。
    /// 2: ハッシュ・埋め込みを EXIF の向きに合わせてから計算し、埋め込みは 4 つの向きの分を持つ。
    /// </summary>
    private const long SchemaVersion = 2;

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL";
        cmd.ExecuteScalar();
        cmd.CommandText = "PRAGMA synchronous=NORMAL";
        cmd.ExecuteNonQuery();
    }

    public static bool HasCacheData()
    {
        try
        {
            if (!File.Exists(DbPath)) return false;
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ImageCache";
            var count = (long)(command.ExecuteScalar() ?? 0L);
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    public static long GetCacheSize()
    {
        try
        {
            if (!File.Exists(DbPath)) return 0;
            return new FileInfo(DbPath).Length;
        }
        catch
        {
            return 0;
        }
    }

    public static void ClearCache()
    {
        MemoryCache.Clear();
        try
        {
            if (!File.Exists(DbPath)) return;
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ImageCache";
            command.ExecuteNonQuery();

            // 古い DB にはグループキャッシュの表が無いため、無くても失敗しないようにする
            command.CommandText = "DROP TABLE IF EXISTS GroupCache; DROP TABLE IF EXISTS GroupCacheInfo";
            command.ExecuteNonQuery();
            
            // VACUUM to shrink the file
            command.CommandText = "VACUUM";
            command.ExecuteNonQuery();
        }
        catch
        {
            // Ignore errors
        }
    }

    public static async Task LoadCacheAsync(IReadOnlyList<string> filePaths)
    {
        MemoryCache.Clear();
        if (!File.Exists(DbPath) || filePaths.Count == 0) return;

        await Task.Run(() =>
        {
            try
            {
                // 古い版のキャッシュはここで捨てる（読み込む前に済ませる）
                Initialize();

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                // To prevent SQLite query length limits, batch the selection
                const int batchSize = 900;
                for (int i = 0; i < filePaths.Count; i += batchSize)
                {
                    var batch = filePaths.Skip(i).Take(batchSize).ToList();
                    var parameters = string.Join(",", batch.Select((_, idx) => $"@p{idx}"));
                    
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT FilePath, FileSize, LastWriteTimeTicks, PHash, Embedding FROM ImageCache WHERE FilePath IN ({parameters})";
                    
                    for (int j = 0; j < batch.Count; j++)
                    {
                        command.Parameters.AddWithValue($"@p{j}", batch[j]);
                    }

                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var path = reader.GetString(0);
                        var size = reader.GetInt64(1);
                        var ticks = reader.GetInt64(2);
                        ulong? pHash = reader.IsDBNull(3) ? null : (ulong)reader.GetInt64(3);
                        float[]? embedding = null;

                        if (!reader.IsDBNull(4))
                        {
                            var bytes = (byte[])reader.GetValue(4);
                            embedding = new float[bytes.Length / 4];
                            Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
                        }

                        MemoryCache[path] = new CacheEntry(size, ticks, pHash, embedding);
                    }
                }
            }
            catch
            {
                // Ignore load errors, we just recompute
            }
        });
    }

    public static async Task SaveCacheAsync(IReadOnlyList<string> updatedPaths)
    {
        if (updatedPaths.Count == 0) return;

        await Task.Run(() =>
        {
            try
            {
                Initialize();

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                ApplyPragmas(connection);
                using var transaction = connection.BeginTransaction();

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT OR REPLACE INTO ImageCache (FilePath, FileSize, LastWriteTimeTicks, PHash, Embedding)
                    VALUES (@FilePath, @FileSize, @LastWriteTimeTicks, @PHash, @Embedding)
                ";

                var pathParam = command.Parameters.Add("@FilePath", SqliteType.Text);
                var sizeParam = command.Parameters.Add("@FileSize", SqliteType.Integer);
                var ticksParam = command.Parameters.Add("@LastWriteTimeTicks", SqliteType.Integer);
                var pHashParam = command.Parameters.Add("@PHash", SqliteType.Integer);
                var embParam = command.Parameters.Add("@Embedding", SqliteType.Blob);

                foreach (var path in updatedPaths)
                {
                    if (MemoryCache.TryGetValue(path, out var entry))
                    {
                        pathParam.Value = path;
                        sizeParam.Value = entry.FileSize;
                        ticksParam.Value = entry.LastWriteTimeTicks;
                        
                        pHashParam.Value = entry.PHash.HasValue ? (long)entry.PHash.Value : DBNull.Value;

                        if (entry.Embedding != null)
                        {
                            var bytes = new byte[entry.Embedding.Length * 4];
                            Buffer.BlockCopy(entry.Embedding, 0, bytes, 0, bytes.Length);
                            embParam.Value = bytes;
                        }
                        else
                        {
                            embParam.Value = DBNull.Value;
                        }

                        command.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
            catch
            {
                // Ignore save errors
            }
        });
    }

    /// <summary>
    /// 前回スキャン時のグルーピング結果を読む。無い・読めない場合は null（全件を計算し直す）。
    /// </summary>
    public static async Task<GroupCacheSnapshot?> LoadGroupCacheAsync(string mode)
    {
        if (!File.Exists(DbPath)) return null;

        return await Task.Run(() =>
        {
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Threshold FROM GroupCacheInfo WHERE Mode = @Mode";
                command.Parameters.AddWithValue("@Mode", mode);
                if (command.ExecuteScalar() is not double threshold) return null;

                command.CommandText = "SELECT FilePath, FileSize, LastWriteTimeTicks, GroupKey FROM GroupCache WHERE Mode = @Mode";
                var files = new Dictionary<string, GroupCacheFile>(StringComparer.OrdinalIgnoreCase);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    int? groupKey = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                    files[reader.GetString(0)] = new GroupCacheFile(reader.GetInt64(1), reader.GetInt64(2), groupKey);
                }
                return new GroupCacheSnapshot(threshold, files);
            }
            catch
            {
                // 古い DB（表が無い）・読み取り失敗はキャッシュ無しとして全件を計算し直す
                return (GroupCacheSnapshot?)null;
            }
        });
    }

    /// <summary>
    /// 今回のグルーピング結果で、その方式のグループキャッシュを丸ごと置き換える。
    /// <paramref name="paths"/> は今回グルーピングの対象になった全ファイル（グループなしを含む）。
    /// </summary>
    public static async Task SaveGroupCacheAsync(
        string mode, double threshold, IReadOnlyList<string> paths, IReadOnlyList<ImageGroup> groups)
    {
        await Task.Run(() =>
        {
            try
            {
                Initialize();

                var groupKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                    foreach (var path in group.Paths)
                        groupKeys[path] = group.Id;

                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                ApplyPragmas(connection);
                using var transaction = connection.BeginTransaction();

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@Mode", mode);

                // 前回分を残すと、今回の対象外になったファイルの古い所属が次回に混ざるため、丸ごと入れ替える
                command.CommandText = "DELETE FROM GroupCache WHERE Mode = @Mode";
                command.ExecuteNonQuery();

                command.CommandText = "INSERT OR REPLACE INTO GroupCacheInfo (Mode, Threshold) VALUES (@Mode, @Threshold)";
                command.Parameters.AddWithValue("@Threshold", threshold);
                command.ExecuteNonQuery();

                command.CommandText = @"
                    INSERT OR REPLACE INTO GroupCache (Mode, FilePath, FileSize, LastWriteTimeTicks, GroupKey)
                    VALUES (@Mode, @FilePath, @FileSize, @LastWriteTimeTicks, @GroupKey)
                ";
                var pathParam = command.Parameters.Add("@FilePath", SqliteType.Text);
                var sizeParam = command.Parameters.Add("@FileSize", SqliteType.Integer);
                var ticksParam = command.Parameters.Add("@LastWriteTimeTicks", SqliteType.Integer);
                var groupParam = command.Parameters.Add("@GroupKey", SqliteType.Integer);

                foreach (var path in paths)
                {
                    if (!MemoryCache.TryGetValue(path, out var entry)) continue;

                    pathParam.Value = path;
                    sizeParam.Value = entry.FileSize;
                    ticksParam.Value = entry.LastWriteTimeTicks;
                    groupParam.Value = groupKeys.TryGetValue(path, out int key) ? key : DBNull.Value;
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                // Ignore save errors
            }
        });
    }
}
