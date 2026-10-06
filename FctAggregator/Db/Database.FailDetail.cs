using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>
/// 测项级 FAIL 明细（一个失败项一行）：FAIL 页表格与导出共用。
/// 老记录（v3.28 前采集、无 fail_items 明细）也会出现——项目退回 fail_reason 汇总串、值/limit 留空。
/// </summary>
public class FailItemDetail
{
    public string Model = "";
    public string Sn = "";
    /// <summary>测试 FAIL 时间（fail_items.ts 优先，退回 batch_timestamp）。</summary>
    public string Ts = "";
    /// <summary>失败项目（无明细的老记录退回 fail_reason 汇总串）。</summary>
    public string TestName = "";
    /// <summary>失败证据原串（优先用于展示）。</summary>
    public string ValueText = "";
    public double? Value;
    public double? LoLim;
    public double? HiLim;
    public string Unit = "";
    public string XmlPath = "";
    /// <summary>是否来自 fail_items 明细（false = 老记录，值是退回的汇总串）。</summary>
    public bool HasDetail;

    /// <summary>值文案：失败证据原串优先，其次格式化数值（沿用图表同款极值转科学计数规则），都没有给空串。</summary>
    public string ValueDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ValueText)) return ValueText;
            if (!Value.HasValue) return "";
            var a = Math.Abs(Value.Value);
            return a > 0 && a < 0.001
                ? Value.Value.ToString("0.####E+0", CultureInfo.InvariantCulture)
                : Value.Value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>限值文案：双侧「下限 ~ 上限」，单边只给一边，都没有给空串（后缀单位）。</summary>
    public string LimitDisplay
    {
        get
        {
            var u = string.IsNullOrWhiteSpace(Unit) ? "" : Unit;
            if (LoLim.HasValue && HiLim.HasValue)
                return $"{Num(LoLim.Value)} ~ {Num(HiLim.Value)}{u}";
            if (LoLim.HasValue) return $"\u2265 {Num(LoLim.Value)}{u}";
            if (HiLim.HasValue) return $"\u2264 {Num(HiLim.Value)}{u}";
            return "";
        }
    }

    /// <summary>数值展示文案（与趋势图/限值同款：极大极小走科学计数）。导出小计的均值也复用它。</summary>
    public static string Num(double v) => Math.Abs(v) >= 1000 || (Math.Abs(v) > 0 && Math.Abs(v) < 0.001)
        ? v.ToString("0.####E+0", CultureInfo.InvariantCulture)
        : v.ToString("0.####", CultureInfo.InvariantCulture);
}

