using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>
/// 预测准确率对账表（v3.20.0 规格 02）的读写方法。
/// Schema 见 <see cref="DbMigrator"/> v10 迁移。
///
/// 写路径：单写者 WAL + _writeLock（与 AggDatabase 主文件其他写一致）；
/// 读路径：短租连接（OpenReader），对账批量读 + /api/predict/accuracy 高频调用不阻塞写。
/// </summary>
public partial class AggDatabase
{
    /// <summary>一条对账记录（predict_accuracy_log 一行）。</summary>
    public sealed class AccuracyRow
    {
        public long Id;
        public string Rule = "";           // yield/cpu/disk/offline
        public string Machine = "";
        public long PredictId;             // alert_predict_log.id 或 device_predict_log.id
        public string PredictTable = "";   // 'alert' | 'device'
        public double? PredictedValue;
        public double? ActualValue;
        public double? Threshold;
        public bool Hit;
        public double? LeadDays;
        public string PredictedAt = "";    // 原预测时间 (yyyy-MM-dd HH:mm:ss)
        public string ReconciledAt = "";   // 对账时间
        public string Note = "";
    }

    /// <summary>
    /// UPSERT 一条对账（(predict_table, predict_id) UNIQUE）：同一预测二次对账直接替换，
    /// 避免重复 key 异常阻塞批量对账循环。
    /// </summary>
    public void UpsertPredictAccuracy(AccuracyRow r)
    {
        if (string.IsNullOrWhiteSpace(r.Machine) || string.IsNullOrWhiteSpace(r.Rule)
            || string.IsNullOrWhiteSpace(r.PredictTable)) return;
        if (string.IsNullOrEmpty(r.ReconciledAt)) r.ReconciledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO predict_accuracy_log
                  (rule, machine, predict_id, predict_table, predicted_value, actual_value,
                   threshold, hit, lead_days, predicted_at, reconciled_at, note)
                VALUES (@rule,@machine,@pid,@ptable,@pred,@act,@thr,@hit,@lead,@predAt,@recAt,@note)
                ON CONFLICT(predict_table, predict_id) DO UPDATE SET
                  rule=excluded.rule,
                  machine=excluded.machine,
                  predicted_value=excluded.predicted_value,
                  actual_value=excluded.actual_value,
                  threshold=excluded.threshold,
                  hit=excluded.hit,
                  lead_days=excluded.lead_days,
                  predicted_at=excluded.predicted_at,
                  reconciled_at=excluded.reconciled_at,
                  note=excluded.note";
            cmd.Parameters.AddWithValue("@rule", r.Rule);
            cmd.Parameters.AddWithValue("@machine", r.Machine);
            cmd.Parameters.AddWithValue("@pid", r.PredictId);
            cmd.Parameters.AddWithValue("@ptable", r.PredictTable);
            cmd.Parameters.AddWithValue("@pred", (object?)r.PredictedValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@act", (object?)r.ActualValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@thr", (object?)r.Threshold ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hit", r.Hit ? 1 : 0);
            cmd.Parameters.AddWithValue("@lead", (object?)r.LeadDays ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@predAt", r.PredictedAt);
            cmd.Parameters.AddWithValue("@recAt", r.ReconciledAt);
            cmd.Parameters.AddWithValue("@note", (object?)r.Note ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 查询对账记录：rule / machine 可空过滤；days 回溯 reconciled_at >= now - days；
    /// limit 上限 5000 防单次拉爆。/api/predict/accuracy 与 PredictAccuracyReconciler 都走这里。
    /// </summary>
    public List<AccuracyRow> QueryPredictAccuracy(string? rule = null, string? machine = null, int days = 30, int limit = 5000)
    {
        var list = new List<AccuracyRow>();
        if (days <= 0) days = 30;
        limit = Math.Min(Math.Max(limit, 1), 5000);
        var since = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");

        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        var sql = @"SELECT id, rule, machine, predict_id, predict_table,
                           predicted_value, actual_value, threshold, hit, lead_days,
                           predicted_at, reconciled_at, note
                      FROM predict_accuracy_log
                     WHERE reconciled_at >= @since";
        if (!string.IsNullOrEmpty(rule)) sql += " AND rule = @rule";
        if (!string.IsNullOrEmpty(machine)) sql += " AND machine = @machine";
        sql += " ORDER BY reconciled_at DESC, id DESC LIMIT @lim";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@since", since);
        if (!string.IsNullOrEmpty(rule)) cmd.Parameters.AddWithValue("@rule", rule);
        if (!string.IsNullOrEmpty(machine)) cmd.Parameters.AddWithValue("@machine", machine);
        cmd.Parameters.AddWithValue("@lim", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadAccuracyRow(reader));
        }
        return list;
    }

    /// <summary>
    /// 统计某规则的命中率（自检 + /api/predict/accuracy summary 用）：
    /// 返回 (总数, hit 数, 平均 lead_days)。无记录返回 (0, 0, 0)。
    /// </summary>
    public (int Total, int Hit, double AvgLeadDays) CountPredictAccuracyByRule(string rule, int days = 30)
    {
        if (string.IsNullOrEmpty(rule)) return (0, 0, 0);
        if (days <= 0) days = 30;
        var since = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");

        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*),
                                   SUM(CASE WHEN hit=1 THEN 1 ELSE 0 END),
                                   AVG(lead_days)
                              FROM predict_accuracy_log
                             WHERE rule = @rule AND reconciled_at >= @since";
        cmd.Parameters.AddWithValue("@rule", rule);
        cmd.Parameters.AddWithValue("@since", since);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0, 0);
        var total = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0));
        var hit = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1));
        var lead = reader.IsDBNull(2) ? 0.0 : reader.GetDouble(2);
        return (total, hit, lead);
    }

    /// <summary>
    /// 某 (predict_table, predict_id) 是否已对账：用于去重，跳过重复对账（每小时可能多次触发对账）。
    /// </summary>
    public bool PredictAccuracyExists(string predictTable, long predictId)
    {
        if (string.IsNullOrEmpty(predictTable) || predictId <= 0) return false;
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM predict_accuracy_log WHERE predict_table=@t AND predict_id=@id LIMIT 1";
        cmd.Parameters.AddWithValue("@t", predictTable);
        cmd.Parameters.AddWithValue("@id", predictId);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// 滚动清理：删除早于 retainDays 天的对账记录。DbMaintenance.RunNow 每日调用。
    /// 返回删除行数。
    /// </summary>
    public int PurgeOldPredictAccuracy(int retainDays)
    {
        if (retainDays <= 0) retainDays = 180;
        var cutoff = DateTime.Now.AddDays(-retainDays).ToString("yyyy-MM-dd HH:mm:ss");
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM predict_accuracy_log WHERE reconciled_at < @cut";
            cmd.Parameters.AddWithValue("@cut", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    private static AccuracyRow ReadAccuracyRow(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Rule = r.IsDBNull(1) ? "" : r.GetString(1),
        Machine = r.IsDBNull(2) ? "" : r.GetString(2),
        PredictId = r.GetInt64(3),
        PredictTable = r.IsDBNull(4) ? "" : r.GetString(4),
        PredictedValue = r.IsDBNull(5) ? null : r.GetDouble(5),
        ActualValue = r.IsDBNull(6) ? null : r.GetDouble(6),
        Threshold = r.IsDBNull(7) ? null : r.GetDouble(7),
        Hit = !r.IsDBNull(8) && r.GetInt64(8) != 0,
        LeadDays = r.IsDBNull(9) ? null : r.GetDouble(9),
        PredictedAt = r.IsDBNull(10) ? "" : r.GetString(10),
        ReconciledAt = r.IsDBNull(11) ? "" : r.GetString(11),
        Note = r.IsDBNull(12) ? "" : r.GetString(12),
    };
}
