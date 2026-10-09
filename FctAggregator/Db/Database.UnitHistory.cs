using Microsoft.Data.Sqlite;

namespace FctAggregator;

public sealed partial class Database
{
    /// <summary>同一序列号的测试，按时间从早到晚。空白序列号不查，避免空串互相对上。</summary>
    public List<UnitRun> ListUnitRuns(string sn)
    {
        var list = new List<UnitRun>();
        sn = (sn ?? "").Trim();
        if (sn.Length == 0) return list;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(xml_path,''), COALESCE(result,''), COALESCE(fail_reason,''),
                   COALESCE(batch_timestamp,''), COALESCE(test_date,'')
              FROM test_records
             WHERE TRIM(COALESCE(sn,'')) = @sn COLLATE NOCASE
             ORDER BY replace(COALESCE(NULLIF(batch_timestamp,''), test_date, ''), 'T', ' '), xml_path";
        cmd.Parameters.AddWithValue("@sn", sn);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new UnitRun
            {
                XmlPath = rd.GetString(0),
                Result = rd.GetString(1),
                FailReason = rd.GetString(2),
                Timestamp = rd.GetString(3),
                TestDate = rd.GetString(4),
            });
        }
        return list;
    }

    /// <summary>这台更早的记录里，最近一次出现该测试项的值。测量值和失败项都算，当前这份排除。</summary>
    public UnitItemPrior? FindPriorItem(string sn, string testName, string currentPath, string currentOrder)
    {
        sn = (sn ?? "").Trim();
        testName = (testName ?? "").Trim();
        if (sn.Length == 0 || testName.Length == 0) return null;
        var ord = (currentOrder ?? "").Replace('T', ' ').Trim();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT value, value_text, lolim, hilim FROM (
                SELECT fi.value AS value, fi.value_text AS value_text, fi.lolim AS lolim, fi.hilim AS hilim,
                       replace(COALESCE(NULLIF(fi.ts,''), NULLIF(r.batch_timestamp,''), r.test_date, ''), 'T', ' ') AS ord,
                       r.xml_path AS path
                  FROM fail_items fi
                  JOIN test_records r ON r.id = fi.record_id
                 WHERE TRIM(COALESCE(r.sn,'')) = @sn COLLATE NOCASE
                   AND fi.test_name = @name COLLATE NOCASE
                UNION ALL
                SELECT m.value, m.value_text, m.lolim, m.hilim,
                       replace(COALESCE(NULLIF(m.ts,''), NULLIF(r.batch_timestamp,''), r.test_date, ''), 'T', ' '),
                       r.xml_path
                  FROM test_measurements m
                  JOIN test_records r ON r.id = m.record_id
                 WHERE TRIM(COALESCE(r.sn,'')) = @sn COLLATE NOCASE
                   AND m.test_name = @name COLLATE NOCASE
            )
             WHERE path <> @path COLLATE NOCASE
               AND ord < @ord
             ORDER BY ord DESC
             LIMIT 1";
        cmd.Parameters.AddWithValue("@sn", sn);
        cmd.Parameters.AddWithValue("@name", testName);
        cmd.Parameters.AddWithValue("@path", currentPath ?? "");
        cmd.Parameters.AddWithValue("@ord", ord);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return null;
        return new UnitItemPrior
        {
            Value = rd.IsDBNull(0) ? null : rd.GetDouble(0),
            ValueText = rd.IsDBNull(1) ? "" : rd.GetString(1),
            Lo = rd.IsDBNull(2) ? null : rd.GetDouble(2),
            Hi = rd.IsDBNull(3) ? null : rd.GetDouble(3),
        };
    }
}
