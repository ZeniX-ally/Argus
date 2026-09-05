using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FctAggregator;

public partial class AggDatabase
{
    public sealed class ProcLogEntry
    {
        public long Id;
        public string Version = "";
        public string ChangedAt = "";
        public string ChangedBy = "";
        public string Content = "";
        public string ScopeMachines = "";
        public string ParamsSnapshot = "";
        public string RelatedReports = "";
        public string CreatedAt = "";
    }

    public sealed class ReportArchiveEntry
    {
        public long Id;
        public string Machine = "";
        public string Sn = "";
        public string Model = "";
        public string TestDate = "";
        public string Result = "";
        public string FailReason = "";
        public string XmlPath = "";
        public string ArchivedPath = "";
        public string ArchivedAt = "";
        public string ArchivedBy = "";
        public string Note = "";
        public string SummaryJson = "";
    }

    public sealed class ProcDiffResult
    {
        public List<string> Added = new();
        public List<string> Removed = new();
        public List<(string Key, string Before, string After)> Changed = new();
        public List<string> Unchanged = new();
    }

    public long CreateProcLog(ProcLogEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.Version)) throw new ArgumentException("version required");
        if (string.IsNullOrWhiteSpace(e.ChangedAt)) e.ChangedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        if (!string.IsNullOrWhiteSpace(e.ScopeMachines)) e.ScopeMachines = NormalizeJson(e.ScopeMachines);
        if (!string.IsNullOrWhiteSpace(e.ParamsSnapshot)) e.ParamsSnapshot = NormalizeJson(e.ParamsSnapshot);
        if (!string.IsNullOrWhiteSpace(e.RelatedReports)) e.RelatedReports = NormalizeJson(e.RelatedReports);
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"INSERT INTO proc_change_log (version, changed_at, changed_by, content, scope_machines, params_snapshot, related_reports, created_at)
                VALUES (@v,@at,@by,@content,@scope,@params,@reports, datetime('now','localtime')); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@v", e.Version.Trim());
            cmd.Parameters.AddWithValue("@at", e.ChangedAt);
            cmd.Parameters.AddWithValue("@by", (object?)e.ChangedBy ?? "");
            cmd.Parameters.AddWithValue("@content", (object?)e.Content ?? "");
            cmd.Parameters.AddWithValue("@scope", (object?)e.ScopeMachines ?? "");
            cmd.Parameters.AddWithValue("@params", (object?)e.ParamsSnapshot ?? "");
            cmd.Parameters.AddWithValue("@reports", (object?)e.RelatedReports ?? "");
            var id = Convert.ToInt64(cmd.ExecuteScalar());
            e.Id = id;
            return id;
        }
    }

    private static string NormalizeJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch { return json; }
    }

    public ProcLogEntry? GetProcLog(long id)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, version, changed_at, changed_by, content, scope_machines, params_snapshot, related_reports, created_at FROM proc_change_log WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadProcLog(r);
    }

    public List<ProcLogEntry> ListProcLogs(string? machine = null, string? version = null, int limit = 100, int offset = 0, string? fromAt = null, string? toAt = null)
    {
        limit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 1000);
        offset = Math.Max(0, offset);
        var sql = "SELECT id, version, changed_at, changed_by, content, scope_machines, params_snapshot, related_reports, created_at FROM proc_change_log";
        var conds = new List<string>();
        var ps = new List<(string n, object v)>();
        if (!string.IsNullOrWhiteSpace(version)) { conds.Add("version = @v COLLATE NOCASE"); ps.Add(("@v", version!.Trim())); }
        if (!string.IsNullOrWhiteSpace(fromAt)) { conds.Add("changed_at >= @from"); ps.Add(("@from", fromAt!)); }
        if (!string.IsNullOrWhiteSpace(toAt)) { conds.Add("changed_at <= @to"); ps.Add(("@to", toAt!)); }
        bool filterMachine = !string.IsNullOrWhiteSpace(machine);
        if (filterMachine) { conds.Add("scope_machines LIKE @m"); ps.Add(("@m", $"%{machine!.Trim()}%")); }
        if (conds.Count > 0) sql += " WHERE " + string.Join(" AND ", conds);
        sql += " ORDER BY changed_at DESC, id DESC LIMIT @lim OFFSET @off";
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.Parameters.AddWithValue("@lim", limit);
        cmd.Parameters.AddWithValue("@off", offset);
        var list = new List<ProcLogEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var e = ReadProcLog(r);
            if (filterMachine)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(e.ScopeMachines))
                    {
                        using var doc = JsonDocument.Parse(e.ScopeMachines);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            bool hit = false;
                            foreach (var el in doc.RootElement.EnumerateArray())
                                if (string.Equals(el.GetString()?.Trim(), machine!.Trim(), StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                            if (!hit) continue;
                        }
                    }
                }
                catch {  }
            }
            list.Add(e);
        }
        return list;
    }

    public List<ProcLogEntry> QueryProcTimeline(string? machine = null, int limit = 50, int offset = 0)
        => ListProcLogs(machine, null, limit, offset);

    public bool DeleteProcLog(long id)
    {
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM proc_change_log WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public ProcDiffResult DiffProcParams(long id1, long id2)
    {
        var a = GetProcLog(id1) ?? throw new InvalidOperationException($"proc_log {id1} not found");
        var b = GetProcLog(id2) ?? throw new InvalidOperationException($"proc_log {id2} not found");
        return DiffJsonObjects(a.ParamsSnapshot, b.ParamsSnapshot);
    }

    public static ProcDiffResult DiffJsonObjects(string? beforeJson, string? afterJson)
    {
        var res = new ProcDiffResult();
        var before = ParseFlat(beforeJson);
        var after = ParseFlat(afterJson);
        foreach (var kv in after)
        {
            if (!before.TryGetValue(kv.Key, out var bv)) res.Added.Add(kv.Key);
            else if (bv != kv.Value) res.Changed.Add((kv.Key, bv, kv.Value));
            else res.Unchanged.Add(kv.Key);
        }
        foreach (var kv in before)
            if (!after.ContainsKey(kv.Key)) res.Removed.Add(kv.Key);
        return res;
    }

    private static Dictionary<string, string> ParseFlat(string? json)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return dict;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in root.EnumerateObject())
                {
                    string val;
                    if (p.Value.ValueKind == JsonValueKind.String) val = p.Value.GetString() ?? "";
                    else val = p.Value.GetRawText();
                    dict[p.Name] = val;
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var el in root.EnumerateArray())
                    dict[$"[{i++}]"] = el.GetRawText();
            }
        }
        catch { }
        return dict;
    }

    private static ProcLogEntry ReadProcLog(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Version = r.IsDBNull(1) ? "" : r.GetString(1),
        ChangedAt = r.IsDBNull(2) ? "" : r.GetString(2),
        ChangedBy = r.IsDBNull(3) ? "" : r.GetString(3),
        Content = r.IsDBNull(4) ? "" : r.GetString(4),
        ScopeMachines = r.IsDBNull(5) ? "" : r.GetString(5),
        ParamsSnapshot = r.IsDBNull(6) ? "" : r.GetString(6),
        RelatedReports = r.IsDBNull(7) ? "" : r.GetString(7),
        CreatedAt = r.IsDBNull(8) ? "" : r.GetString(8),
    };

    public long ArchiveReport(ReportArchiveEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.XmlPath) && string.IsNullOrWhiteSpace(e.Sn)) throw new ArgumentException("xml_path or sn required");
        if (string.IsNullOrWhiteSpace(e.ArchivedAt)) e.ArchivedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"INSERT INTO report_archive (machine, sn, model, test_date, result, xml_path, archived_path, archived_at, archived_by, note, summary_json)
                VALUES (@m,@sn,@model,@date,@result,@xml,@arch,@at,@by,@note,@summary); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@m", (object?)e.Machine ?? "");
            cmd.Parameters.AddWithValue("@sn", (object?)e.Sn ?? "");
            cmd.Parameters.AddWithValue("@model", (object?)e.Model ?? "");
            cmd.Parameters.AddWithValue("@date", (object?)e.TestDate ?? "");
            cmd.Parameters.AddWithValue("@result", (object?)e.Result ?? "");
            cmd.Parameters.AddWithValue("@xml", (object?)e.XmlPath ?? "");
            cmd.Parameters.AddWithValue("@arch", (object?)e.ArchivedPath ?? "");
            cmd.Parameters.AddWithValue("@at", e.ArchivedAt);
            cmd.Parameters.AddWithValue("@by", (object?)e.ArchivedBy ?? "");
            cmd.Parameters.AddWithValue("@note", (object?)e.Note ?? "");
            cmd.Parameters.AddWithValue("@summary", (object?)e.SummaryJson ?? "");
            var id = Convert.ToInt64(cmd.ExecuteScalar());
            e.Id = id;
            return id;
        }
    }

    public List<ReportArchiveEntry> ListReportArchives(string? machine = null, int limit = 100, int offset = 0)
    {
        limit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 1000);
        offset = Math.Max(0, offset);
        var sql = "SELECT id, machine, sn, model, test_date, result, xml_path, archived_path, archived_at, archived_by, note, summary_json FROM report_archive";
        var conds = new List<string>();
        var ps = new List<(string n, object v)>();
        if (!string.IsNullOrWhiteSpace(machine)) { conds.Add("machine = @m COLLATE NOCASE"); ps.Add(("@m", machine!.Trim())); }
        if (conds.Count > 0) sql += " WHERE " + string.Join(" AND ", conds);
        sql += " ORDER BY archived_at DESC, id DESC LIMIT @lim OFFSET @off";
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.Parameters.AddWithValue("@lim", limit);
        cmd.Parameters.AddWithValue("@off", offset);
        var list = new List<ReportArchiveEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadArchive(r));
        return list;
    }

    public ReportArchiveEntry? GetReportArchive(long id)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, machine, sn, model, test_date, result, xml_path, archived_path, archived_at, archived_by, note, summary_json FROM report_archive WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadArchive(r) : null;
    }

    public bool DeleteReportArchive(long id)
    {
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM report_archive WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private static ReportArchiveEntry ReadArchive(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Machine = r.IsDBNull(1) ? "" : r.GetString(1),
        Sn = r.IsDBNull(2) ? "" : r.GetString(2),
        Model = r.IsDBNull(3) ? "" : r.GetString(3),
        TestDate = r.IsDBNull(4) ? "" : r.GetString(4),
        Result = r.IsDBNull(5) ? "" : r.GetString(5),
        XmlPath = r.IsDBNull(6) ? "" : r.GetString(6),
        ArchivedPath = r.IsDBNull(7) ? "" : r.GetString(7),
        ArchivedAt = r.IsDBNull(8) ? "" : r.GetString(8),
        ArchivedBy = r.IsDBNull(9) ? "" : r.GetString(9),
        Note = r.IsDBNull(10) ? "" : r.GetString(10),
        SummaryJson = r.IsDBNull(11) ? "" : r.GetString(11),
    };
}