public sealed partial class Database
{
    /// <summary>
    /// 测项级 FAIL 明细。两种用法：
    /// ① 屏幕表格：<paramref name="recentRecords"/> &gt; 0 —— 先取最近 N 条 FAIL 记录再展开其失败项（保持"最近"语义）；
    /// ② 导出：传 <paramref name="fromYmd"/> / <paramref name="toYmdInclusive"/> —— 取该 <b>test_date 闭区间</b>的全部 FAIL
    ///    （8 位目录日期，与 FAIL 页 KPI 同口径，保证导出的数字与界面能对上）。
    /// 默认按「项目（忽略大小写）→ 时间倒序」排序。月表会再按失败次数排，传 <paramref name="orderByItem"/> false 省掉这一次。
    /// </summary>
    public List<FailItemDetail> QueryFailItemDetails(
        string stationId = "", string? fromYmd = null, string? toYmdInclusive = null,
        int recentRecords = 0, int maxRows = 200000, bool orderByItem = true)
    {
        var list = new List<FailItemDetail>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        bool byStation = !string.IsNullOrEmpty(stationId);
        var conds = new List<string>();

        if (recentRecords > 0)
        {
            var sub = "SELECT id FROM test_records WHERE result='FAIL'";
            if (byStation) sub += " AND station_id=@s";
            sub += " ORDER BY id DESC LIMIT @recent";
            conds.Add($"r.id IN ({sub})");
            cmd.Parameters.AddWithValue("@recent", Math.Max(1, recentRecords));
        }
        else
        {
            conds.Add("r.result='FAIL'");
            if (byStation) conds.Add("r.station_id=@s");
            if (!string.IsNullOrWhiteSpace(fromYmd) || !string.IsNullOrWhiteSpace(toYmdInclusive))
                conds.Add(RTestDateRangeClosed);
        }
        if (byStation) cmd.Parameters.AddWithValue("@s", stationId);
        if (!string.IsNullOrWhiteSpace(fromYmd) || !string.IsNullOrWhiteSpace(toYmdInclusive))
        {
            var a = string.IsNullOrWhiteSpace(fromYmd) ? DateTime.Today.AddYears(-50).ToString("yyyyMMdd") : fromYmd;
            var b = string.IsNullOrWhiteSpace(toYmdInclusive) ? DateTime.Today.ToString("yyyyMMdd") : toYmdInclusive;
            BindRangeDual(cmd, a, b);
        }

        cmd.CommandText = $@"
            SELECT r.model, r.sn,
                   COALESCE(NULLIF(fi.ts,''), r.batch_timestamp, r.test_date) AS ts,
                   COALESCE(NULLIF(fi.test_name,''), r.fail_reason, '') AS item,
                   fi.value_text, fi.value, fi.lolim, fi.hilim, COALESCE(fi.unit,''),
                   COALESCE(r.xml_path,''), CASE WHEN fi.id IS NULL THEN 0 ELSE 1 END
            FROM test_records r
            LEFT JOIN fail_items fi ON fi.record_id = r.id
            WHERE {string.Join(" AND ", conds)}
            {(orderByItem ? "ORDER BY item COLLATE NOCASE, ts DESC" : "")}
            LIMIT @max";
        cmd.Parameters.AddWithValue("@max", Math.Max(1, maxRows));

        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new FailItemDetail
            {
                Model = rd.IsDBNull(0) ? "" : rd.GetString(0),
                Sn = rd.IsDBNull(1) ? "" : rd.GetString(1),
                Ts = rd.IsDBNull(2) ? "" : rd.GetString(2),
                TestName = rd.IsDBNull(3) ? "" : rd.GetString(3),
                ValueText = rd.IsDBNull(4) ? "" : rd.GetString(4),
                Value = rd.IsDBNull(5) ? null : rd.GetDouble(5),
                LoLim = rd.IsDBNull(6) ? null : rd.GetDouble(6),
                HiLim = rd.IsDBNull(7) ? null : rd.GetDouble(7),
                Unit = rd.IsDBNull(8) ? "" : rd.GetString(8),
                XmlPath = rd.IsDBNull(9) ? "" : rd.GetString(9),
                HasDetail = !rd.IsDBNull(10) && rd.GetInt64(10) != 0,
            });
        }
        return list;
    }

    /// <summary>某自然月内，按失败项目名（忽略大小写）计失败明细条数。口径与 FAIL 页同一条 LEFT JOIN。</summary>
    public Dictionary<string, int> CountFailDetailsByName(string fromYmd, string toYmdInclusive, string stationId = "")
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var conds = new List<string> { "r.result='FAIL'", RTestDateRangeClosed };
        if (!string.IsNullOrEmpty(stationId))
        {
            conds.Add("r.station_id=@s");
            cmd.Parameters.AddWithValue("@s", stationId);
        }
        BindRangeDual(cmd, fromYmd, toYmdInclusive);
        cmd.CommandText = $@"
            SELECT COALESCE(NULLIF(fi.test_name,''), r.fail_reason, '') AS item, COUNT(*)
              FROM test_records r
              LEFT JOIN fail_items fi ON fi.record_id = r.id
             WHERE {string.Join(" AND ", conds)}
             GROUP BY item COLLATE NOCASE";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var name = rd.IsDBNull(0) ? "" : rd.GetString(0);
            if (name.Length == 0) continue;
            dict[name] = rd.GetInt32(1);
        }
        return dict;
    }

    /// <summary>某项目在区间内最近几条失败明细（时间倒序），给测试报告提示用。</summary>
    public List<FailItemDetail> ListRecentFailDetails(string testName, string fromYmd, string toYmdInclusive, int limit = 5)
    {
        var list = new List<FailItemDetail>();
        if (string.IsNullOrWhiteSpace(testName)) return list;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT r.model, r.sn,
                   COALESCE(NULLIF(fi.ts,''), r.batch_timestamp, r.test_date) AS ts,
                   COALESCE(NULLIF(fi.test_name,''), r.fail_reason, '') AS item,
                   fi.value_text, fi.value, fi.lolim, fi.hilim, COALESCE(fi.unit,'')
              FROM test_records r
              LEFT JOIN fail_items fi ON fi.record_id = r.id
             WHERE r.result='FAIL' AND {RTestDateRangeClosed}
               AND COALESCE(NULLIF(fi.test_name,''), r.fail_reason, '') = @name COLLATE NOCASE
             ORDER BY ts DESC
             LIMIT @lim";
        BindRangeDual(cmd, fromYmd, toYmdInclusive);
        cmd.Parameters.AddWithValue("@name", testName.Trim());
        cmd.Parameters.AddWithValue("@lim", Math.Max(1, limit));
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new FailItemDetail
            {
                Model = rd.IsDBNull(0) ? "" : rd.GetString(0),
                Sn = rd.IsDBNull(1) ? "" : rd.GetString(1),
                Ts = rd.IsDBNull(2) ? "" : rd.GetString(2),
                TestName = rd.IsDBNull(3) ? "" : rd.GetString(3),
                ValueText = rd.IsDBNull(4) ? "" : rd.GetString(4),
                Value = rd.IsDBNull(5) ? null : rd.GetDouble(5),
                LoLim = rd.IsDBNull(6) ? null : rd.GetDouble(6),
                HiLim = rd.IsDBNull(7) ? null : rd.GetDouble(7),
                Unit = rd.IsDBNull(8) ? "" : rd.GetString(8),
                HasDetail = true,
            });
        }
        return list;
    }

    /// <summary>本月同一项目失败次数达到门槛、且还没有待办卡时，补一张待确认卡。已有卡不改累计次数。</summary>
    public void EnsureRepeatFailTodos(string fromYmd, string toYmdInclusive)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(NULLIF(fi.test_name,''), r.fail_reason, '') AS item,
                   COALESCE(r.station_id,''),
                   COUNT(*),
                   MAX(COALESCE(r.model,'')),
                   MAX(COALESCE(NULLIF(fi.ts,''), r.batch_timestamp, r.test_date, ''))
              FROM test_records r
              LEFT JOIN fail_items fi ON fi.record_id = r.id
             WHERE r.result='FAIL' AND {RTestDateRangeClosed}
             GROUP BY item COLLATE NOCASE, r.station_id
            HAVING COUNT(*) >= @n AND item <> ''";
        BindRangeDual(cmd, fromYmd, toYmdInclusive);
        cmd.Parameters.AddWithValue("@n", RepeatFailMonth.Threshold);
        var hits = new List<(string Name, string Station, int Count, string Model, string Seen)>();
        using (var rd = cmd.ExecuteReader())
        {
            while (rd.Read())
                hits.Add((rd.GetString(0), rd.GetString(1), rd.GetInt32(2), rd.IsDBNull(3) ? "" : rd.GetString(3), rd.IsDBNull(4) ? "" : rd.GetString(4)));
        }
        foreach (var h in hits)
        {
            string key;
            try { key = TodoGrouping.MergeKeyOf(h.Name); }
            catch { key = h.Name.Trim(); }
            if (key.Length == 0) key = h.Name.Trim();
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT id FROM todo_items WHERE group_key=@k AND station_id=@s";
            sel.Parameters.AddWithValue("@k", key);
            sel.Parameters.AddWithValue("@s", h.Station);
            if (sel.ExecuteScalar() != null) continue;
            using var ins = conn.CreateCommand();
            ins.CommandText = @"
                INSERT INTO todo_items
                    (group_key, station_id, title, model, variants, variant_count,
                     fail_count, first_seen, last_seen, state)
                VALUES (@k, @s, @t, @m, @v, 1, @c, @seen, @seen, 'pending')";
            ins.Parameters.AddWithValue("@k", key);
            ins.Parameters.AddWithValue("@s", h.Station);
            ins.Parameters.AddWithValue("@t", h.Name.Trim());
            ins.Parameters.AddWithValue("@m", h.Model);
            ins.Parameters.AddWithValue("@v", h.Name.Trim());
            ins.Parameters.AddWithValue("@c", h.Count);
            ins.Parameters.AddWithValue("@seen", h.Seen);
            ins.ExecuteNonQuery();
        }
    }
}

public static class RepeatFailMonth
{
    public const int Threshold = 5;

    public static (string FromYmd, string ToYmd) Range(DateTime day)
    {
        var from = new DateTime(day.Year, day.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        return (from.ToString("yyyyMMdd"), to.ToString("yyyyMMdd"));
    }

    public static int Lookup(IReadOnlyDictionary<string, int> counts, string? name)
    {
        if (counts == null || string.IsNullOrWhiteSpace(name)) return 0;
        return counts.TryGetValue(name, out var n) ? n : 0;
    }

    public static void Apply(IEnumerable<TodoItem> todos, IReadOnlyDictionary<string, int> counts)
    {
        foreach (var t in todos)
        {
            int n = Lookup(counts, t.Title);
            foreach (var v in t.Variants)
            {
                var cv = Lookup(counts, v);
                if (cv > n) n = cv;
            }
            t.MonthCount = n;
        }
    }
}
