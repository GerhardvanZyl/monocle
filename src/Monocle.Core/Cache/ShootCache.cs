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
/// Also stores the rating undo/redo history and Monocle's belief about what each sidecar
/// currently says, so both survive a restart of the app.
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
            CREATE TABLE IF NOT EXISTS rating_history (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                batch INTEGER NOT NULL,
                id TEXT NOT NULL,
                label TEXT NOT NULL,
                state INTEGER NOT NULL,
                beforeJson TEXT NOT NULL,
                afterJson TEXT NOT NULL,
                beforeDiskJson TEXT NOT NULL,
                afterDiskJson TEXT NOT NULL,
                note TEXT,
                ts TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sidecar_state (
                id TEXT NOT NULL,
                fileName TEXT NOT NULL,
                rating INTEGER,
                PRIMARY KEY (id, fileName)
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

    // ---- Rating edit history (undo/redo) + the on-disk beliefs that guard it ------------------
    // Both tables are created with CREATE TABLE IF NOT EXISTS in the constructor, so a cache
    // written by an older build simply gains them (empty) the next time the shoot is opened —
    // the same forward-compatible pattern the metrics/scores/previews tables already use. An
    // empty history means "nothing to undo", never an error.

    private static readonly JsonSerializerOptions HistoryJson = new() { WriteIndented = false };

    /// <summary>Allocate the group id shared by every frame of one bulk action, so a shoot-wide
    /// revert undoes as a single step.</summary>
    public long NextBatchId()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(batch), 0) + 1 FROM rating_history";
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 1L);
        }
    }

    /// <summary>Record an applied edit and truncate the redo branch (any previously undone entry
    /// is unreachable once a new edit lands, exactly as in an editor). Voided entries are kept.</summary>
    public void AppendEdit(RatingEdit edit)
    {
        lock (_gate)
        {
            using (var del = _db.CreateCommand())
            {
                del.CommandText = "DELETE FROM rating_history WHERE state = $undone";
                del.Parameters.AddWithValue("$undone", (int)RatingEditState.Undone);
                del.ExecuteNonQuery();
            }

            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rating_history (batch, id, label, state, beforeJson, afterJson,
                                            beforeDiskJson, afterDiskJson, note, ts)
                VALUES ($b, $id, $l, $s, $bj, $aj, $bd, $ad, $n, $ts);
                """;
            cmd.Parameters.AddWithValue("$b", edit.Batch);
            cmd.Parameters.AddWithValue("$id", edit.ItemId);
            cmd.Parameters.AddWithValue("$l", edit.Label);
            cmd.Parameters.AddWithValue("$s", (int)RatingEditState.Applied);
            cmd.Parameters.AddWithValue("$bj", JsonSerializer.Serialize(edit.Before, HistoryJson));
            cmd.Parameters.AddWithValue("$aj", JsonSerializer.Serialize(edit.After, HistoryJson));
            cmd.Parameters.AddWithValue("$bd", JsonSerializer.Serialize(edit.BeforeDisk, HistoryJson));
            cmd.Parameters.AddWithValue("$ad", JsonSerializer.Serialize(edit.AfterDisk, HistoryJson));
            cmd.Parameters.AddWithValue("$n", (object?)edit.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", edit.TimestampUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The entries a Ctrl+Z would undo: the still-applied frames of the newest batch that
    /// has any, newest frame first. Empty when there is nothing to undo.</summary>
    public IReadOnlyList<RatingEdit> NextUndoBatch() =>
        BatchAt(RatingEditState.Applied, newest: true);

    /// <summary>The entries a Ctrl+Shift+Z would redo: the undone frames of the oldest undone
    /// batch, oldest frame first.</summary>
    public IReadOnlyList<RatingEdit> NextRedoBatch() =>
        BatchAt(RatingEditState.Undone, newest: false);

    private IReadOnlyList<RatingEdit> BatchAt(RatingEditState state, bool newest)
    {
        lock (_gate)
        {
            long batch;
            using (var pick = _db.CreateCommand())
            {
                pick.CommandText = $"SELECT batch FROM rating_history WHERE state = $s ORDER BY seq {(newest ? "DESC" : "ASC")} LIMIT 1";
                pick.Parameters.AddWithValue("$s", (int)state);
                var scalar = pick.ExecuteScalar();
                if (scalar is null || scalar is DBNull)
                    return Array.Empty<RatingEdit>();
                batch = Convert.ToInt64(scalar);
            }

            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT seq, batch, id, label, state, beforeJson, afterJson, beforeDiskJson, afterDiskJson, note, ts
                FROM rating_history WHERE batch = $b AND state = $s ORDER BY seq {(newest ? "DESC" : "ASC")};
                """;
            cmd.Parameters.AddWithValue("$b", batch);
            cmd.Parameters.AddWithValue("$s", (int)state);
            return ReadEdits(cmd);
        }
    }

    /// <summary>Every entry that ever touched a frame, newest first (diagnostics / tests).</summary>
    public IReadOnlyList<RatingEdit> HistoryFor(string id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT seq, batch, id, label, state, beforeJson, afterJson, beforeDiskJson, afterDiskJson, note, ts
                FROM rating_history WHERE id = $id ORDER BY seq DESC;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            return ReadEdits(cmd);
        }
    }

    private static List<RatingEdit> ReadEdits(SqliteCommand cmd)
    {
        var list = new List<RatingEdit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RatingEdit
            {
                Seq = r.GetInt64(0),
                Batch = r.GetInt64(1),
                ItemId = r.GetString(2),
                Label = r.GetString(3),
                State = (RatingEditState)r.GetInt32(4),
                Before = Deserialize<RatingSnapshot>(r.GetString(5)) ?? new RatingSnapshot(),
                After = Deserialize<RatingSnapshot>(r.GetString(6)) ?? new RatingSnapshot(),
                BeforeDisk = ReadDisk(r.GetString(7)),
                AfterDisk = ReadDisk(r.GetString(8)),
                Note = r.IsDBNull(9) ? null : r.GetString(9),
                TimestampUtc = DateTime.TryParse(r.GetString(10), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTime.UtcNow,
            });
        }
        return list;
    }

    private static T? Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, HistoryJson); }
        catch { return default; }
    }

    private static Dictionary<string, SidecarRatingState> ReadDisk(string json)
    {
        var parsed = Deserialize<Dictionary<string, SidecarRatingState>>(json);
        var result = new Dictionary<string, SidecarRatingState>(StringComparer.OrdinalIgnoreCase);
        if (parsed is not null)
            foreach (var (k, v) in parsed)
                result[k] = v;
        return result;
    }

    /// <summary>Move an entry between applied / undone / voided. <paramref name="note"/> records
    /// why an entry was voided; passing null leaves any existing note alone.</summary>
    public void SetEditState(long seq, RatingEditState state, string? note = null)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = note is null
                ? "UPDATE rating_history SET state = $s WHERE seq = $q"
                : "UPDATE rating_history SET state = $s, note = $n WHERE seq = $q";
            cmd.Parameters.AddWithValue("$s", (int)state);
            cmd.Parameters.AddWithValue("$q", seq);
            if (note is not null)
                cmd.Parameters.AddWithValue("$n", note);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>How many entries are currently undoable / redoable (for enabling the commands).</summary>
    public (int Undoable, int Redoable) HistoryCounts()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT
                    SUM(CASE WHEN state = 0 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN state = 1 THEN 1 ELSE 0 END)
                FROM rating_history;
                """;
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return (0, 0);
            return (r.IsDBNull(0) ? 0 : r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1));
        }
    }

    /// <summary>Monocle's belief about each of a frame's sidecars: the rating it observed on disk
    /// after its own last write. Empty when Monocle has never looked.</summary>
    public Dictionary<string, int?> GetSidecarBelief(string id)
    {
        lock (_gate)
        {
            var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT fileName, rating FROM sidecar_state WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetInt32(1);
            return result;
        }
    }

    /// <summary>Replace the belief for a frame with a freshly observed reading. Rows for files that
    /// are no longer part of the frame are dropped so a renamed/removed file can't strand a belief.</summary>
    public void PutSidecarBelief(string id, IReadOnlyDictionary<string, int?> observed)
    {
        lock (_gate)
        {
            using (var del = _db.CreateCommand())
            {
                del.CommandText = "DELETE FROM sidecar_state WHERE id = $id";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
            }
            foreach (var (fileName, rating) in observed)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO sidecar_state (id, fileName, rating) VALUES ($id, $f, $r)";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$f", fileName);
                cmd.Parameters.AddWithValue("$r", (object?)rating ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Bulk form of <see cref="PutSidecarBelief"/> for whole-shoot passes, in one transaction
    /// (a per-row commit costs an fsync each and a large shoot would stall the scan).
    /// With <paramref name="onlyIfMissing"/> the write is skipped for any frame that already has a
    /// belief — that is what makes an external edit made while Monocle was closed still detectable:
    /// re-seeding a frame Monocle has written before would launder the change into the baseline.
    /// </summary>
    public void PutSidecarBeliefs(IReadOnlyList<(string Id, string FileName, int? Rating)> beliefs, bool onlyIfMissing)
    {
        if (beliefs.Count == 0)
            return;

        lock (_gate)
        {
            using var tx = _db.BeginTransaction();

            if (!onlyIfMissing)
                foreach (var id in beliefs.Select(b => b.Id).Distinct(StringComparer.Ordinal))
                {
                    using var del = _db.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM sidecar_state WHERE id = $id";
                    del.Parameters.AddWithValue("$id", id);
                    del.ExecuteNonQuery();
                }

            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = onlyIfMissing
                ? """
                  INSERT INTO sidecar_state (id, fileName, rating)
                  SELECT $id, $f, $r WHERE NOT EXISTS (SELECT 1 FROM sidecar_state WHERE id = $id);
                  """
                : "INSERT OR REPLACE INTO sidecar_state (id, fileName, rating) VALUES ($id, $f, $r)";
            var pId = cmd.Parameters.Add("$id", SqliteType.Text);
            var pFile = cmd.Parameters.Add("$f", SqliteType.Text);
            var pRating = cmd.Parameters.Add("$r", SqliteType.Integer);
            foreach (var (id, fileName, rating) in beliefs)
            {
                pId.Value = id;
                pFile.Value = fileName;
                pRating.Value = (object?)rating ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
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
