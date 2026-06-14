using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Monocle.Core.Imaging;
using Monocle.Core.Model;

namespace Monocle.Core.Cache;

/// <summary>
/// Per-shoot cache (SQLite for metrics/EXIF, on-disk blobs for preview JPEGs), keyed by a
/// file fingerprint (size + mtime) so results auto-invalidate when a file changes
/// (FEATURES §8, #19). Lives in a <c>.monocle-cache</c> folder inside the shoot.
/// </summary>
public sealed class ShootCache : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly string _previewDir;

    // Analysis runs through Parallel.ForEachAsync (up to 8 threads) sharing this one instance,
    // but Microsoft.Data.Sqlite does not support concurrent commands/readers on a single
    // connection. Every DB access is serialized through this gate; the operations are short.
    private readonly object _gate = new();

    public ShootCache(string shootFolder)
    {
        var cacheDir = Path.Combine(shootFolder, ".monocle-cache");
        Directory.CreateDirectory(cacheDir);
        _previewDir = Path.Combine(cacheDir, "previews");
        Directory.CreateDirectory(_previewDir);

        // Pooling=False: one long-lived connection per shoot; pooling would keep the db file
        // locked after Dispose, blocking reopening the shoot or moving the folder.
        _db = new SqliteConnection($"Data Source={Path.Combine(cacheDir, "cache.db")};Pooling=False");
        _db.Open();
        // WAL + a busy timeout keep readers and the writer from tripping over each other if the
        // db is ever touched concurrently (e.g. a future cross-process reader).
        Exec("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        Exec("""
            CREATE TABLE IF NOT EXISTS items (
                id TEXT PRIMARY KEY,
                fingerprint TEXT NOT NULL,
                metrics TEXT,
                exif TEXT
            );
            CREATE TABLE IF NOT EXISTS scores (
                id TEXT NOT NULL,
                modelId TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                json TEXT NOT NULL,
                PRIMARY KEY (id, modelId)
            );
            CREATE TABLE IF NOT EXISTS previews (
                path TEXT PRIMARY KEY,
                id TEXT NOT NULL,
                fingerprint TEXT NOT NULL
            );
            """);
    }

    /// <summary>Cached model scores whose fingerprint still matches the file.</summary>
    public List<ModelScore> GetScores(string id, string fingerprint)
    {
        lock (_gate)
        {
            var result = new List<ModelScore>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT json FROM scores WHERE id = $id AND fingerprint = $fp";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var score = JsonSerializer.Deserialize<ModelScore>(r.GetString(0));
                if (score is not null)
                    result.Add(score);
            }
            return result;
        }
    }

    public void PutScore(string id, string fingerprint, ModelScore score)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO scores (id, modelId, fingerprint, json) VALUES ($id, $m, $fp, $j)
                ON CONFLICT(id, modelId) DO UPDATE SET fingerprint=$fp, json=$j;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$m", score.ModelId);
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(score));
            cmd.ExecuteNonQuery();
        }
    }

    public bool TryGetAnalysis(string id, string fingerprint, out TechnicalMetrics? metrics, out ExifInfo? exif)
    {
        lock (_gate)
        {
            metrics = null;
            exif = null;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT fingerprint, metrics, exif FROM items WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.GetString(0) != fingerprint)
                return false;

            metrics = r.IsDBNull(1) ? null : JsonSerializer.Deserialize<TechnicalMetrics>(r.GetString(1));
            exif = r.IsDBNull(2) ? null : JsonSerializer.Deserialize<ExifInfo>(r.GetString(2));
            return metrics is not null;
        }
    }

    public void PutAnalysis(string id, string fingerprint, TechnicalMetrics metrics, ExifInfo exif)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO items (id, fingerprint, metrics, exif) VALUES ($id, $fp, $m, $e)
                ON CONFLICT(id) DO UPDATE SET fingerprint=$fp, metrics=$m, exif=$e;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$m", JsonSerializer.Serialize(metrics));
            cmd.Parameters.AddWithValue("$e", JsonSerializer.Serialize(exif));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Path to a cached preview at the given size + rotation + crop, or null on a miss.</summary>
    public string? GetPreviewPath(string id, string fingerprint, int longEdge, int rotation = 0, string cropTag = "")
    {
        var path = PreviewPath(id, fingerprint, longEdge, rotation, cropTag);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Write a preview JPEG to the cache and return its path. Old previews for the same
    /// frame whose fingerprint is now stale (the file changed) are pruned so the blob folder
    /// doesn't grow unbounded across edits.</summary>
    public string PutPreview(string id, string fingerprint, int longEdge, int rotation, byte[] jpeg, string cropTag = "")
    {
        var path = PreviewPath(id, fingerprint, longEdge, rotation, cropTag);
        // Write to a unique temp then atomically swap in: the 8-way analysis loop (and the UI) can
        // request the same preview key concurrently, and a half-written blob would decode as garbage
        // for a reader that sees the file mid-write. The temp is unique so concurrent writers of the
        // same key don't clobber each other's temp; the final Replace is the only contended step.
        var tmp = Path.Combine(_previewDir, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tmp, jpeg);
            Sidecars.AtomicFile.Replace(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp))
                try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ }
        }

        lock (_gate)
        {
            using (var ins = _db.CreateCommand())
            {
                ins.CommandText = "INSERT OR REPLACE INTO previews (path, id, fingerprint) VALUES ($p, $id, $fp)";
                ins.Parameters.AddWithValue("$p", path);
                ins.Parameters.AddWithValue("$id", id);
                ins.Parameters.AddWithValue("$fp", fingerprint);
                ins.ExecuteNonQuery();
            }

            var stale = new List<string>();
            using (var sel = _db.CreateCommand())
            {
                sel.CommandText = "SELECT path FROM previews WHERE id = $id AND fingerprint <> $fp";
                sel.Parameters.AddWithValue("$id", id);
                sel.Parameters.AddWithValue("$fp", fingerprint);
                using var r = sel.ExecuteReader();
                while (r.Read())
                    stale.Add(r.GetString(0));
            }
            foreach (var p in stale)
                try { File.Delete(p); } catch { /* best-effort; the row removal still reclaims tracking */ }
            if (stale.Count > 0)
            {
                using var del = _db.CreateCommand();
                del.CommandText = "DELETE FROM previews WHERE id = $id AND fingerprint <> $fp";
                del.Parameters.AddWithValue("$id", id);
                del.Parameters.AddWithValue("$fp", fingerprint);
                del.ExecuteNonQuery();
            }
        }
        return path;
    }

    private string PreviewPath(string id, string fingerprint, int longEdge, int rotation, string cropTag)
    {
        var key = $"{id}|{fingerprint}|{longEdge}|r{rotation}|c{cropTag}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_previewDir, $"{hash}.jpg");
    }

    private void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _db.Dispose();
    }
}
