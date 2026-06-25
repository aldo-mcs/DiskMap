using Microsoft.Data.Sqlite;
using DiskMap.Core.Scanning;

namespace DiskMap.Core.Snapshots;

public sealed record SnapshotInfo(long Id, string Root, DateTime TakenUtc, long TotalSize, long TotalFiles);

/// <summary>
/// Persists scans to a local SQLite database under %LOCALAPPDATA%\DiskMap so the app can
/// answer "what changed since last time?". Only directory-level aggregates are stored
/// (bounded by folder count) — that is all the history/diff view needs.
/// </summary>
public sealed class SnapshotStore
{
    /// <summary>How many snapshots to keep per scanned root. Bounds the database's growth —
    /// without this, a directory-level row gets written per folder on every single scan, which
    /// for a whole-drive scan repeated a handful of times adds up to hundreds of MB.</summary>
    private const int RetentionPerRoot = 5;

    private readonly string _connectionString;

    public SnapshotStore(string? databasePath = null)
    {
        databasePath ??= DefaultDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        Initialize();
    }

    public static string DefaultDatabasePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskMap", "snapshots.db");

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                root TEXT NOT NULL,
                taken_utc TEXT NOT NULL,
                total_size INTEGER NOT NULL,
                total_files INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS entries (
                snapshot_id INTEGER NOT NULL,
                path TEXT NOT NULL,
                size INTEGER NOT NULL,
                file_count INTEGER NOT NULL,
                FOREIGN KEY(snapshot_id) REFERENCES snapshots(id));
            CREATE INDEX IF NOT EXISTS ix_entries_snapshot ON entries(snapshot_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public long Save(FileSystemNode root)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        long snapshotId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO snapshots (root, taken_utc, total_size, total_files) VALUES ($root, $taken, $size, $files); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$root", root.Path);
            cmd.Parameters.AddWithValue("$taken", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$size", root.Size);
            cmd.Parameters.AddWithValue("$files", root.FileCount);
            snapshotId = (long)cmd.ExecuteScalar()!;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO entries (snapshot_id, path, size, file_count) VALUES ($sid, $path, $size, $files)";
            var pId = cmd.Parameters.AddWithValue("$sid", snapshotId);
            var pPath = cmd.CreateParameter(); pPath.ParameterName = "$path"; cmd.Parameters.Add(pPath);
            var pSize = cmd.CreateParameter(); pSize.ParameterName = "$size"; cmd.Parameters.Add(pSize);
            var pFiles = cmd.CreateParameter(); pFiles.ParameterName = "$files"; cmd.Parameters.Add(pFiles);

            foreach (var dir in EnumerateDirectories(root))
            {
                pPath.Value = dir.Path;
                pSize.Value = dir.Size;
                pFiles.Value = dir.FileCount;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();

        PruneOldSnapshots(conn, root.Path);
        return snapshotId;
    }

    /// <summary>Deletes snapshots beyond <see cref="RetentionPerRoot"/> for this root, then VACUUMs
    /// to actually reclaim the freed pages — SQLite doesn't shrink the file on DELETE alone.</summary>
    private static void PruneOldSnapshots(SqliteConnection conn, string root)
    {
        long deleted;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM entries WHERE snapshot_id IN (
                    SELECT id FROM snapshots WHERE root = $root
                    ORDER BY taken_utc DESC LIMIT -1 OFFSET $keep);
                DELETE FROM snapshots WHERE root = $root
                    AND id NOT IN (SELECT id FROM snapshots WHERE root = $root ORDER BY taken_utc DESC LIMIT $keep);
                """;
            cmd.Parameters.AddWithValue("$root", root);
            cmd.Parameters.AddWithValue("$keep", RetentionPerRoot);
            deleted = cmd.ExecuteNonQuery();
        }

        if (deleted > 0)
        {
            using var vacuum = conn.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
        }
    }

    public List<SnapshotInfo> List(string? root = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = root is null
            ? "SELECT id, root, taken_utc, total_size, total_files FROM snapshots ORDER BY taken_utc DESC"
            : "SELECT id, root, taken_utc, total_size, total_files FROM snapshots WHERE root = $root ORDER BY taken_utc DESC";
        if (root is not null) cmd.Parameters.AddWithValue("$root", root);

        var result = new List<SnapshotInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SnapshotInfo(
                reader.GetInt64(0), reader.GetString(1),
                DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetInt64(3), reader.GetInt64(4)));
        }
        return result;
    }

    public Dictionary<string, long> LoadEntrySizes(long snapshotId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, size FROM entries WHERE snapshot_id = $sid";
        cmd.Parameters.AddWithValue("$sid", snapshotId);
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = reader.GetInt64(1);
        return map;
    }

    public void Delete(long snapshotId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE snapshot_id = $sid; DELETE FROM snapshots WHERE id = $sid;";
        cmd.Parameters.AddWithValue("$sid", snapshotId);
        cmd.ExecuteNonQuery();
    }

    private static IEnumerable<FileSystemNode> EnumerateDirectories(FileSystemNode node)
    {
        if (!node.IsDirectory) yield break;
        yield return node;
        foreach (var child in node.Children)
            if (child.IsDirectory)
                foreach (var d in EnumerateDirectories(child))
                    yield return d;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
