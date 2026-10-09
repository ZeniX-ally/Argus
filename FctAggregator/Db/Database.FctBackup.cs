using Microsoft.Data.Sqlite;

namespace FctAggregator;

public sealed partial class Database
{
    private void EnsureFctProgramBackupTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS fct_program_backups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                zip_path TEXT NOT NULL,
                source_root TEXT,
                zip_bytes INTEGER NOT NULL DEFAULT 0,
                file_count INTEGER NOT NULL DEFAULT 0,
                zip_sha256 TEXT,
                station_id TEXT,
                trigger TEXT,
                status TEXT NOT NULL DEFAULT 'ok',
                note TEXT,
                zip_exists INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS idx_fpb_created ON fct_program_backups(created_at);
            CREATE INDEX IF NOT EXISTS idx_fpb_status ON fct_program_backups(status);
        ";
        cmd.ExecuteNonQuery();
    }

    public long InsertFctProgramBackup(FctProgramBackupRecord r)
    {
        using var conn = Open();
        EnsureFctProgramBackupTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO fct_program_backups
                (created_at, zip_path, source_root, zip_bytes, file_count, zip_sha256,
                 station_id, trigger, status, note, zip_exists)
            VALUES (@t,@z,@s,@b,@c,@h,@st,@tr,@stt,@n,@ex)";
        cmd.Parameters.AddWithValue("@t", r.CreatedAt);
        cmd.Parameters.AddWithValue("@z", r.ZipPath);
        cmd.Parameters.AddWithValue("@s", r.SourceRoot ?? "");
        cmd.Parameters.AddWithValue("@b", r.ZipBytes);
        cmd.Parameters.AddWithValue("@c", r.FileCount);
        cmd.Parameters.AddWithValue("@h", r.ZipSha256 ?? "");
        cmd.Parameters.AddWithValue("@st", r.StationId ?? "");
        cmd.Parameters.AddWithValue("@tr", r.Trigger ?? "");
        cmd.Parameters.AddWithValue("@stt", r.Status ?? "ok");
        cmd.Parameters.AddWithValue("@n", (object?)r.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ex", r.ZipExists ? 1 : 0);
        cmd.ExecuteNonQuery();
        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(idCmd.ExecuteScalar());
    }

    public List<FctProgramBackupRecord> ListFctProgramBackups(int limit = 50)
    {
        var list = new List<FctProgramBackupRecord>();
        using var conn = Open();
        EnsureFctProgramBackupTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, created_at, zip_path, COALESCE(source_root,''), zip_bytes, file_count,
                   COALESCE(zip_sha256,''), COALESCE(station_id,''), COALESCE(trigger,''),
                   status, note, zip_exists
            FROM fct_program_backups
            ORDER BY id DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", Math.Max(1, limit));
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new FctProgramBackupRecord
            {
                Id = rd.GetInt64(0),
                CreatedAt = rd.GetString(1),
                ZipPath = rd.GetString(2),
                SourceRoot = rd.GetString(3),
                ZipBytes = rd.GetInt64(4),
                FileCount = rd.GetInt32(5),
                ZipSha256 = rd.GetString(6),
                StationId = rd.GetString(7),
                Trigger = rd.GetString(8),
                Status = rd.GetString(9),
                Note = rd.IsDBNull(10) ? null : rd.GetString(10),
                ZipExists = rd.GetInt64(11) != 0,
            });
        }
        return list;
    }

    public FctProgramBackupRecord? GetLatestFctProgramBackupOk()
    {
        using var conn = Open();
        EnsureFctProgramBackupTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, created_at, zip_path, COALESCE(source_root,''), zip_bytes, file_count,
                   COALESCE(zip_sha256,''), COALESCE(station_id,''), COALESCE(trigger,''),
                   status, note, zip_exists
            FROM fct_program_backups WHERE status='ok'
            ORDER BY id DESC LIMIT 1";
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        return new FctProgramBackupRecord
        {
            Id = rd.GetInt64(0),
            CreatedAt = rd.GetString(1),
            ZipPath = rd.GetString(2),
            SourceRoot = rd.GetString(3),
            ZipBytes = rd.GetInt64(4),
            FileCount = rd.GetInt32(5),
            ZipSha256 = rd.GetString(6),
            StationId = rd.GetString(7),
            Trigger = rd.GetString(8),
            Status = rd.GetString(9),
            Note = rd.IsDBNull(10) ? null : rd.GetString(10),
            ZipExists = rd.GetInt64(11) != 0,
        };
    }

    public void MarkFctProgramBackupZipDeleted(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE fct_program_backups SET zip_exists=0 WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }
}
