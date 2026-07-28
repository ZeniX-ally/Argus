using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>SQLite 数据库: 建表、去重入库、统计。</summary>
public class Database
{
    private readonly string _connString;

    public Database(string dbPath)
    {
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        Init();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    private void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS test_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                station_id TEXT NOT NULL,
                model TEXT,
                category TEXT,
                test_date TEXT NOT NULL,
                sn TEXT,
                result TEXT,
                xml_path TEXT UNIQUE,
                fail_reason TEXT,
                tester TEXT,
                panel_status TEXT,
                batch_timestamp TEXT,
                has_fail_items INTEGER,
                file_size INTEGER,
                created_at TEXT DEFAULT (datetime('now','localtime'))
            );
            CREATE INDEX IF NOT EXISTS idx_date ON test_records(test_date);
            CREATE INDEX IF NOT EXISTS idx_sn ON test_records(sn);
            CREATE INDEX IF NOT EXISTS idx_result ON test_records(result);
        ";
        cmd.ExecuteNonQuery();

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = @"
            CREATE TABLE IF NOT EXISTS maintenance_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                station_id TEXT,
                equipment_model TEXT,
                equipment_sn TEXT,
                fail_item TEXT NOT NULL,
                fail_reason TEXT,
                severity TEXT DEFAULT 'major',
                status TEXT DEFAULT 'open',
                resolver TEXT,
                resolution TEXT,
                notes TEXT,
                created_at TEXT DEFAULT (datetime('now','localtime')),
                updated_at TEXT DEFAULT (datetime('now','localtime'))
            );
            CREATE INDEX IF NOT EXISTS idx_maint_status ON maintenance_records(status);
        ";
        cmd2.ExecuteNonQuery();
    }

    /// <summary>已存在的 xml_path 集合(去重用)。</summary>
    public HashSet<string> GetExistingPaths(IEnumerable<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = paths.ToList();
        if (list.Count == 0) return result;
        using var conn = Open();
        // 分批 IN 查询
        const int batch = 500;
        for (int i = 0; i < list.Count; i += batch)
        {
            var chunk = list.Skip(i).Take(batch).ToList();
            using var cmd = conn.CreateCommand();
            var ph = string.Join(",", chunk.Select((_, j) => $"@p{j}"));
            cmd.CommandText = $"SELECT xml_path FROM test_records WHERE xml_path IN ({ph})";
            for (int j = 0; j < chunk.Count; j++)
                cmd.Parameters.AddWithValue($"@p{j}", chunk[j]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
        }
        return result;
    }

    /// <summary>批量插入(忽略重复 xml_path)。返回插入数。</summary>
    public int BatchInsert(IEnumerable<TestRecord> records)
    {
        var list = records.ToList();
        if (list.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        int inserted = 0;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO test_records
            (station_id, model, category, test_date, sn, result, xml_path,
             fail_reason, tester, panel_status, batch_timestamp, has_fail_items, file_size)
            VALUES (@station,@model,@cat,@date,@sn,@result,@path,
                    @reason,@tester,@panel,@ts,@hasfail,@size)";
        var pStation = cmd.Parameters.Add("@station", SqliteType.Text);
        var pModel = cmd.Parameters.Add("@model", SqliteType.Text);
        var pCat = cmd.Parameters.Add("@cat", SqliteType.Text);
        var pDate = cmd.Parameters.Add("@date", SqliteType.Text);
        var pSn = cmd.Parameters.Add("@sn", SqliteType.Text);
        var pResult = cmd.Parameters.Add("@result", SqliteType.Text);
        var pPath = cmd.Parameters.Add("@path", SqliteType.Text);
        var pReason = cmd.Parameters.Add("@reason", SqliteType.Text);
        var pTester = cmd.Parameters.Add("@tester", SqliteType.Text);
        var pPanel = cmd.Parameters.Add("@panel", SqliteType.Text);
        var pTs = cmd.Parameters.Add("@ts", SqliteType.Text);
        var pHasFail = cmd.Parameters.Add("@hasfail", SqliteType.Integer);
        var pSize = cmd.Parameters.Add("@size", SqliteType.Integer);
        foreach (var rec in list)
        {
            pStation.Value = rec.StationId;
            pModel.Value = (object?)rec.Model ?? DBNull.Value;
            pCat.Value = (object?)rec.Category ?? DBNull.Value;
            pDate.Value = rec.TestDate;
            pSn.Value = (object?)rec.Sn ?? DBNull.Value;
            pResult.Value = rec.Result;
            pPath.Value = rec.XmlPath;
            pReason.Value = (object?)rec.FailReason ?? DBNull.Value;
            pTester.Value = (object?)rec.Tester ?? DBNull.Value;
            pPanel.Value = (object?)rec.PanelStatus ?? DBNull.Value;
            pTs.Value = (object?)rec.BatchTimestamp ?? DBNull.Value;
            pHasFail.Value = rec.HasFailItems ? 1 : 0;
            pSize.Value = (object?)rec.FileSize ?? DBNull.Value;
            inserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return inserted;
    }

    public int InsertOne(TestRecord rec) => BatchInsert(new[] { rec });

    /// <summary>累计统计。</summary>
    public StatsData FetchGlobalStats(string stationId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = string.IsNullOrEmpty(stationId) ? "" : "WHERE station_id = @s";
        cmd.CommandText = $@"
            SELECT
                COUNT(CASE WHEN result='PASS' THEN 1 END),
                COUNT(CASE WHEN result='FAIL' THEN 1 END),
                COUNT(CASE WHEN result='INTERRUPTED' THEN 1 END),
                COUNT(CASE WHEN result='INVALID' THEN 1 END),
                COUNT(DISTINCT sn)
            FROM test_records {where}";
        if (!string.IsNullOrEmpty(stationId))
            cmd.Parameters.AddWithValue("@s", stationId);
        using var r = cmd.ExecuteReader();
        var s = new StatsData();
        if (r.Read())
        {
            s.Pass = r.GetInt32(0);
            s.Fail = r.GetInt32(1);
            s.Interrupted = r.GetInt32(2);
            s.Invalid = r.GetInt32(3);
            s.ProductCount = r.GetInt32(4);
        }
        return s;
    }

    /// <summary>今日统计(用 batch_timestamp 日期部分 = 传入 yyyy-MM-dd)。</summary>
    /// <summary>今日统计。用 test_date(目录日期, 格式 yyyyMMdd)判断当日。
    /// 目录日期 PASS/FAIL 都有且格式统一, 比 batch_timestamp(XML内部, 格式可能差异)可靠。
    /// dateYmd 传入 yyyyMMdd。</summary>
    public StatsData FetchDailyStats(string stationId, string dateYmd)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE test_date = @d";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id = @s";
        cmd.CommandText = $@"
            SELECT
                COUNT(CASE WHEN result='PASS' THEN 1 END),
                COUNT(CASE WHEN result='FAIL' THEN 1 END),
                COUNT(CASE WHEN result='INTERRUPTED' THEN 1 END)
            FROM test_records {where}";
        cmd.Parameters.AddWithValue("@d", dateYmd);
        if (!string.IsNullOrEmpty(stationId))
            cmd.Parameters.AddWithValue("@s", stationId);
        using var r = cmd.ExecuteReader();
        var s = new StatsData();
        if (r.Read())
        {
            s.Pass = r.GetInt32(0);
            s.Fail = r.GetInt32(1);
            s.Interrupted = r.GetInt32(2);
        }
        return s;
    }

    // ==================== 维修记录 ====================
    public int CreateMaintenance(MaintenanceRecord m)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO maintenance_records
            (station_id, equipment_model, equipment_sn, fail_item, fail_reason, severity, status, resolver, resolution, notes, created_at)
            VALUES (@st,@model,@sn,@item,@reason,@sev,@status,@resolver,@reso,@notes,
                    COALESCE(NULLIF(@created,''), datetime('now','localtime')));
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@st", (object?)m.StationId ?? "");
        cmd.Parameters.AddWithValue("@model", (object?)m.EquipmentModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sn", (object?)m.EquipmentSn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@item", m.FailItem);
        cmd.Parameters.AddWithValue("@reason", (object?)m.FailReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sev", m.Severity);
        cmd.Parameters.AddWithValue("@status", m.Status);
        cmd.Parameters.AddWithValue("@resolver", (object?)m.Resolver ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reso", (object?)m.Resolution ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@notes", (object?)m.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", (object?)(m.CreatedAt ?? ""));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<MaintenanceRecord> ListMaintenance(string statusFilter = "")
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = string.IsNullOrEmpty(statusFilter) ? "" : "WHERE status = @s";
        cmd.CommandText = $@"SELECT id, station_id, equipment_model, equipment_sn, fail_item, fail_reason,
            severity, status, resolver, resolution, notes, created_at
            FROM maintenance_records {where} ORDER BY id DESC LIMIT 200";
        if (!string.IsNullOrEmpty(statusFilter)) cmd.Parameters.AddWithValue("@s", statusFilter);
        var list = new List<MaintenanceRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new MaintenanceRecord
            {
                Id = r.GetInt32(0),
                StationId = r.IsDBNull(1) ? "" : r.GetString(1),
                EquipmentModel = r.IsDBNull(2) ? "" : r.GetString(2),
                EquipmentSn = r.IsDBNull(3) ? "" : r.GetString(3),
                FailItem = r.IsDBNull(4) ? "" : r.GetString(4),
                FailReason = r.IsDBNull(5) ? "" : r.GetString(5),
                Severity = r.IsDBNull(6) ? "major" : r.GetString(6),
                Status = r.IsDBNull(7) ? "open" : r.GetString(7),
                Resolver = r.IsDBNull(8) ? "" : r.GetString(8),
                Resolution = r.IsDBNull(9) ? "" : r.GetString(9),
                Notes = r.IsDBNull(10) ? "" : r.GetString(10),
                CreatedAt = r.IsDBNull(11) ? "" : r.GetString(11),
            });
        }
        return list;
    }

    public bool UpdateMaintenanceStatus(int id, string status)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE maintenance_records SET status=@s, updated_at=datetime('now','localtime') WHERE id=@id";
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteMaintenance(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM maintenance_records WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ==================== Debug 查询 ====================
    public List<(string sn, string result, string model, string ts, string path)> RecentFails(int limit = 10)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT sn, result, model, batch_timestamp, xml_path FROM test_records
            WHERE result='FAIL' ORDER BY id DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", limit);
        var list = new List<(string, string, string, string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((
                r.IsDBNull(0) ? "" : r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? "" : r.GetString(4)));
        return list;
    }

    /// <summary>查询所有 FAIL 记录(SN / 首个fail项 / 时间 / 路径)。</summary>
    public List<FailRecord> AllFails(string stationId = "")
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE result='FAIL'";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id=@s";
        cmd.CommandText = $@"SELECT sn, fail_reason, batch_timestamp, test_date, model, xml_path
            FROM test_records {where} ORDER BY id DESC LIMIT 2000";
        if (!string.IsNullOrEmpty(stationId)) cmd.Parameters.AddWithValue("@s", stationId);
        var list = new List<FailRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FailRecord
            {
                Sn = r.IsDBNull(0) ? "" : r.GetString(0),
                FailItem = r.IsDBNull(1) ? "" : r.GetString(1),
                Timestamp = r.IsDBNull(2) ? "" : r.GetString(2),
                TestDate = r.IsDBNull(3) ? "" : r.GetString(3),
                Model = r.IsDBNull(4) ? "" : r.GetString(4),
                XmlPath = r.IsDBNull(5) ? "" : r.GetString(5),
            });
        }
        return list;
    }

    public string? MaxSn()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(sn) FROM test_records";
        var v = cmd.ExecuteScalar();
        return v == null || v == DBNull.Value ? null : v.ToString();
    }

    public int TotalRecords()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM test_records";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}

public class StatsData
{
    public int Pass, Fail, Interrupted, Invalid, ProductCount;
}

public class FailRecord
{
    public string Sn = "";
    public string FailItem = "";
    public string Timestamp = "";
    public string TestDate = "";
    public string Model = "";
    public string XmlPath = "";
}

public class MaintenanceRecord
{
    public int Id;
    public string StationId = "";
    public string EquipmentModel = "";
    public string EquipmentSn = "";
    public string FailItem = "";
    public string FailReason = "";
    public string Severity = "major";   // critical/major/minor
    public string Status = "open";       // open/in_progress/resolved/closed
    public string Resolver = "";
    public string Resolution = "";
    public string Notes = "";
    public string CreatedAt = "";
}
