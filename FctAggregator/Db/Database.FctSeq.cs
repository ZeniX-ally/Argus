using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FctAggregator;

public sealed partial class Database
{
    private void EnsureFctSeqChangeTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS fct_seq_change_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                detected_at TEXT NOT NULL,
                seq_root TEXT,
                added INTEGER NOT NULL DEFAULT 0,
                removed INTEGER NOT NULL DEFAULT 0,
                modified INTEGER NOT NULL DEFAULT 0,
                fingerprint TEXT,
                summary_json TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_fseq_detected ON fct_seq_change_log(detected_at);
        ";
        cmd.ExecuteNonQuery();
    }

    public long InsertFctSeqChangeLog(SeqChangeReport report)
    {
        using var conn = Open();
        EnsureFctSeqChangeTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO fct_seq_change_log
                (detected_at, seq_root, added, removed, modified, fingerprint, summary_json)
            VALUES (@t,@r,@a,@d,@m,@f,@j)";
        cmd.Parameters.AddWithValue("@t", report.DetectedAt);
        cmd.Parameters.AddWithValue("@r", report.SeqRoot);
        cmd.Parameters.AddWithValue("@a", report.Added);
        cmd.Parameters.AddWithValue("@d", report.Removed);
        cmd.Parameters.AddWithValue("@m", report.Modified);
        cmd.Parameters.AddWithValue("@f", report.Fingerprint());
        cmd.Parameters.AddWithValue("@j", JsonSerializer.Serialize(report.Changes.Select(c => new
        {
            kind = c.Kind.ToString(),
            path = c.RelativePath,
            old_size = c.OldSize,
            new_size = c.NewSize,
        })));
        cmd.ExecuteNonQuery();
        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(idCmd.ExecuteScalar());
    }

    public List<(long Id, string At, int Added, int Removed, int Modified, string Root)> ListFctSeqChangeLogs(int limit = 30)
    {
        var list = new List<(long, string, int, int, int, string)>();
        using var conn = Open();
        EnsureFctSeqChangeTable(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, detected_at, added, removed, modified, COALESCE(seq_root,'')
            FROM fct_seq_change_log ORDER BY id DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", Math.Max(1, limit));
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add((rd.GetInt64(0), rd.GetString(1), rd.GetInt32(2), rd.GetInt32(3), rd.GetInt32(4), rd.GetString(5)));
        return list;
    }
}
