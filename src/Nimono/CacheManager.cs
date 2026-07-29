using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Nimono;

internal record CacheEntry(long FileSize, long LastWriteTimeTicks, ulong? PHash, float[]? Embedding);

internal static class CacheManager
{
    private static readonly string DbFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nimono");
    private static readonly string DbPath = Path.Combine(DbFolder, "cache.db");
    private static readonly string ConnectionString = $"Data Source={DbPath}";

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
    }

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
}
