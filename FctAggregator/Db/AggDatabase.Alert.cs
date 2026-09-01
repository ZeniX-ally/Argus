using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>
/// 聚合库告警历史扩展（P8 告警规则中心，Lite-Infra，2026-08-28）。
/// 统一存储 yield/disk/cpu/offline 告警历史，单写者 WAL，走既有 _writeLock；读短租连接。
/// 规则配置本身走 Config.cs 的阈值（device_alert_* + yield_alert_*），历史落库供 /api/alerts/history 查询与前端告警页展示。
/// </summary>
public partial class AggDatabase
{
    /// <summary>一条告警历史（落库时即时写入，前端按时间倒序查看）。</summary>
    public sealed class AlertHistoryRow
    {
        public long Id;
        public string Ts = "";
        public string Machine = "";
        public string Rule = "";      // yield / disk / cpu / offline
        public string Level = "";     // warn / critical（当前用 warn）
        public string Metric = "";    // 如 "yield 85.2% < 90%" / "disk 5.3GB < 10GB"
        public string Detail = "";
    }

    /// <summary>写入一条告警历史（内部调用，异常静默不影响告警推送）。</summary>
    public long InsertAlertHistory(string machine, string rule, string metric, string detail = "", string level = "warn")
    {
        if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(rule)) return 0;
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock (_writeLock)
        {
            try
            {
                Open();
                using var cmd = _conn!.CreateCommand();
                cmd.CommandText = @"INSERT INTO alert_history (ts, machine, rule, level, metric, detail) VALUES (@ts,@m,@r,@lv,@metric,@detail); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@ts", ts);
                cmd.Parameters.AddWithValue("@m", machine);
                cmd.Parameters.AddWithValue("@r", rule);
                cmd.Parameters.AddWithValue("@lv", level);
                cmd.Parameters.AddWithValue("@metric", metric ?? "");
                cmd.Parameters.AddWithValue("@detail", detail ?? "");
                var id = Convert.ToInt64(cmd.ExecuteScalar());
                return id;
            }
            catch (Exception ex) { Logger.Warning($"[告警历史] 写入失败 machine={machine} rule={rule}: {ex.Message}"); return 0; }
        }
    }

    /// <summary>查询告警历史（viewer 可读）：按时间倒序，支持 machine/rule 过滤，分页。</summary>
    public List<AlertHistoryRow> ListAlertHistory(string? machine = null, string? rule = null, int limit = 100, int offset = 0)
    {
        limit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 1000);
        offset = Math.Max(0, offset);
        var sql = "SELECT id, ts, machine, rule, level, metric, detail FROM alert_history";
        var conds = new List<string>();
        var ps = new List<(string n, object v)>();
        if (!string.IsNullOrWhiteSpace(machine)) { conds.Add("machine = @m COLLATE NOCASE"); ps.Add(("@m", machine!.Trim())); }
        if (!string.IsNullOrWhiteSpace(rule)) { conds.Add("rule = @r COLLATE NOCASE"); ps.Add(("@r", rule!.Trim())); }
        if (conds.Count > 0) sql += " WHERE " + string.Join(" AND ", conds);
        sql += " ORDER BY id DESC LIMIT @lim OFFSET @off";
        var list = new List<AlertHistoryRow>();
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.Parameters.AddWithValue("@lim", limit);
        cmd.Parameters.AddWithValue("@off", offset);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AlertHistoryRow
            {
                Id = r.GetInt64(0),
                Ts = r.IsDBNull(1) ? "" : r.GetString(1),
                Machine = r.IsDBNull(2) ? "" : r.GetString(2),
                Rule = r.IsDBNull(3) ? "" : r.GetString(3),
                Level = r.IsDBNull(4) ? "" : r.GetString(4),
                Metric = r.IsDBNull(5) ? "" : r.GetString(5),
                Detail = r.IsDBNull(6) ? "" : r.GetString(6),
            });
        }
        return list;
    }

    /// <summary>告警历史总数（按过滤条件）。</summary>
    public long CountAlertHistory(string? machine = null, string? rule = null)
    {
        var sql = "SELECT COUNT(*) FROM alert_history";
        var conds = new List<string>();
        var ps = new List<(string n, object v)>();
        if (!string.IsNullOrWhiteSpace(machine)) { conds.Add("machine = @m COLLATE NOCASE"); ps.Add(("@m", machine!.Trim())); }
        if (!string.IsNullOrWhiteSpace(rule)) { conds.Add("rule = @r COLLATE NOCASE"); ps.Add(("@r", rule!.Trim())); }
        if (conds.Count > 0) sql += " WHERE " + string.Join(" AND ", conds);
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>清理过期告警历史（> retainDays 天）。返回删除行数。</summary>
    public int PurgeOldAlerts(int retainDays)
    {
        if (retainDays <= 0) retainDays = 30;
        var cutoff = DateTime.Now.AddDays(-retainDays).ToString("yyyy-MM-dd HH:mm:ss");
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM alert_history WHERE ts < @cut";
            cmd.Parameters.AddWithValue("@cut", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }
}
