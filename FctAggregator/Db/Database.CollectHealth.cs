using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>窗口内解析失败按 error_code 聚合的一行（含一条样例路径，供告警卡直接定位文件）。</summary>
public sealed class CollectFailureRow
{
    public string ErrorCode { get; init; } = "";
    public int Count { get; init; }
    public string SamplePath { get; init; } = "";
    public string SkipReason { get; init; } = "";
}

/// <summary>单条解析失败明细（告警卡「最近失败文件」列表用）。</summary>
public sealed class CollectFailureItem
{
    public string XmlPath { get; init; } = "";
    public string ErrorCode { get; init; } = "";
    public string SkipReason { get; init; } = "";
    public string CreatedAt { get; init; } = "";
}

/// <summary>
/// 采集健康度查询（parse_failure_log）。采集异常/漏采告警卡的数据源——
/// 该表自审计 A6/ALGO-2 起就在写，但此前只进不出（没有任何读取入口），
/// 现场漏采只能靠翻日志发现。
/// </summary>
public sealed partial class Database
{
    /// <summary>正常跳过（debug 文件 / 路径不匹配规则）的 error_code，不计入「异常」。</summary>
    public const string SkipErrorCode = "skip";

    /// <summary>窗口内解析失败按 error_code 聚合，按条数倒序。sinceTs 格式 `yyyy-MM-dd HH:mm:ss`（与 created_at 同口径）。</summary>
    public List<CollectFailureRow> FetchParseFailureSummary(string sinceTs, string? stationId = null)
    {
        var list = new List<CollectFailureRow>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT error_code, COUNT(*) AS n,
                       MIN(xml_path) AS sample,
                       MIN(skip_reason) AS reason
                FROM parse_failure_log
                WHERE created_at >= @since
                  AND (@st IS NULL OR station_id = @st)
                GROUP BY error_code
                ORDER BY n DESC, error_code ASC";
            cmd.Parameters.AddWithValue("@since", sinceTs ?? "");
            cmd.Parameters.AddWithValue("@st", (object?)stationId ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new CollectFailureRow
                {
                    ErrorCode = r.IsDBNull(0) ? "" : r.GetString(0),
                    Count = r.GetInt32(1),
                    SamplePath = r.IsDBNull(2) ? "" : r.GetString(2),
                    SkipReason = r.IsDBNull(3) ? "" : r.GetString(3),
                });
            }
        }
        catch (Exception ex) { Logger.Warning($"[采集健康] 解析失败汇总查询失败: {ex.Message}"); }
        return list;
    }

    /// <summary>窗口内**异常**解析失败条数（排除正常跳过）。</summary>
    public int CountAbnormalParseFailures(string sinceTs, string? stationId = null)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*) FROM parse_failure_log
                WHERE created_at >= @since
                  AND error_code <> @skip
                  AND (@st IS NULL OR station_id = @st)";
            cmd.Parameters.AddWithValue("@since", sinceTs ?? "");
            cmd.Parameters.AddWithValue("@skip", SkipErrorCode);
            cmd.Parameters.AddWithValue("@st", (object?)stationId ?? DBNull.Value);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? 0 : Convert.ToInt32(v);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[采集健康] 解析失败计数失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>窗口内最近的异常解析失败明细（按时间倒序），告警卡列出前若干条用于定位。</summary>
    public List<CollectFailureItem> FetchRecentParseFailures(int limit, string sinceTs, string? stationId = null)
    {
        var list = new List<CollectFailureItem>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT xml_path, error_code, skip_reason, created_at
                FROM parse_failure_log
                WHERE created_at >= @since
                  AND error_code <> @skip
                  AND (@st IS NULL OR station_id = @st)
                ORDER BY created_at DESC, id DESC
                LIMIT @lim";
            cmd.Parameters.AddWithValue("@since", sinceTs ?? "");
            cmd.Parameters.AddWithValue("@skip", SkipErrorCode);
            cmd.Parameters.AddWithValue("@st", (object?)stationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lim", Math.Max(1, limit));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new CollectFailureItem
                {
                    XmlPath = r.IsDBNull(0) ? "" : r.GetString(0),
                    ErrorCode = r.IsDBNull(1) ? "" : r.GetString(1),
                    SkipReason = r.IsDBNull(2) ? "" : r.GetString(2),
                    CreatedAt = r.IsDBNull(3) ? "" : r.GetString(3),
                });
            }
        }
        catch (Exception ex) { Logger.Warning($"[采集健康] 最近解析失败查询失败: {ex.Message}"); }
        return list;
    }
}
