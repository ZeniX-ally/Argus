using Microsoft.Data.Sqlite;

namespace FctAggregator;

public sealed partial class Database
{
    public void SaveCycle(string xmlPath, double? seconds)
    {
        if (string.IsNullOrWhiteSpace(xmlPath)) return;
        if (seconds is double s && (double.IsNaN(s) || double.IsInfinity(s) || s < 0))
            seconds = null;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO test_cycle(xml_path, seconds) VALUES(@p, @s)
            ON CONFLICT(xml_path) DO UPDATE SET seconds = excluded.seconds";
        cmd.Parameters.AddWithValue("@p", xmlPath);
        cmd.Parameters.AddWithValue("@s", (object?)seconds ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void RememberCycles(IEnumerable<TestRecord> records)
    {
        foreach (var rec in records)
        {
            if (!rec.CycleChecked || string.IsNullOrWhiteSpace(rec.XmlPath)) continue;
            try { SaveCycle(rec.XmlPath, rec.CycleSeconds); }
            catch (Exception ex) { Logger.Warning($"[节拍] 写入失败 {rec.XmlPath}: {ex.Message}"); }
        }
    }

    public List<(double Seconds, int? Hour)> ListTodayCycles(string stationId, string dateYmd)
    {
        var list = new List<(double, int?)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var station = string.IsNullOrEmpty(stationId) ? "" : " AND r.station_id = @s";
        cmd.CommandText = $@"
            SELECT c.seconds,
                   CASE WHEN substr(replace(COALESCE(r.batch_timestamp,''), 'T', ' '), 12, 2) GLOB '[0-9][0-9]'
                        THEN CAST(substr(replace(r.batch_timestamp, 'T', ' '), 12, 2) AS INTEGER)
                        ELSE NULL END
              FROM test_cycle c
              JOIN test_records r ON r.xml_path = c.xml_path
             WHERE c.seconds > 0 AND {TestDateEqDay}{station}";
        BindDayDual(cmd, dateYmd);
        if (station.Length > 0) cmd.Parameters.AddWithValue("@s", stationId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var sec = rd.GetDouble(0);
            int? hour = rd.IsDBNull(1) ? null : rd.GetInt32(1);
            if (hour is < 0 or > 23) hour = null;
            list.Add((sec, hour));
        }
        return list;
    }

    public List<string> ListMissingCyclePaths(string stationId, string dateYmd, int limit)
    {
        var list = new List<string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var station = string.IsNullOrEmpty(stationId) ? "" : " AND r.station_id = @s";
        cmd.CommandText = $@"
            SELECT r.xml_path
              FROM test_records r
              LEFT JOIN test_cycle c ON c.xml_path = r.xml_path
             WHERE c.xml_path IS NULL AND r.xml_path IS NOT NULL AND {TestDateEqDay}{station}
             LIMIT @n";
        BindDayDual(cmd, dateYmd);
        if (station.Length > 0) cmd.Parameters.AddWithValue("@s", stationId);
        cmd.Parameters.AddWithValue("@n", Math.Max(1, limit));
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var p = rd.IsDBNull(0) ? "" : rd.GetString(0);
            if (p.Length > 0) list.Add(p);
        }
        return list;
    }
}
