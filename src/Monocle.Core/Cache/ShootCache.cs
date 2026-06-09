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
        Exec("""
            CREATE TABLE IF NOT EXISTS items (
                id TEXT PRIMARY KEY,
                fingerprint TEXT NOT NULL,
                metrics TEXT,
                exif TEXT
            );
            """);
    }

    public bool TryGetAnalysis(string id, string fingerprint, out TechnicalMetrics? metrics, out ExifInfo? exif)
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

    public void PutAnalysis(string id, string fingerprint, TechnicalMetrics metrics, ExifInfo exif)
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

    /// <summary>Path to a cached preview at the given size + rotation + crop, or null on a miss.</summary>
    public string? GetPreviewPath(string id, string fingerprint, int longEdge, int rotation = 0, string cropTag = "")
    {
        var path = PreviewPath(id, fingerprint, longEdge, rotation, cropTag);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Write a preview JPEG to the cache and return its path.</summary>
    public string PutPreview(string id, string fingerprint, int longEdge, int rotation, byte[] jpeg, string cropTag = "")
    {
        var path = PreviewPath(id, fingerprint, longEdge, rotation, cropTag);
        File.WriteAllBytes(path, jpeg);
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
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
