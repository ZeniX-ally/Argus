using Microsoft.Data.Sqlite;

namespace FctAggregator;

public sealed partial class Database
{
    private readonly string _connString;
    private readonly string _dbPath;

    public event Action<MaintenanceRecord, string, string>? MaintenanceStatusChanged;

    private void NotifyStatusChanged(MaintenanceRecord rec, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        try { MaintenanceStatusChanged?.Invoke(rec, from, to); }
        catch (Exception ex) { Logger.Warning($"状态变更回调异常: {ex.Message}"); }
    }

    public event Action<List<(TestRecord Rec, long Id)>>? RecordsInserted;

    private void NotifyRecordsInserted(List<(TestRecord, long)> rows)
    {
        if (rows.Count == 0) return;
        try { RecordsInserted?.Invoke(rows); }
        catch (Exception ex) { Logger.Warning($"插入事件回调异常: {ex.Message}"); }
    }

    public static Database? Current { get; private set; }

    public Database(string dbPath)
    {
        _dbPath = dbPath;
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        Init();
        Current = this;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Init()
    {
        using (var conn = Open())
        {
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
                    fixture_id TEXT,
                    created_at TEXT DEFAULT (datetime('now','localtime'))
                );
                CREATE INDEX IF NOT EXISTS idx_date ON test_records(test_date);
                CREATE INDEX IF NOT EXISTS idx_sn ON test_records(sn);
                CREATE INDEX IF NOT EXISTS idx_result ON test_records(result);
            ";
            cmd.ExecuteNonQuery();

            using (var cmdFix = conn.CreateCommand())
            {
                cmdFix.CommandText = "ALTER TABLE test_records ADD COLUMN fixture_id TEXT;";
                try { cmdFix.ExecuteNonQuery(); }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
            }

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

            using var cmd3 = conn.CreateCommand();
            cmd3.CommandText = @"
                CREATE TABLE IF NOT EXISTS resolvers (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    created_at TEXT DEFAULT (datetime('now','localtime'))
                );
            ";
            cmd3.ExecuteNonQuery();

            using var cmdErr = conn.CreateCommand();
            cmdErr.CommandText = @"
                CREATE TABLE IF NOT EXISTS parse_failure_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    xml_path TEXT NOT NULL,
                    error_code TEXT,
                    skip_reason TEXT,
                    station_id TEXT,
                    created_at TEXT DEFAULT (datetime('now','localtime'))
                );
                CREATE INDEX IF NOT EXISTS idx_pf_code ON parse_failure_log(error_code);
            ";
            cmdErr.ExecuteNonQuery();
            using var cmdSlow = conn.CreateCommand();
            cmdSlow.CommandText = @"
                CREATE TABLE IF NOT EXISTS db_slow_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    sql TEXT,
                    ms INTEGER,
                    ts TEXT DEFAULT (datetime('now','localtime'))
                );
                CREATE INDEX IF NOT EXISTS idx_slow_ts ON db_slow_log(ts);
            ";
            cmdSlow.ExecuteNonQuery();
            using var cmdHealth = conn.CreateCommand();
            cmdHealth.CommandText = @"
                CREATE TABLE IF NOT EXISTS db_health_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    check_type TEXT,
                    result TEXT,
                    ts TEXT DEFAULT (datetime('now','localtime'))
                );
                CREATE INDEX IF NOT EXISTS idx_health_ts ON db_health_log(ts);
            ";
            cmdHealth.ExecuteNonQuery();
            using var cmd4 = conn.CreateCommand();
            cmd4.CommandText = @"
                CREATE TABLE IF NOT EXISTS dismissed_todos (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    fail_item TEXT NOT NULL,
                    station_id TEXT,
                    model TEXT,
                    dismissed_at TEXT DEFAULT (datetime('now','localtime'))
                );
                CREATE INDEX IF NOT EXISTS idx_dismissed_item ON dismissed_todos(fail_item);
            ";
            cmd4.ExecuteNonQuery();

            using var cmd5 = conn.CreateCommand();
            cmd5.CommandText = @"
                CREATE TABLE IF NOT EXISTS todo_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    group_key TEXT NOT NULL,
                    station_id TEXT NOT NULL DEFAULT '',
                    title TEXT NOT NULL,
                    model TEXT,
                    variants TEXT,
                    variant_count INTEGER NOT NULL DEFAULT 1,
                    fail_count INTEGER NOT NULL DEFAULT 0,
                    first_seen TEXT,
                    last_seen TEXT,
                    state TEXT NOT NULL DEFAULT 'pending',
                    maintenance_id INTEGER,
                    resolved_at TEXT,
                    created_at TEXT DEFAULT (datetime('now','localtime')),
                    updated_at TEXT DEFAULT (datetime('now','localtime'))
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_todo_group ON todo_items(group_key, station_id);
                CREATE INDEX IF NOT EXISTS idx_todo_state ON todo_items(state);

                CREATE TABLE IF NOT EXISTS app_meta (
                    k TEXT PRIMARY KEY,
                    v TEXT
                );
            ";
            cmd5.ExecuteNonQuery();

            using var cmd6 = conn.CreateCommand();
            cmd6.CommandText = @"
                CREATE TABLE IF NOT EXISTS todo_sync_state (
                    origin_machine TEXT NOT NULL,
                    todo_id INTEGER NOT NULL,
                    owner TEXT,
                    state TEXT,
                    version INTEGER NOT NULL DEFAULT 0,
                    updated_at TEXT,
                    PRIMARY KEY (origin_machine, todo_id)
                );
            ";
            cmd6.ExecuteNonQuery();

            using var cmd7 = conn.CreateCommand();
            cmd7.CommandText = @"
                CREATE TABLE IF NOT EXISTS device_samples_local (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    cpu_usage REAL NOT NULL,
                    mem_used_pct REAL NOT NULL,
                    disk_free_gb REAL NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_device_samples_local_ts ON device_samples_local(ts);
            ";
            cmd7.ExecuteNonQuery();

            // 规格06 多元深分析：PASS 测量值 + 失败项明细（fail_items 期2启用，先建表）
            using var cmd8 = conn.CreateCommand();
            cmd8.CommandText = @"
                CREATE TABLE IF NOT EXISTS test_measurements (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    record_id INTEGER NOT NULL,
                    xml_path TEXT NOT NULL,
                    station_id TEXT,
                    model TEXT,
                    sn TEXT,
                    test_date TEXT,
                    ts TEXT,
                    test_name TEXT NOT NULL,
                    value REAL,
                    value_text TEXT,
                    lolim REAL,
                    hilim REAL,
                    unit TEXT,
                    rule TEXT,
                    UNIQUE(record_id, test_name)
                );
                CREATE INDEX IF NOT EXISTS idx_tm_name_ts ON test_measurements(test_name, ts);
                CREATE INDEX IF NOT EXISTS idx_tm_ts ON test_measurements(ts);

                CREATE TABLE IF NOT EXISTS fail_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    record_id INTEGER NOT NULL,
                    xml_path TEXT NOT NULL,
                    station_id TEXT,
                    model TEXT,
                    sn TEXT,
                    test_date TEXT,
                    ts TEXT,
                    fixture_id TEXT,
                    tester TEXT,
                    panel_status TEXT,
                    hour INTEGER,
                    test_name TEXT NOT NULL,
                    value REAL,
                    value_text TEXT,
                    lolim REAL,
                    hilim REAL,
                    unit TEXT,
                    rule TEXT,
                    UNIQUE(record_id, test_name)
                );
                CREATE INDEX IF NOT EXISTS idx_fi_name_ts ON fail_items(test_name, ts);
                CREATE INDEX IF NOT EXISTS idx_fi_fixture_ts ON fail_items(fixture_id, ts);
                CREATE INDEX IF NOT EXISTS idx_fi_tester_ts ON fail_items(tester, ts);
                CREATE INDEX IF NOT EXISTS idx_fi_ts ON fail_items(ts);
            ";
            cmd8.ExecuteNonQuery();

            // 规格06期3：TDMS 特征化（每通道基础统计，analyze_tdms_enabled 默认关）
            using var cmd9 = conn.CreateCommand();
            cmd9.CommandText = @"
                CREATE TABLE IF NOT EXISTS tdms_features (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    record_id INTEGER NOT NULL,
                    xml_path TEXT NOT NULL,
                    station_id TEXT,
                    model TEXT,
                    sn TEXT,
                    test_date TEXT,
                    ts TEXT,
                    tdms_path TEXT NOT NULL,
                    group_name TEXT NOT NULL,
                    channel_name TEXT NOT NULL,
                    section TEXT,
                    n INTEGER,
                    vmin REAL,
                    vmax REAL,
                    vmean REAL,
                    vstd REAL,
                    vfirst REAL,
                    vlast REAL,
                    UNIQUE(record_id, group_name, channel_name)
                );
                CREATE INDEX IF NOT EXISTS idx_tf_ts ON tdms_features(ts);
                CREATE INDEX IF NOT EXISTS idx_tf_model_ts ON tdms_features(model, ts);
                CREATE INDEX IF NOT EXISTS idx_tf_channel_ts ON tdms_features(group_name, channel_name, ts);
            ";
            cmd9.ExecuteNonQuery();

            // 正常态自学习底座：每 (source, model, signal_key) 一行增量模型（Welford+P² 状态持久化在 qstate）
            using var cmd10 = conn.CreateCommand();
            cmd10.CommandText = @"
                CREATE TABLE IF NOT EXISTS normal_models (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    source TEXT NOT NULL,
                    model TEXT NOT NULL,
                    signal_key TEXT NOT NULL,
                    n INTEGER NOT NULL,
                    mean REAL, sigma REAL,
                    p01 REAL, p50 REAL, p99 REAL,
                    min_v REAL, max_v REAL,
                    last_ts TEXT,
                    status TEXT NOT NULL DEFAULT 'learning',
                    qstate TEXT,
                    UNIQUE(source, model, signal_key)
                );
                CREATE INDEX IF NOT EXISTS idx_nm_status ON normal_models(status);
            ";
            cmd10.ExecuteNonQuery();

            // 审计 M13：归因统计四条查询以 substr(...) 过滤，非 sargable 全表扫描×4（维护窗口拉长）——
            // 补虚拟生成列 batch_ts_date + 索引（幂等；虚拟列不回填历史，老库零成本升级）
            using var cmdTsDate = conn.CreateCommand();
            cmdTsDate.CommandText = "SELECT COUNT(*) FROM pragma_table_info('test_records') WHERE name='batch_ts_date'";
            var hasTsDate = Convert.ToInt64(cmdTsDate.ExecuteScalar() ?? 0L);
            if (hasTsDate == 0)
            {
                cmdTsDate.CommandText = "ALTER TABLE test_records ADD COLUMN batch_ts_date TEXT GENERATED ALWAYS AS (substr(COALESCE(NULLIF(batch_timestamp,''),created_at),1,10)) VIRTUAL";
                cmdTsDate.ExecuteNonQuery();
            }
            cmdTsDate.CommandText = "CREATE INDEX IF NOT EXISTS idx_tr_tsdate ON test_records(batch_ts_date)";
            cmdTsDate.ExecuteNonQuery();
        }
        MigrateClosedStatus();
    }

    private void MigrateClosedStatus()
    {
        try
        {
            int pending;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM maintenance_records WHERE status='closed'";
                pending = Convert.ToInt32(cmd.ExecuteScalar());
            }
            if (pending == 0) return;

            if (!BackupDbFile())
            {
                Logger.Warning($"[迁移] 备份未完成, 跳过 closed->resolved 迁移({pending} 条暂按「已完成」显示)");
                return;
            }

            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE maintenance_records SET status='resolved' WHERE status='closed'";
                var done = cmd.ExecuteNonQuery();
                Logger.Info($"[迁移] {done} 条维修记录状态「已关闭」-> 「已完成」(v2.2.0 状态体系合并)");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[迁移] 维修记录状态迁移失败: {ex.Message}");
        }
    }

    private bool BackupDbFile()
    {
        try
        {
            if (!File.Exists(_dbPath)) return true;
            var bak = $"{_dbPath}.bak-{DateTime.Now:yyyyMMdd}";
            if (File.Exists(bak))
            {
                Logger.Info($"[迁移] 已存在当日备份, 直接迁移: {Path.GetFileName(bak)}");
                return true;
            }
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            File.Copy(_dbPath, bak);
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = File.OpenRead(bak);
                var hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-","").ToLowerInvariant();
                File.WriteAllText(bak + ".sha256", hash);
            }
            catch { }
            Logger.Info($"[迁移] 数据库已备份: {Path.GetFileName(bak)}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[迁移] 数据库备份失败: {ex.Message}");
            return false;
        }
    }

    public const int BackupKeepDays = 7;
    public string? BackupDaily()
    {
        try
        {
            if (!File.Exists(_dbPath)) return null;
            var bak = $"{_dbPath}.bak-{DateTime.Now:yyyyMMdd}";
            if (File.Exists(bak)) return null;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            File.Copy(_dbPath, bak);
            var dir = Path.GetDirectoryName(_dbPath)!;
            var prefix = Path.GetFileName(_dbPath) + ".bak-";
            var old = Directory.Exists(dir)
                ? Directory.GetFiles(dir, prefix + "*").OrderByDescending(f => f, StringComparer.Ordinal).ToList()
                : new List<string>();
            foreach (var f in old.Skip(BackupKeepDays))
            {
                try { File.Delete(f); try { File.Delete(f + ".sha256"); } catch { } } catch { }
            }
            Logger.Info($"[数据库] 每日备份完成: {Path.GetFileName(bak)}（保留 {BackupKeepDays} 份）");
            return bak;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[数据库] 每日备份失败: {ex.Message}");
            return null;
        }
    }

    public HashSet<string> GetExistingPaths(IEnumerable<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = paths.ToList();
        if (list.Count == 0) return result;
        using var conn = Open();
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

    /// <summary>规格06：按 xml_path 批量查记录 id（可按 result 过滤），供回填补测量值用。</summary>
    public Dictionary<string, long> GetRecordIdsByPaths(IEnumerable<string> paths, string? result = null)
    {
        var result1 = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var list = paths.ToList();
        if (list.Count == 0) return result1;
        using var conn = Open();
        const int batch = 500;
        for (int i = 0; i < list.Count; i += batch)
        {
            var chunk = list.Skip(i).Take(batch).ToList();
            using var cmd = conn.CreateCommand();
            var ph = string.Join(",", chunk.Select((_, j) => $"@p{j}"));
            cmd.CommandText = $"SELECT id, xml_path FROM test_records WHERE xml_path IN ({ph})" +
                              (string.IsNullOrEmpty(result) ? "" : " AND result = @res");
            if (!string.IsNullOrEmpty(result)) cmd.Parameters.AddWithValue("@res", result);
            for (int j = 0; j < chunk.Count; j++)
                cmd.Parameters.AddWithValue($"@p{j}", chunk[j]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result1[r.GetString(1)] = r.GetInt64(0);
        }
        return result1;
    }

    public int BatchInsert(IEnumerable<TestRecord> records, List<long>? insertedIds = null)
    {
        var list = records.ToList();
        if (list.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        int inserted = 0;
        var insertedRows = new List<(TestRecord, long)>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT OR IGNORE INTO test_records
            (station_id, model, category, test_date, sn, result, xml_path,
             fail_reason, tester, panel_status, batch_timestamp, has_fail_items, file_size, fixture_id)
            VALUES (@station,@model,@cat,@date,@sn,@result,@path,
                    @reason,@tester,@panel,@ts,@hasfail,@size,@fixture)
            RETURNING id";
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
        var pFixture = cmd.Parameters.Add("@fixture", SqliteType.Text);
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
            pFixture.Value = (object?)rec.FixtureId ?? DBNull.Value;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                inserted++;
                insertedRows.Add((rec, r.GetInt64(0)));
            }
        }
        tx.Commit();
        if (insertedIds != null)
            foreach (var (_, id) in insertedRows) insertedIds.Add(id);
        NotifyRecordsInserted(insertedRows);
        InsertMeasurementsFor(insertedRows);
        InsertFailItemsFor(insertedRows);
        return inserted;
    }

    /// <summary>审计 A2：单条入库并返回新记录 id（INSERT OR IGNORE 未命中即重复记录时返回 0），免上层反查。</summary>
    public long InsertOneReturnId(TestRecord rec)
    {
        var ids = new List<long>(1);
        BatchInsert(new[] { rec }, ids);
        return ids.Count > 0 ? ids[0] : 0;
    }

    /// <summary>规格06：PASS 测量值批量入库（独立事务，失败仅告警不回滚主记录）。</summary>
    public void InsertMeasurementsFor(List<(TestRecord Rec, long Id)> rows)
    {
        var withM = rows.Where(r => r.Rec.Measurements.Count > 0).ToList();
        if (withM.Count == 0) return;
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR IGNORE INTO test_measurements
                (record_id, xml_path, station_id, model, sn, test_date, ts, test_name, value, value_text, lolim, hilim, unit, rule)
                VALUES (@rid, @path, @station, @model, @sn, @date, @ts, @name, @value, @vtext, @lolim, @hilim, @unit, @rule)";
            var pRid = cmd.Parameters.Add("@rid", SqliteType.Integer);
            var pPath = cmd.Parameters.Add("@path", SqliteType.Text);
            var pStation = cmd.Parameters.Add("@station", SqliteType.Text);
            var pModel = cmd.Parameters.Add("@model", SqliteType.Text);
            var pSn = cmd.Parameters.Add("@sn", SqliteType.Text);
            var pDate = cmd.Parameters.Add("@date", SqliteType.Text);
            var pTs = cmd.Parameters.Add("@ts", SqliteType.Text);
            var pName = cmd.Parameters.Add("@name", SqliteType.Text);
            var pValue = cmd.Parameters.Add("@value", SqliteType.Real);
            var pVText = cmd.Parameters.Add("@vtext", SqliteType.Text);
            var pLo = cmd.Parameters.Add("@lolim", SqliteType.Real);
            var pHi = cmd.Parameters.Add("@hilim", SqliteType.Real);
            var pUnit = cmd.Parameters.Add("@unit", SqliteType.Text);
            var pRule = cmd.Parameters.Add("@rule", SqliteType.Text);
            foreach (var (rec, id) in withM)
            {
                var ts = (object?)rec.BatchTimestamp ?? DBNull.Value;
                foreach (var m in rec.Measurements)
                {
                    pRid.Value = id;
                    pPath.Value = rec.XmlPath;
                    pStation.Value = (object?)rec.StationId ?? DBNull.Value;
                    pModel.Value = (object?)rec.Model ?? DBNull.Value;
                    pSn.Value = (object?)rec.Sn ?? DBNull.Value;
                    pDate.Value = (object?)rec.TestDate ?? DBNull.Value;
                    pTs.Value = ts;
                    pName.Value = m.TestName;
                    pValue.Value = (object?)m.Value ?? DBNull.Value;
                    pVText.Value = (object?)m.ValueText ?? DBNull.Value;
                    pLo.Value = (object?)m.Lolim ?? DBNull.Value;
                    pHi.Value = (object?)m.Hilim ?? DBNull.Value;
                    pUnit.Value = (object?)m.Unit ?? DBNull.Value;
                    pRule.Value = (object?)m.Rule ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[分析采集] 测量值入库失败(不影响主记录): {ex.Message}");
        }
    }

    public int PurgeOldMeasurements(int retentionDays = 30)
    {
        var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, retentionDays)).ToString("yyyy-MM-dd");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM test_measurements WHERE COALESCE(ts,'') <> '' AND substr(COALESCE(ts,''),1,10) < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    public int PurgeOldFailItems(int retentionDays = 90)
    {
        var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, retentionDays)).ToString("yyyy-MM-dd");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM fail_items WHERE COALESCE(ts,'') <> '' AND substr(COALESCE(ts,''),1,10) < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>规格06期2：FAIL 失败项明细批量入库（独立事务，失败仅告警不回滚主记录）。value_text 恒存原串。</summary>
    public void InsertFailItemsFor(List<(TestRecord Rec, long Id)> rows)
    {
        var withF = rows.Where(r => r.Rec.Result == "FAIL" && r.Rec.FailedTests.Count > 0).ToList();
        if (withF.Count == 0) return;
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR IGNORE INTO fail_items
                (record_id, xml_path, station_id, model, sn, test_date, ts, fixture_id, tester, panel_status, hour, test_name, value, value_text, lolim, hilim, unit, rule)
                VALUES (@rid, @path, @station, @model, @sn, @date, @ts, @fixture, @tester, @panel, @hour, @name, @value, @vtext, @lolim, @hilim, @unit, @rule)";
            var pRid = cmd.Parameters.Add("@rid", SqliteType.Integer);
            var pPath = cmd.Parameters.Add("@path", SqliteType.Text);
            var pStation = cmd.Parameters.Add("@station", SqliteType.Text);
            var pModel = cmd.Parameters.Add("@model", SqliteType.Text);
            var pSn = cmd.Parameters.Add("@sn", SqliteType.Text);
            var pDate = cmd.Parameters.Add("@date", SqliteType.Text);
            var pTs = cmd.Parameters.Add("@ts", SqliteType.Text);
            var pFixture = cmd.Parameters.Add("@fixture", SqliteType.Text);
            var pTester = cmd.Parameters.Add("@tester", SqliteType.Text);
            var pPanel = cmd.Parameters.Add("@panel", SqliteType.Text);
            var pHour = cmd.Parameters.Add("@hour", SqliteType.Integer);
            var pName = cmd.Parameters.Add("@name", SqliteType.Text);
            var pValue = cmd.Parameters.Add("@value", SqliteType.Real);
            var pVText = cmd.Parameters.Add("@vtext", SqliteType.Text);
            var pLo = cmd.Parameters.Add("@lolim", SqliteType.Real);
            var pHi = cmd.Parameters.Add("@hilim", SqliteType.Real);
            var pUnit = cmd.Parameters.Add("@unit", SqliteType.Text);
            var pRule = cmd.Parameters.Add("@rule", SqliteType.Text);
            static double? ParseNum(string? s) => string.IsNullOrWhiteSpace(s) ? null :
                double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
            static int? ParseHour(string? ts)
            {
                if (string.IsNullOrWhiteSpace(ts) || ts.Length < 13) return null;
                return int.TryParse(ts.Substring(11, 2), out var h) && h is >= 0 and <= 23 ? h : (int?)null;
            }
            foreach (var (rec, id) in withF)
            {
                var ts = (object?)rec.BatchTimestamp ?? DBNull.Value;
                var hour = (object?)ParseHour(rec.BatchTimestamp) ?? DBNull.Value;
                foreach (var f in rec.FailedTests)
                {
                    pRid.Value = id;
                    pPath.Value = rec.XmlPath;
                    pStation.Value = (object?)rec.StationId ?? DBNull.Value;
                    pModel.Value = (object?)rec.Model ?? DBNull.Value;
                    pSn.Value = (object?)rec.Sn ?? DBNull.Value;
                    pDate.Value = (object?)rec.TestDate ?? DBNull.Value;
                    pTs.Value = ts;
                    pFixture.Value = (object?)rec.FixtureId ?? DBNull.Value;
                    pTester.Value = (object?)rec.Tester ?? DBNull.Value;
                    pPanel.Value = (object?)rec.PanelStatus ?? DBNull.Value;
                    pHour.Value = hour;
                    pName.Value = f.Name;
                    pValue.Value = (object?)ParseNum(f.Value) ?? DBNull.Value;
                    pVText.Value = string.IsNullOrEmpty(f.Value) ? DBNull.Value : f.Value;
                    pLo.Value = (object?)ParseNum(f.Lolim) ?? DBNull.Value;
                    pHi.Value = (object?)ParseNum(f.Hilim) ?? DBNull.Value;
                    pUnit.Value = (object?)f.Unit ?? DBNull.Value;
                    pRule.Value = (object?)f.Rule ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[分析采集] 失败项明细入库失败(不影响主记录): {ex.Message}");
        }
    }

    /// <summary>规格06期3：TDMS 通道特征批量入库（独立事务 + INSERT OR IGNORE 幂等，失败仅告警不回滚主记录）。</summary>
    public void InsertTdmsFeaturesFor(TestRecord rec, long id, string tdmsPath, List<TdmsFeatureAnalyzer.TdmsFeatureRow> rows)
    {
        if (rows.Count == 0) return;
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR IGNORE INTO tdms_features
                (record_id, xml_path, station_id, model, sn, test_date, ts, tdms_path, group_name, channel_name, section, n, vmin, vmax, vmean, vstd, vfirst, vlast)
                VALUES (@rid, @path, @station, @model, @sn, @date, @ts, @tdms, @group, @channel, @section, @n, @vmin, @vmax, @vmean, @vstd, @vfirst, @vlast)";
            var pRid = cmd.Parameters.Add("@rid", SqliteType.Integer);
            var pPath = cmd.Parameters.Add("@path", SqliteType.Text);
            var pStation = cmd.Parameters.Add("@station", SqliteType.Text);
            var pModel = cmd.Parameters.Add("@model", SqliteType.Text);
            var pSn = cmd.Parameters.Add("@sn", SqliteType.Text);
            var pDate = cmd.Parameters.Add("@date", SqliteType.Text);
            var pTs = cmd.Parameters.Add("@ts", SqliteType.Text);
            var pTdms = cmd.Parameters.Add("@tdms", SqliteType.Text);
            var pGroup = cmd.Parameters.Add("@group", SqliteType.Text);
            var pChannel = cmd.Parameters.Add("@channel", SqliteType.Text);
            var pSection = cmd.Parameters.Add("@section", SqliteType.Text);
            var pN = cmd.Parameters.Add("@n", SqliteType.Integer);
            var pVMin = cmd.Parameters.Add("@vmin", SqliteType.Real);
            var pVMax = cmd.Parameters.Add("@vmax", SqliteType.Real);
            var pVMean = cmd.Parameters.Add("@vmean", SqliteType.Real);
            var pVStd = cmd.Parameters.Add("@vstd", SqliteType.Real);
            var pVFirst = cmd.Parameters.Add("@vfirst", SqliteType.Real);
            var pVLast = cmd.Parameters.Add("@vlast", SqliteType.Real);
            var ts = (object?)rec.BatchTimestamp ?? DBNull.Value;
            foreach (var f in rows)
            {
                pRid.Value = id;
                pPath.Value = rec.XmlPath;
                pStation.Value = (object?)rec.StationId ?? DBNull.Value;
                pModel.Value = (object?)rec.Model ?? DBNull.Value;
                pSn.Value = (object?)rec.Sn ?? DBNull.Value;
                pDate.Value = (object?)rec.TestDate ?? DBNull.Value;
                pTs.Value = ts;
                pTdms.Value = tdmsPath;
                pGroup.Value = f.GroupName;
                pChannel.Value = f.ChannelName;
                pSection.Value = (object?)f.Section ?? DBNull.Value;
                pN.Value = f.N;
                pVMin.Value = (object?)f.Min ?? DBNull.Value;
                pVMax.Value = (object?)f.Max ?? DBNull.Value;
                pVMean.Value = (object?)f.Mean ?? DBNull.Value;
                pVStd.Value = (object?)f.Std ?? DBNull.Value;
                pVFirst.Value = (object?)f.First ?? DBNull.Value;
                pVLast.Value = (object?)f.Last ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[TDMS] 特征入库失败(不影响主记录): {ex.Message}");
        }
    }

    public int PurgeOldTdmsFeatures(int retentionDays = 30)
    {
        var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, retentionDays)).ToString("yyyy-MM-dd");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tdms_features WHERE COALESCE(ts,'') <> '' AND substr(COALESCE(ts,''),1,10) < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>UI 用：最近 TDMS 通道特征（含冗余列，免回查 test_records）。</summary>
    public List<(long RecordId, string Model, string Sn, string GroupName, string ChannelName, string Section, int N, double? Min, double? Max, double? Mean, double? Std, string Ts)>
        GetRecentTdmsFeatures(int limit = 50)
    {
        var list = new List<(long, string, string, string, string, string, int, double?, double?, double?, double?, string)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT record_id, COALESCE(model,''), COALESCE(sn,''), group_name, channel_name,
                   COALESCE(section,''), COALESCE(n,0), vmin, vmax, vmean, vstd, COALESCE(ts,'')
            FROM tdms_features ORDER BY id DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", Math.Max(1, limit));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            double? Rd(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                      r.GetString(5), r.GetInt32(6), Rd(7), Rd(8), Rd(9), Rd(10), r.GetString(11)));
        }
        return list;
    }

    /// <summary>快照用：TDMS 特征总量摘要。</summary>
    public (long Rows, long Records, string LastTs) GetTdmsFeatureSummary()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COUNT(DISTINCT record_id), COALESCE(MAX(ts),'') FROM tdms_features";
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1), r.GetString(2)) : (0, 0, "");
    }

    /// <summary>正常态底座：upsert 单个模型行（qstate 为 Welford+P² 状态 JSON）。</summary>
    public void UpsertNormalModel(NormalModelRow r)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO normal_models (source, model, signal_key, n, mean, sigma, p01, p50, p99, min_v, max_v, last_ts, status, qstate)
            VALUES (@s, @m, @k, @n, @mean, @sigma, @p01, @p50, @p99, @minv, @maxv, @ts, @status, @qs)
            ON CONFLICT(source, model, signal_key) DO UPDATE SET
                n=@n, mean=@mean, sigma=@sigma, p01=@p01, p50=@p50, p99=@p99,
                min_v=@minv, max_v=@maxv, last_ts=@ts, status=@status, qstate=@qs";
        cmd.Parameters.AddWithValue("@s", r.Source);
        cmd.Parameters.AddWithValue("@m", r.Model);
        cmd.Parameters.AddWithValue("@k", r.SignalKey);
        cmd.Parameters.AddWithValue("@n", r.N);
        cmd.Parameters.AddWithValue("@mean", r.Mean);
        cmd.Parameters.AddWithValue("@sigma", r.Sigma);
        cmd.Parameters.AddWithValue("@p01", r.P01);
        cmd.Parameters.AddWithValue("@p50", r.P50);
        cmd.Parameters.AddWithValue("@p99", r.P99);
        cmd.Parameters.AddWithValue("@minv", r.MinV);
        cmd.Parameters.AddWithValue("@maxv", r.MaxV);
        cmd.Parameters.AddWithValue("@ts", r.LastTs);
        cmd.Parameters.AddWithValue("@status", r.Status);
        cmd.Parameters.AddWithValue("@qs", r.QState);
        cmd.ExecuteNonQuery();
    }

    /// <summary>正常态底座：按三元组取模型行（含 qstate），无则 null。</summary>
    public NormalModelRow? GetNormalModel(string source, string model, string signalKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT source, model, signal_key, n, COALESCE(mean,0), COALESCE(sigma,0), COALESCE(p01,0), COALESCE(p50,0), COALESCE(p99,0),
                   COALESCE(min_v,0), COALESCE(max_v,0), COALESCE(last_ts,''), status, COALESCE(qstate,'')
            FROM normal_models WHERE source=@s AND model=@m AND signal_key=@k";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@m", model ?? "");
        cmd.Parameters.AddWithValue("@k", signalKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new NormalModelRow
        {
            Source = r.GetString(0), Model = r.GetString(1), SignalKey = r.GetString(2),
            N = r.GetInt64(3), Mean = r.GetDouble(4), Sigma = r.GetDouble(5),
            P01 = r.GetDouble(6), P50 = r.GetDouble(7), P99 = r.GetDouble(8),
            MinV = r.GetDouble(9), MaxV = r.GetDouble(10),
            LastTs = r.GetString(11), Status = r.GetString(12), QState = r.GetString(13),
        };
    }

    /// <summary>规格06期3：backfill 用——按 xml_path 批量取记录简报（免重解析 XML）。</summary>
    public List<TdmsRecordBrief> GetRecordBriefsByPaths(IEnumerable<string> paths)
    {
        var list = new List<TdmsRecordBrief>();
        var all = paths.Select(Path.GetFullPath).ToList();
        for (int i = 0; i < all.Count; i += 500)
        {
            var chunk = all.Skip(i).Take(500).ToList();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var names = new List<string>();
            for (int j = 0; j < chunk.Count; j++) names.Add($"@p{j}");
            cmd.CommandText = $@"
                SELECT id, xml_path, COALESCE(station_id,''), COALESCE(model,''), COALESCE(category,''),
                       COALESCE(sn,''), COALESCE(test_date,''), COALESCE(NULLIF(batch_timestamp,''),created_at)
                FROM test_records WHERE xml_path IN ({string.Join(",", names)})";
            for (int j = 0; j < chunk.Count; j++)
                cmd.Parameters.AddWithValue(names[j], chunk[j]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new TdmsRecordBrief
                {
                    Id = r.GetInt64(0),
                    XmlPath = r.GetString(1),
                    StationId = r.GetString(2),
                    Model = r.GetString(3),
                    Category = r.GetString(4),
                    Sn = r.GetString(5),
                    TestDate = r.GetString(6),
                    Ts = r.IsDBNull(7) ? "" : r.GetString(7),
                });
            }
        }
        return list;
    }

    /// <summary>规格06期2：归因聚合源——全局计数 + fixture/tester/hour 三维桶 + 逐小时失败数。</summary>
    public AttributionSource FetchAttributionStats(string since)
    {
        var src = new AttributionSource();
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT COUNT(*),
                       SUM(CASE WHEN result='FAIL' THEN 1 ELSE 0 END)
                FROM test_records
                WHERE batch_ts_date >= @since";
            cmd.Parameters.AddWithValue("@since", since);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                src.TotalN = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                src.TotalFails = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            }
        }
        var dims = new (string Dim, string KeyExpr, string ExtraWhere)[]
        {
            ("fixture", "COALESCE(fixture_id,'')", "AND COALESCE(fixture_id,'') <> ''"),
            ("tester", "COALESCE(tester,'')", "AND COALESCE(tester,'') <> '' AND COALESCE(tester,'') <> 'UNKNOWN'"),
            ("hour", "CAST(substr(COALESCE(NULLIF(batch_timestamp,''),created_at),12,2) AS INTEGER)", ""),
        };
        foreach (var (dim, keyExpr, extra) in dims)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT {keyExpr} AS k, COUNT(*) AS n,
                       SUM(CASE WHEN result='FAIL' THEN 1 ELSE 0 END) AS fails,
                       SUM(CASE WHEN panel_status IS NOT NULL AND panel_status<>'' AND panel_status<>'Passed' THEN 1 ELSE 0 END) AS panelng
                FROM test_records
                WHERE batch_ts_date >= @since
                  {extra}
                GROUP BY k";
            cmd.Parameters.AddWithValue("@since", since);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0)) continue;
                src.Buckets.Add(new AttrBucketRow
                {
                    Dim = dim,
                    Key = r.GetValue(0).ToString() ?? "",
                    N = r.GetInt64(1),
                    Fails = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                    PanelNg = r.IsDBNull(3) ? 0 : r.GetInt64(3),
                });
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT CAST(substr(COALESCE(NULLIF(batch_timestamp,''),created_at),12,2) AS INTEGER) AS h,
                       SUM(CASE WHEN result='FAIL' THEN 1 ELSE 0 END)
                FROM test_records
                WHERE batch_ts_date >= @since
                  AND length(COALESCE(NULLIF(batch_timestamp,''),created_at)) >= 12
                GROUP BY h";
            cmd.Parameters.AddWithValue("@since", since);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0)) continue;
                var h = Convert.ToInt32(r.GetValue(0));
                if (h is >= 0 and <= 23)
                    src.HourlyFails[h] = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            }
        }
        return src;
    }

    /// <summary>规格06期2：tester 效应量聚合源（测量值 JOIN 主记录取 tester）。</summary>
    public List<TesterStatsRow> FetchTesterMeasurementStats(string since)
    {
        var list = new List<TesterStatsRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(m.model,''), m.test_name, COALESCE(r.tester,''), COUNT(*),
                   AVG(m.value), AVG(m.value*m.value),
                   MIN(m.lolim), MAX(m.hilim), MAX(m.unit)
            FROM test_measurements m
            JOIN test_records r ON r.id = m.record_id
            WHERE m.ts >= @since AND m.value IS NOT NULL
              AND COALESCE(r.tester,'') <> '' AND COALESCE(r.tester,'') <> 'UNKNOWN'
            GROUP BY 1, 2, 3";
        cmd.Parameters.AddWithValue("@since", since);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long n = r.GetInt64(3);
            double mean = r.IsDBNull(4) ? 0 : r.GetDouble(4);
            double meanSq = r.IsDBNull(5) ? 0 : r.GetDouble(5);
            double popVar = Math.Max(0, meanSq - mean * mean);
            double sampleVar = n > 1 ? popVar * n / (n - 1) : 0;
            list.Add(new TesterStatsRow
            {
                Model = r.GetString(0),
                TestName = r.GetString(1),
                Tester = r.GetString(2),
                N = (int)n,
                Mean = mean,
                Sigma = Math.Sqrt(sampleVar),
                Lolim = r.IsDBNull(6) ? null : r.GetDouble(6),
                Hilim = r.IsDBNull(7) ? null : r.GetDouble(7),
                Unit = r.IsDBNull(8) ? null : r.GetString(8),
            });
        }
        return list;
    }

    /// <summary>规格06：测量值按 (model, test_name) 聚合统计（ts 日期前缀区间，to 端不含）。</summary>
    public List<MeasureStatsRow> AggregateMeasurementStats(string fromDay, string toDayExclusive)
    {
        var list = new List<MeasureStatsRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(model,''), test_name, COUNT(*), AVG(value), AVG(value*value),
                   MIN(lolim), MAX(hilim), MAX(unit)
            FROM test_measurements
            WHERE ts >= @from AND ts < @to AND value IS NOT NULL
            GROUP BY COALESCE(model,''), test_name";
        cmd.Parameters.AddWithValue("@from", fromDay);
        cmd.Parameters.AddWithValue("@to", toDayExclusive);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long n = r.GetInt64(2);
            double mean = r.IsDBNull(3) ? 0 : r.GetDouble(3);
            double meanSq = r.IsDBNull(4) ? 0 : r.GetDouble(4);
            double popVar = Math.Max(0, meanSq - mean * mean);
            double sampleVar = n > 1 ? popVar * n / (n - 1) : 0;
            list.Add(new MeasureStatsRow
            {
                Model = r.GetString(0),
                TestName = r.GetString(1),
                N = (int)n,
                Mean = mean,
                Sigma = Math.Sqrt(sampleVar),
                Lolim = r.IsDBNull(5) ? null : r.GetDouble(5),
                Hilim = r.IsDBNull(6) ? null : r.GetDouble(6),
                Unit = r.IsDBNull(7) ? null : r.GetString(7),
            });
        }
        return list;
    }

    public int InsertOne(TestRecord rec) => BatchInsert(new[] { rec });
    public void LogParseFailure(string xmlPath, string errorCode, string skipReason, string stationId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO parse_failure_log(xml_path, error_code, skip_reason, station_id) VALUES(@p,@c,@s,@st)";
            cmd.Parameters.AddWithValue("@p", xmlPath ?? "");
            cmd.Parameters.AddWithValue("@c", errorCode ?? "");
            cmd.Parameters.AddWithValue("@s", skipReason ?? "");
            cmd.Parameters.AddWithValue("@st", stationId ?? "");
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Logger.Warning($"[解析失败日志] 写入失败: {ex.Message}"); }
    }

    public void LogSlowQuery(string sql, long ms)
    {
        if (ms < 500) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO db_slow_log(sql, ms) VALUES(@s,@m)";
            cmd.Parameters.AddWithValue("@s", (sql ?? "").Length > 2000 ? (sql ?? "").Substring(0,2000) : (sql ?? ""));
            cmd.Parameters.AddWithValue("@m", ms);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public void LogHealth(string checkType, string result)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO db_health_log(check_type, result) VALUES(@t,@r)";
            cmd.Parameters.AddWithValue("@t", checkType ?? "");
            cmd.Parameters.AddWithValue("@r", result ?? "");
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public string RunHealthCheck()
    {
        string result = "ok";
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            var r = cmd.ExecuteScalar()?.ToString() ?? "ok";
            result = r;
            LogHealth("integrity_check", r);
            if (!r.Equals("ok", StringComparison.OrdinalIgnoreCase))
                Logger.Warning($"[DB健康] integrity_check 异常: {r}");
        }
        catch (Exception ex)
        {
            result = ex.Message;
            LogHealth("integrity_check", "error:" + ex.Message);
        }
        return result;
    }

    // TSV 归档需要压平的字符（制表/回车/换行）
    private static readonly char[] SanitizeTsvChars = { '\t', '\r', '\n' };

    public int ArchiveColdData(int warmDays = 90)
    {
        if (warmDays <= 0) return 0;
        var cutoff = DateTime.Today.AddDays(-warmDays).ToString("yyyyMMdd");
        try
        {
            int toArchive;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM test_records WHERE test_date < @c";
                cmd.Parameters.AddWithValue("@c", cutoff);
                toArchive = Convert.ToInt32(cmd.ExecuteScalar());
            }
            if (toArchive == 0) return 0;
            var archDir = Path.Combine(Path.GetDirectoryName(_dbPath) ?? ".", "archive");
            Directory.CreateDirectory(archDir);
            var archFile = Path.Combine(archDir, $"test_records_before_{cutoff}.tsv");
            string[] cols = { "id", "station_id", "model", "category", "test_date", "sn", "result", "xml_path",
                              "fail_reason", "tester", "panel_status", "batch_timestamp", "has_fail_items",
                              "file_size", "fixture_id", "created_at" };
            // 删除前先备份数据库文件；备份失败视为归档失败（不删数据）
            if (!BackupDbFile())
                throw new IOException("数据库备份失败，跳过冷数据删除");
            // 真归档：同一事务内先流式导出全部待归档行到 TSV，全部写成功后才执行 DELETE 并 COMMIT；
            // 写文件失败/异常则回滚且不删任何数据
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    using (var writer = new StreamWriter(archFile, append: false))
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = $"SELECT {string.Join(", ", cols)} FROM test_records WHERE test_date < @c ORDER BY id";
                        cmd.Parameters.AddWithValue("@c", cutoff);
                        using var r = cmd.ExecuteReader();
                        writer.WriteLine(string.Join('\t', cols));
                        writer.WriteLine($"cutoff={cutoff} count={toArchive} at={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        var vals = new string[cols.Length];
                        while (r.Read())
                        {
                            for (int i = 0; i < cols.Length; i++)
                            {
                                var v = r.IsDBNull(i) ? "" : r.GetValue(i)?.ToString() ?? "";
                                // TSV 安全化：fail_reason 等字段可能含换行/制表符，直接写会列错行错，统一压成空格
                                if (v.IndexOfAny(SanitizeTsvChars) >= 0)
                                    v = v.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                                vals[i] = v;
                            }
                            writer.WriteLine(string.Join('\t', vals));
                        }
                    }
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM test_records WHERE test_date < @c";
                        cmd.Parameters.AddWithValue("@c", cutoff);
                        var deleted = cmd.ExecuteNonQuery();
                        tx.Commit();
                        Logger.Info($"[DB分层] 归档冷数据 {deleted} 条 (test_date < {cutoff}) -> {archFile}");
                        LogHealth("archive_cold", $"deleted={deleted} cutoff={cutoff}");
                        return deleted;
                    }
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    try { if (File.Exists(archFile)) File.Delete(archFile); } catch { }
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[DB分层] 归档失败: {ex.Message}");
            return 0;
        }
    }

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
                COUNT(DISTINCT COALESCE(NULLIF(COALESCE(sn,''),''), xml_path))
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
                COUNT(CASE WHEN result='INTERRUPTED' THEN 1 END),
                COUNT(DISTINCT COALESCE(NULLIF(COALESCE(sn,''),''), xml_path))
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
            s.TodayProductCount = r.GetInt32(3);
        }
        return s;
    }

    /// <summary>当月统计（SN 终检口径）：每个产品（SN，空 SN 按 xml_path 兜底）只取当月最后一次测试结果，
    /// 维修重测不再重复计入分母；良率 = 终检 PASS / (终检 PASS + 终检 FAIL)，中断不计分母。</summary>
    public StatsData FetchMonthlyStats(string stationId, string dateYm)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 8 位日期下 substr 前缀匹配 == 闭区间范围匹配（@to 上界取 "31"，不存在该日也不会越到下月），
        // 范围谓词可走 idx_date 索引，避免 UI 定时器每 5 秒触发全表扫描
        var from = dateYm + "01";
        var to = dateYm + "31";
        var where = "WHERE (test_date >= @from AND test_date <= @to)";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id = @s";
        cmd.CommandText = $@"
            SELECT
                COUNT(CASE WHEN result='PASS' THEN 1 END),
                COUNT(CASE WHEN result='FAIL' THEN 1 END),
                COUNT(CASE WHEN result='INTERRUPTED' THEN 1 END),
                COUNT(*)
            FROM (
                SELECT result, ROW_NUMBER() OVER (
                    PARTITION BY COALESCE(NULLIF(COALESCE(sn,''),''), xml_path)
                    ORDER BY id DESC) AS rn
                FROM test_records {where}
            ) WHERE rn = 1";
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        if (!string.IsNullOrEmpty(stationId))
            cmd.Parameters.AddWithValue("@s", stationId);
        using var r = cmd.ExecuteReader();
        var s = new StatsData();
        if (r.Read())
        {
            s.Pass = r.GetInt32(0);
            s.Fail = r.GetInt32(1);
            s.Interrupted = r.GetInt32(2);
            s.TodayProductCount = r.GetInt32(3);
        }
        return s;
    }

    /// <summary>列出 SN 缺失的记录（历史回填用），返回 (id, xml_path)。</summary>
    public List<(long Id, string XmlPath)> ListRecordsMissingSn()
    {
        var list = new List<(long, string)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, xml_path FROM test_records
            WHERE (sn IS NULL OR sn = '') AND COALESCE(xml_path,'') <> ''
            ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    /// <summary>回填单条记录的 SN（仅当仍为空时生效，幂等）。返回受影响行数。</summary>
    public int UpdateMissingSn(long id, string sn)
    {
        if (string.IsNullOrWhiteSpace(sn)) return 0;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE test_records SET sn = @sn WHERE id = @id AND (sn IS NULL OR sn = '')";
        cmd.Parameters.AddWithValue("@sn", sn.Trim());
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>批量回填 SN：单连接单事务内执行与 UpdateMissingSn 相同的 UPDATE（仅写仍为空的行，幂等），
    /// 避免逐条独立事务每条一次 fsync。返回总受影响行数；
    /// 批内任一条失败（如锁冲突）则回滚整批并退化为逐条重试，保证不整批丢失。</summary>
    public int UpdateMissingSnBatch(List<(long Id, string Sn)> rows)
    {
        if (rows.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        try
        {
            int total = 0;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE test_records SET sn = @sn WHERE id = @id AND (sn IS NULL OR sn = '')";
            var pSn = cmd.Parameters.Add("@sn", SqliteType.Text);
            var pId = cmd.Parameters.Add("@id", SqliteType.Integer);
            foreach (var (id, sn) in rows)
            {
                if (string.IsNullOrWhiteSpace(sn)) continue;
                pSn.Value = sn.Trim();
                pId.Value = id;
                total += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return total;
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            Logger.Warning($"[DB] SN 批量回填失败(已回滚本批)，退化为逐条重试: {ex.Message}");
        }
        // 逐条重试该批：与 UpdateMissingSn 同语义（仅写仍为空的行），个别失败不影响其余行
        int retried = 0;
        foreach (var (id, sn) in rows)
        {
            try { retried += UpdateMissingSn(id, sn); }
            catch (Exception ex2) { Logger.Warning($"[DB] SN 逐条回填 id={id} 失败: {ex2.Message}"); }
        }
        return retried;
    }

    public List<HourlyStatItem> FetchDailyHourlyStats(string stationId, string dateYmd)
    {
        var items = new List<HourlyStatItem>();
        for (int h = 0; h < 24; h++)
        {
            items.Add(new HourlyStatItem { Hour = h });
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE test_date = @d";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id = @s";
        cmd.CommandText = $@"
            SELECT result, COALESCE(batch_timestamp,''), COALESCE(created_at,''), COALESCE(xml_path,'')
            FROM test_records {where}";
        cmd.Parameters.AddWithValue("@d", dateYmd);
        if (!string.IsNullOrEmpty(stationId))
            cmd.Parameters.AddWithValue("@s", stationId);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var res = r.GetString(0);
            var batchTs = r.GetString(1);
            var created = r.GetString(2);
            var xml = r.GetString(3);

            int h = ExtractHourFromRecord(xml, batchTs, created);
            if (h >= 0 && h < 24)
            {
                if (string.Equals(res, "PASS", StringComparison.OrdinalIgnoreCase))
                    items[h].Pass++;
                else if (string.Equals(res, "FAIL", StringComparison.OrdinalIgnoreCase))
                    items[h].Fail++;
            }
        }
        return items;
    }

    public List<TopFailItem> FetchDailyTopFails(string stationId, string dateYmd, int limit = 5,
        bool? mergeOverride = null, string? mergeLevel = null)
    {
        var cfg = AppConfig.Instance;
        bool merge = mergeOverride ?? cfg.LearnFailMergeEnabled;
        var level = mergeLevel ?? cfg.LearnFailMergeLevel;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE test_date = @d AND result = 'FAIL'";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id = @s";
        cmd.CommandText = $@"
            SELECT COALESCE(fail_reason,''), COALESCE(station_id,'')
            FROM test_records {where}";
        cmd.Parameters.AddWithValue("@d", dateYmd);
        if (!string.IsNullOrEmpty(stationId))
            cmd.Parameters.AddWithValue("@s", stationId);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stationDist = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int totalFailCount = 0;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rawReason = r.GetString(0).Trim();
            var st = r.GetString(1).Trim();
            if (string.IsNullOrEmpty(rawReason)) rawReason = "未知测项错误";

            var split = rawReason.Split(new[] { '\r', '\n', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length == 0) split = new[] { rawReason };

            foreach (var item in split)
            {
                var cleaned = item.Trim();
                if (string.IsNullOrEmpty(cleaned)) continue;
                var key = cleaned;
                if (merge)
                {
                    key = FailReasonMerger.GetMergedKey(cleaned, true, level);
                    if (!hints.ContainsKey(key))
                    {
                        var pr = FailReasonMerger.Parse(cleaned);
                        if (!string.IsNullOrEmpty(pr.RootCauseHint)) hints[key] = pr.RootCauseHint;
                    }
                }
                totalFailCount++;
                counts[key] = counts.GetValueOrDefault(key) + 1;

                if (!stationDist.TryGetValue(key, out var mDict))
                {
                    mDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    stationDist[key] = mDict;
                }
                if (!string.IsNullOrEmpty(st))
                {
                    mDict[st] = mDict.GetValueOrDefault(st) + 1;
                }
            }
        }

        var list = new List<TopFailItem>();
        var topPairs = counts.OrderByDescending(kv => kv.Value).Take(limit);
        foreach (var p in topPairs)
        {
            string topStation = "";
            if (stationDist.TryGetValue(p.Key, out var mDict) && mDict.Count > 0)
            {
                topStation = mDict.OrderByDescending(kv => kv.Value).First().Key;
            }
            list.Add(new TopFailItem
            {
                FailItem = p.Key,
                Count = p.Value,
                Ratio = totalFailCount > 0 ? (double)p.Value / totalFailCount * 100.0 : 0.0,
                MainStation = topStation,
                RootCauseHint = hints.GetValueOrDefault(p.Key, "")
            });
        }
        return list;
    }

    public List<LiveFailAlert> FetchRecentFailAlerts(string stationId, int limit = 10)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE result = 'FAIL'";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id = @s";
        cmd.CommandText = $@"
            SELECT id, COALESCE(sn,''), COALESCE(station_id,''), COALESCE(model,''),
                   COALESCE(fail_reason,''), COALESCE(tester,''),
                   COALESCE(batch_timestamp,''), COALESCE(created_at,''), COALESCE(xml_path,'')
            FROM test_records {where}
            ORDER BY id DESC LIMIT @lim";
        if (!string.IsNullOrEmpty(stationId))
            cmd.Parameters.AddWithValue("@s", stationId);
        cmd.Parameters.AddWithValue("@lim", limit);

        var list = new List<LiveFailAlert>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var alert = new LiveFailAlert
            {
                Id = r.GetInt64(0),
                Sn = r.GetString(1),
                StationId = r.GetString(2),
                Model = r.GetString(3),
                FailReason = r.GetString(4),
                Tester = r.GetString(5),
                TimeText = FormatLogTime(r.GetString(8), r.GetString(6), r.GetString(7)),
                XmlPath = r.GetString(8)
            };
            list.Add(alert);
        }
        return list;
    }

    private static int ExtractHourFromRecord(string xmlPath, string batchTs, string createdAt)
    {
        if (!string.IsNullOrEmpty(xmlPath))
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(xmlPath);
            var m = System.Text.RegularExpressions.Regex.Match(fileName, @"_(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})");
            if (m.Success && int.TryParse(m.Groups[4].Value, out var h) && h >= 0 && h <= 23)
                return h;
        }
        if (!string.IsNullOrEmpty(batchTs))
        {
            var m = System.Text.RegularExpressions.Regex.Match(batchTs, @"[T\s](\d{2}):(\d{2})");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var h) && h >= 0 && h <= 23)
                return h;
        }
        if (!string.IsNullOrEmpty(createdAt))
        {
            var m = System.Text.RegularExpressions.Regex.Match(createdAt, @"[T\s](\d{2}):(\d{2})");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var h) && h >= 0 && h <= 23)
                return h;
        }
        return -1;
    }

    public static string FormatLogTime(string xmlPath, string batchTs, string createdAt)
    {
        if (!string.IsNullOrEmpty(xmlPath))
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(xmlPath);
            var m = System.Text.RegularExpressions.Regex.Match(fileName, @"_(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})");
            if (m.Success)
            {
                return $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value} {m.Groups[4].Value}:{m.Groups[5].Value}:{m.Groups[6].Value}";
            }
        }
        if (!string.IsNullOrEmpty(batchTs))
        {
            if (DateTime.TryParse(batchTs.Replace('T', ' '), out var dt))
                return dt.ToString("yyyy-MM-dd HH:mm:ss");
            return batchTs;
        }
        if (!string.IsNullOrEmpty(createdAt)) return createdAt;
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public int CreateMaintenance(MaintenanceRecord m)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO maintenance_records
            (station_id, equipment_model, equipment_sn, fail_item, fail_reason, severity, status, resolver, resolution, notes, created_at, updated_at)
            VALUES (@st,@model,@sn,@item,@reason,@sev,@status,@resolver,@reso,@notes,
                    COALESCE(NULLIF(@created,''), datetime('now','localtime')),
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

    public List<MaintenanceRecord> ListMaintenance(string statusFilter = "", int limit = 500)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = string.IsNullOrEmpty(statusFilter) ? "" : "WHERE status = @s";
        cmd.CommandText = $@"SELECT id, station_id, equipment_model, equipment_sn, fail_item, fail_reason,
            severity, status, resolver, resolution, notes, created_at, updated_at
            FROM maintenance_records {where}
            ORDER BY COALESCE(NULLIF(updated_at,''), created_at, '') DESC, id DESC
            LIMIT @lim";
        if (!string.IsNullOrEmpty(statusFilter)) cmd.Parameters.AddWithValue("@s", statusFilter);
        cmd.Parameters.AddWithValue("@lim", limit <= 0 ? 500 : limit);
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
                UpdatedAt = r.IsDBNull(12) ? "" : r.GetString(12),
            });
        }
        return list;
    }

    public Dictionary<string, int> CountMaintenanceByStatus()
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, COUNT(*) FROM maintenance_records GROUP BY status";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = r.IsDBNull(0) ? "open" : r.GetString(0);
            dict[key] = r.GetInt32(1);
        }
        return dict;
    }

    public bool UpdateMaintenanceStatus(int id, string status)
    {
        string from;
        using (var conn = Open())
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT status FROM maintenance_records WHERE id=@id";
            sel.Parameters.AddWithValue("@id", id);
            from = sel.ExecuteScalar() as string ?? "";
            if (string.Equals(from, status, StringComparison.OrdinalIgnoreCase)) return true;
        }
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE maintenance_records SET status=@s, updated_at=datetime('now','localtime') WHERE id=@id";
            cmd.Parameters.AddWithValue("@s", status);
            cmd.Parameters.AddWithValue("@id", id);
            if (cmd.ExecuteNonQuery() == 0) return false;
        }
        var snapshot = GetMaintenance(id);
        if (snapshot != null) NotifyStatusChanged(snapshot, from, status);
        return true;
    }

    public bool UpdateMaintenance(MaintenanceRecord m)
    {
        string from;
        using (var conn = Open())
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT status FROM maintenance_records WHERE id=@id";
            sel.Parameters.AddWithValue("@id", m.Id);
            from = sel.ExecuteScalar() as string ?? "";
        }
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
            UPDATE maintenance_records SET
                equipment_model=@model, equipment_sn=@sn, fail_item=@item, fail_reason=@reason,
                severity=@sev, status=@status, resolver=@resolver, resolution=@reso, notes=@notes,
                created_at=COALESCE(NULLIF(@created,''), created_at),
                updated_at=datetime('now','localtime')
            WHERE id=@id";
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
            cmd.Parameters.AddWithValue("@id", m.Id);
            if (cmd.ExecuteNonQuery() == 0) return false;
        }
        if (!string.Equals(from, m.Status, StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = GetMaintenance(m.Id);
            if (snapshot != null) NotifyStatusChanged(snapshot, from, m.Status);
        }
        return true;
    }

    public MaintenanceRecord? GetMaintenance(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, station_id, equipment_model, equipment_sn, fail_item, fail_reason,
            severity, status, resolver, resolution, notes, created_at, updated_at
            FROM maintenance_records WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new MaintenanceRecord
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
            UpdatedAt = r.IsDBNull(12) ? "" : r.GetString(12),
        };
    }

    public bool DeleteMaintenance(int id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        int affected;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM maintenance_records WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            affected = cmd.ExecuteNonQuery();
        }

        if (affected > 0)
        {
            using var seq = conn.CreateCommand();
            seq.Transaction = tx;
            seq.CommandText = @"
                UPDATE sqlite_sequence
                   SET seq = (SELECT COALESCE(MAX(id), 0) FROM maintenance_records)
                 WHERE name = 'maintenance_records'";
            seq.ExecuteNonQuery();
        }

        tx.Commit();
        return affected > 0;
    }

    private const string TodoWatermarkKey = "todo_sync_last_id";

    public const int TodoViewLimit = 300;

    public string? GetMeta(string key)
    {
        using var conn = Open();
        return GetMeta(conn, key);
    }

    public void SetMeta(string key, string value)
    {
        using var conn = Open();
        SetMeta(conn, key, value);
    }

    private static string? GetMeta(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM app_meta WHERE k=@k";
        cmd.Parameters.AddWithValue("@k", key);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : v.ToString();
    }

    private static void SetMeta(SqliteConnection conn, string key, string value, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO app_meta(k,v) VALUES(@k,@v) ON CONFLICT(k) DO UPDATE SET v=@v";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    public int SyncTodoItems(int scanDays = 30)
    {
        if (scanDays < 1) scanDays = 1;
        var cutoff = DateTime.Today.AddDays(-scanDays).ToString("yyyyMMdd");

        using var conn = Open();

        long watermark = 0;
        var wm = GetMeta(conn, TodoWatermarkKey);
        if (wm != null) long.TryParse(wm, out watermark);
        long effectiveWatermark = watermark;

        long maxId;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COALESCE(MAX(id),0) FROM test_records";
            maxId = Convert.ToInt64(c.ExecuteScalar() ?? 0L);
        }

        var groups = new Dictionary<(string, string), TodoAgg>();
        if (maxId > watermark)
        {
            var dismissed = new HashSet<(string, string)>();
            using (var dc = conn.CreateCommand())
            {
                dc.CommandText = "SELECT fail_item, station_id FROM dismissed_todos";
                using var dr = dc.ExecuteReader();
                while (dr.Read()) dismissed.Add((dr.GetString(0), dr.GetString(1)));
            }
            using var c = conn.CreateCommand();
            c.CommandText = @"
                SELECT fail_reason, station_id, COALESCE(model,''),
                       COALESCE(NULLIF(batch_timestamp,''), test_date),
                       test_date
                  FROM test_records
                 WHERE result='FAIL'
                   AND fail_reason IS NOT NULL AND TRIM(fail_reason) <> ''
                   AND id > @wm AND id <= @max";
            c.Parameters.AddWithValue("@wm", watermark);
            c.Parameters.AddWithValue("@max", maxId);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var testDate = r.IsDBNull(4) ? "" : r.GetString(4);
                if (string.CompareOrdinal(testDate, cutoff) < 0) continue;
                var item = r.IsDBNull(0) ? "" : r.GetString(0);
                string key;
                try { key = TodoGrouping.MergeKeyOf(item); }
                catch (Exception ex) { Logger.Warning($"[待办] 合并键计算失败，跳过该项: {ex.Message} | {item}"); continue; }
                if (key.Length == 0) continue;
                var station = r.IsDBNull(1) ? "" : r.GetString(1);
                if (dismissed.Contains((key, station))) continue;
                var model = r.IsDBNull(2) ? "" : r.GetString(2);
                var ts = NormalizeTs(r.IsDBNull(3) ? "" : r.GetString(3));

                if (!groups.TryGetValue((key, station), out var agg))
                    groups[(key, station)] = agg = new TodoAgg { Model = model };
                agg.Count++;
                agg.Variants.Add(item.Trim());
                if (string.IsNullOrEmpty(agg.Model)) agg.Model = model;
                if (ts.Length > 0)
                {
                    if (agg.First.Length == 0 || string.CompareOrdinal(ts, agg.First) < 0) agg.First = ts;
                    if (string.CompareOrdinal(ts, agg.Last) > 0) agg.Last = ts;
                }
            }
        }

        int created = 0;
        using (var tx = conn.BeginTransaction())
        {
            foreach (var ((key, station), agg) in groups)
            {
                string? oldVariants = null;
                int id = 0;
                using (var sel = conn.CreateCommand())
                {
                    sel.Transaction = tx;
                    sel.CommandText = "SELECT id, variants FROM todo_items WHERE group_key=@k AND station_id=@s";
                    sel.Parameters.AddWithValue("@k", key);
                    sel.Parameters.AddWithValue("@s", station);
                    using var rr = sel.ExecuteReader();
                    if (rr.Read())
                    {
                        id = rr.GetInt32(0);
                        oldVariants = rr.IsDBNull(1) ? "" : rr.GetString(1);
                    }
                }

                var variants = new List<string>();
                if (!string.IsNullOrEmpty(oldVariants))
                    variants.AddRange(oldVariants.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                foreach (var v in agg.Variants)
                    if (!variants.Contains(v, StringComparer.OrdinalIgnoreCase)) variants.Add(v);
                if (variants.Count > 40) variants = variants.Take(40).ToList();
                var title = TodoGrouping.TitleOf(variants);
                if (string.IsNullOrEmpty(title)) title = key;

                if (id == 0)
                {
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = @"
                        INSERT INTO todo_items
                            (group_key, station_id, title, model, variants, variant_count,
                             fail_count, first_seen, last_seen, state)
                        VALUES (@k, @s, @t, @m, @vs, @vc, @c, @f, @l, 'pending')";
                    ins.Parameters.AddWithValue("@k", key);
                    ins.Parameters.AddWithValue("@s", station);
                    ins.Parameters.AddWithValue("@t", title);
                    ins.Parameters.AddWithValue("@m", agg.Model);
                    ins.Parameters.AddWithValue("@vs", string.Join("\n", variants));
                    ins.Parameters.AddWithValue("@vc", variants.Count);
                    ins.Parameters.AddWithValue("@c", agg.Count);
                    ins.Parameters.AddWithValue("@f", agg.First);
                    ins.Parameters.AddWithValue("@l", agg.Last);
                    ins.ExecuteNonQuery();
                    created++;
                }
                else
                {
                    using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = @"
                        UPDATE todo_items
                           SET title=@t,
                               model=CASE WHEN COALESCE(model,'')='' THEN @m ELSE model END,
                               variants=@vs, variant_count=@vc,
                               fail_count=fail_count+@c,
                               first_seen=CASE WHEN COALESCE(first_seen,'')='' OR (@f<>'' AND @f<first_seen)
                                               THEN @f ELSE first_seen END,
                               last_seen=CASE WHEN @l>COALESCE(last_seen,'') THEN @l ELSE last_seen END,
                               updated_at=datetime('now','localtime')
                         WHERE id=@id";
                    upd.Parameters.AddWithValue("@t", title);
                    upd.Parameters.AddWithValue("@m", agg.Model);
                    upd.Parameters.AddWithValue("@vs", string.Join("\n", variants));
                    upd.Parameters.AddWithValue("@vc", variants.Count);
                    upd.Parameters.AddWithValue("@c", agg.Count);
                    upd.Parameters.AddWithValue("@f", agg.First);
                    upd.Parameters.AddWithValue("@l", agg.Last);
                    upd.Parameters.AddWithValue("@id", id);
                    upd.ExecuteNonQuery();
                }
            }

            effectiveWatermark = maxId;
            SetMeta(conn, TodoWatermarkKey, effectiveWatermark.ToString(), tx);
            tx.Commit();
        }

        ReconcileTodoStates(conn);
        return created;
    }

    private sealed class TodoAgg
    {
        public int Count;
        public string Model = "";
        public string First = "";
        public string Last = "";
        public readonly List<string> Variants = new();
    }

    internal static string NormalizeTs(string ts) => TimeUtil.Normalize(ts);

    private static void ReconcileTodoStates(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            -- 记录被删 -> 重新变成未确认
            UPDATE todo_items
               SET state='pending', maintenance_id=NULL, resolved_at=NULL,
                   updated_at=datetime('now','localtime')
             WHERE maintenance_id IS NOT NULL
               AND maintenance_id NOT IN (SELECT id FROM maintenance_records);

            -- 记录已完成 -> resolved（resolved_at 取记录的最后更新时间）
            UPDATE todo_items
               SET state='resolved',
                   resolved_at=(SELECT COALESCE(NULLIF(m.updated_at,''), m.created_at)
                                  FROM maintenance_records m WHERE m.id=todo_items.maintenance_id),
                   updated_at=datetime('now','localtime')
             WHERE maintenance_id IS NOT NULL
               AND (SELECT status FROM maintenance_records WHERE id=todo_items.maintenance_id)
                   IN ('resolved','closed');

            -- 记录仍活跃 -> ack
            UPDATE todo_items
               SET state='ack', resolved_at=NULL, updated_at=datetime('now','localtime')
             WHERE maintenance_id IS NOT NULL
               AND (SELECT status FROM maintenance_records WHERE id=todo_items.maintenance_id)
                   NOT IN ('resolved','closed');

            -- 处理完之后又出现新不良 -> 复发，回到未确认
            UPDATE todo_items
               SET state='pending', maintenance_id=NULL, resolved_at=NULL,
                   updated_at=datetime('now','localtime')
             WHERE state='resolved' AND COALESCE(resolved_at,'')<>''
               AND COALESCE(last_seen,'') > resolved_at;
        ";
        cmd.ExecuteNonQuery();
    }

    public List<TodoItem> ListTodoView(DateTime? from = null, DateTime? to = null, int limit = TodoViewLimit)
    {
        using var conn = Open();

        var list = new List<TodoItem>();
        using (var cmd = conn.CreateCommand())
        {
            // 排序与截断下推 SQL（LIMIT），避免全量加载后在内存 Take
            cmd.CommandText = @"
                SELECT id, group_key, station_id, title, COALESCE(model,''), COALESCE(variants,''),
                       variant_count, fail_count, COALESCE(first_seen,''), COALESCE(last_seen,'')
                  FROM todo_items
                 WHERE state='pending'
                 ORDER BY fail_count DESC, last_seen DESC
                 LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var it = new TodoItem
                {
                    Id = r.GetInt32(0),
                    GroupKey = r.GetString(1),
                    StationId = r.GetString(2),
                    Title = r.GetString(3),
                    Model = r.GetString(4),
                    VariantCount = r.GetInt32(6),
                    TotalCount = r.GetInt32(7),
                    FirstSeen = r.GetString(8),
                    LastSeen = r.GetString(9),
                };
                var vs = r.GetString(5);
                if (vs.Length > 0) it.Variants.AddRange(vs.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                list.Add(it);
            }
        }
        if (list.Count == 0) return list;

        if (from != null || to != null)
        {
            var ranged = new Dictionary<(string, string), (int cnt, string first, string last)>();
            using (var cmd = conn.CreateCommand())
            {
                var where = "WHERE result='FAIL' AND fail_reason IS NOT NULL AND TRIM(fail_reason) <> ''";
                if (from != null) { where += " AND test_date >= @a"; }
                if (to != null) { where += " AND test_date <= @b"; }
                cmd.CommandText = $@"
                    SELECT fail_reason, station_id, COUNT(*),
                           MIN(COALESCE(NULLIF(batch_timestamp,''),
                                        substr(test_date,1,4)||'-'||substr(test_date,5,2)||'-'||substr(test_date,7,2)||' 00:00:00')),
                           MAX(COALESCE(NULLIF(batch_timestamp,''),
                                        substr(test_date,1,4)||'-'||substr(test_date,5,2)||'-'||substr(test_date,7,2)||' 00:00:00'))
                      FROM test_records {where}
                     GROUP BY fail_reason, station_id";
                if (from != null) cmd.Parameters.AddWithValue("@a", from.Value.ToString("yyyyMMdd"));
                if (to != null) cmd.Parameters.AddWithValue("@b", to.Value.ToString("yyyyMMdd"));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string key;
                    try { key = TodoGrouping.KeyOf(r.IsDBNull(0) ? "" : r.GetString(0)); }
                    catch (Exception ex) { Logger.Warning($"[待办] 区间统计合并键计算失败，跳过该项: {ex.Message}"); continue; }
                    if (key.Length == 0) continue;
                    var station = r.IsDBNull(1) ? "" : r.GetString(1);
                    var cnt = r.GetInt32(2);
                    var f = NormalizeTs(r.IsDBNull(3) ? "" : r.GetString(3));
                    var l = NormalizeTs(r.IsDBNull(4) ? "" : r.GetString(4));
                    if (ranged.TryGetValue((key, station), out var old))
                        ranged[(key, station)] = (old.cnt + cnt,
                            old.first.Length == 0 || (f.Length > 0 && string.CompareOrdinal(f, old.first) < 0) ? f : old.first,
                            string.CompareOrdinal(l, old.last) > 0 ? l : old.last);
                    else
                        ranged[(key, station)] = (cnt, f, l);
                }
            }

            var kept = new List<TodoItem>();
            foreach (var it in list)
            {
                if (!ranged.TryGetValue((it.GroupKey, it.StationId), out var v)) continue;
                it.RangeCount = v.cnt;
                it.RangeFirstSeen = v.first;
                it.RangeLastSeen = v.last;
                kept.Add(it);
            }
            list = kept;
        }
        else
        {
            foreach (var it in list) it.RangeCount = it.TotalCount;
        }

        // 排序与截断已下推 SQL（ORDER BY fail_count DESC, last_seen DESC + LIMIT @limit），
        // 删除内存截断；区间模式下 RangeCount 可能与累计 fail_count 不同，按 SortCount 复排保持原排序口径（不截断）
        if (from != null || to != null)
        {
            list = list.OrderByDescending(x => x.SortCount)
                       .ThenByDescending(x => x.LastSeen, StringComparer.Ordinal)
                       .ToList();
        }
        return list;
    }

    public int CountPendingTodos()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM todo_items WHERE state='pending'";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    [Obsolete("待办来自真实不良，不得忽略；待办视图已不再读 dismissed_todos。")]
    public void DismissTodo(string failItem, string stationId, string model)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO dismissed_todos(fail_item, station_id, model)
            VALUES (@item, @st, @model)";
        cmd.Parameters.AddWithValue("@item", failItem);
        cmd.Parameters.AddWithValue("@st", stationId);
        cmd.Parameters.AddWithValue("@model", (object?)model ?? "");
        cmd.ExecuteNonQuery();
    }

    public int AcknowledgeTodo(int todoId, MaintenanceRecord rec)
    {
        TodoItem? todo = GetTodoItem(todoId);
        if (todo == null) throw new InvalidOperationException($"待办 #{todoId} 不存在（可能已被处理）");

        if (string.IsNullOrWhiteSpace(rec.StationId)) rec.StationId = todo.StationId;
        if (string.IsNullOrWhiteSpace(rec.FailItem)) rec.FailItem = todo.Title;
        if (string.IsNullOrEmpty(rec.Status)) rec.Status = "open";
        if (string.IsNullOrWhiteSpace(rec.Notes) && todo.VariantCount > 1)
            rec.Notes = TodoGrouping.BuildSourceItemsNote(todo.Variants.Take(20));

        var id = CreateMaintenance(rec);

        rec.Id = id;
        NotifyStatusChanged(rec, "", rec.Status);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE todo_items
               SET state='ack', maintenance_id=@mid, resolved_at=NULL,
                   updated_at=datetime('now','localtime')
             WHERE id=@id";
        cmd.Parameters.AddWithValue("@mid", id);
        cmd.Parameters.AddWithValue("@id", todoId);
        cmd.ExecuteNonQuery();
        return id;
    }

    public int AcknowledgeTodo(int todoId, string resolver, string severity, string status = "open")
    {
        TodoItem? todo = GetTodoItem(todoId);
        if (todo == null) throw new InvalidOperationException($"待办 #{todoId} 不存在（可能已被处理）");

        var reason = todo.VariantCount > 1
            ? $"合并 {todo.VariantCount} 个同类测试项，累计 {todo.TotalCount} 次不良"
            : $"累计 {todo.TotalCount} 次不良";
        var rec = new MaintenanceRecord
        {
            StationId = todo.StationId,
            FailItem = todo.Title,
            FailReason = reason,
            Severity = severity,
            Status = string.IsNullOrEmpty(status) ? "open" : status,
            Resolver = resolver,
            Notes = todo.VariantCount > 1 ? TodoGrouping.BuildSourceItemsNote(todo.Variants.Take(20)) : "",
        };
        return AcknowledgeTodo(todoId, rec);
    }

    public bool DeleteTodo(int todoId)
    {
        using var conn = Open();
        string? key = null, station = null, model = null;
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT group_key, station_id, COALESCE(model,'') FROM todo_items WHERE id=@id";
            sel.Parameters.AddWithValue("@id", todoId);
            using var r = sel.ExecuteReader();
            if (!r.Read()) return false;
            key = r.GetString(0);
            station = r.GetString(1);
            model = r.GetString(2);
        }
        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM todo_items WHERE id=@id";
            del.Parameters.AddWithValue("@id", todoId);
            del.ExecuteNonQuery();
        }
        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(station))
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT OR IGNORE INTO dismissed_todos(fail_item, station_id, model) VALUES(@k, @s, @m)";
            ins.Parameters.AddWithValue("@k", key);
            ins.Parameters.AddWithValue("@s", station);
            ins.Parameters.AddWithValue("@m", (object?)model ?? "");
            ins.ExecuteNonQuery();
        }
        return true;
    }

    public TodoItem? GetTodoItem(int id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, group_key, station_id, title, COALESCE(model,''), COALESCE(variants,''),
                   variant_count, fail_count, COALESCE(first_seen,''), COALESCE(last_seen,''), state
              FROM todo_items WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var it = new TodoItem
        {
            Id = r.GetInt32(0),
            GroupKey = r.GetString(1),
            StationId = r.GetString(2),
            Title = r.GetString(3),
            Model = r.GetString(4),
            VariantCount = r.GetInt32(6),
            TotalCount = r.GetInt32(7),
            FirstSeen = r.GetString(8),
            LastSeen = r.GetString(9),
            State = r.GetString(10),
        };
        var vs = r.GetString(5);
        if (vs.Length > 0) it.Variants.AddRange(vs.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        it.RangeCount = it.TotalCount;
        return it;
    }

    public Dictionary<string, int> CountFailByItems(IEnumerable<string> items, string stationId = "")
    {
        var list = items.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (list.Count == 0) return result;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var names = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            names.Add($"@p{i}");
            cmd.Parameters.AddWithValue($"@p{i}", list[i]);
        }
        var where = $"WHERE result='FAIL' AND fail_reason IN ({string.Join(",", names)})";
        if (!string.IsNullOrEmpty(stationId))
        {
            where += " AND station_id=@st";
            cmd.Parameters.AddWithValue("@st", stationId);
        }
        cmd.CommandText = $"SELECT fail_reason, COUNT(*) FROM test_records {where} GROUP BY fail_reason";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.IsDBNull(0) ? "" : r.GetString(0)] = r.GetInt32(1);
        return result;
    }

    public TodoItem? GetTodoByMaintenance(int maintenanceId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM todo_items WHERE maintenance_id=@id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", maintenanceId);
        var v = cmd.ExecuteScalar();
        if (v == null || v is DBNull) return null;
        return GetTodoItem(Convert.ToInt32(v));
    }

    public void InsertLocalDeviceSample(double cpuUsage, double memUsedPct, double diskFreeGb, string? ts = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO device_samples_local (ts, cpu_usage, mem_used_pct, disk_free_gb)
            VALUES (@ts, @cpu, @mem, @disk);";
        cmd.Parameters.AddWithValue("@ts", string.IsNullOrEmpty(ts) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : ts);
        cmd.Parameters.AddWithValue("@cpu", Math.Round(cpuUsage, 2));
        cmd.Parameters.AddWithValue("@mem", Math.Round(memUsedPct, 2));
        cmd.Parameters.AddWithValue("@disk", Math.Round(diskFreeGb, 2));
        cmd.ExecuteNonQuery();
    }

    public List<(string Ts, double Cpu, double Mem, double DiskFree)> GetLocalDeviceSamples(int days = 7)
    {
        var list = new List<(string, double, double, double)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ts, cpu_usage, mem_used_pct, disk_free_gb
            FROM device_samples_local
            WHERE ts >= datetime('now', 'localtime', @daysModifier)
            ORDER BY ts ASC;";
        cmd.Parameters.AddWithValue("@daysModifier", $"-{Math.Max(1, days)} days");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((
                r.GetString(0),
                r.GetDouble(1),
                r.GetDouble(2),
                r.GetDouble(3)
            ));
        }
        return list;
    }

    public int PurgeOldLocalDeviceSamples(int retentionDays = 14)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM device_samples_local
            WHERE ts < datetime('now', 'localtime', @cutoff);";
        cmd.Parameters.AddWithValue("@cutoff", $"-{Math.Max(1, retentionDays)} days");
        return cmd.ExecuteNonQuery();
    }

    public List<BaselineSourceRecord> FetchBaselineSourceRecords(int windowDays, DateTime? now = null)
    {
        var list = new List<BaselineSourceRecord>();
        var today = (now ?? DateTime.Now).Date;
        var from = today.AddDays(-(Math.Max(1, windowDays) - 1));
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, COALESCE(test_date,''), COALESCE(model,''), COALESCE(sn,''), COALESCE(result,''),
                   COALESCE(batch_timestamp,''), COALESCE(created_at,''), COALESCE(xml_path,'')
            FROM test_records
            WHERE (test_date >= @from8 AND test_date <= @to8)
               OR (test_date >= @fromDash AND test_date <= @toDash)";
        cmd.Parameters.AddWithValue("@from8", from.ToString("yyyyMMdd"));
        cmd.Parameters.AddWithValue("@to8", today.ToString("yyyyMMdd"));
        cmd.Parameters.AddWithValue("@fromDash", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@toDash", today.ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var hour = ExtractHourFromRecord(r.GetString(7), r.GetString(5), r.GetString(6));
            list.Add(new BaselineSourceRecord(
                r.GetInt64(0), r.GetString(1), hour, r.GetString(2), r.GetString(3), r.GetString(4)));
        }
        return list;
    }

    public List<string> FetchDayFailReasons(string dateYmd, DateTime? now = null)
    {
        var list = new List<string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(fail_reason,'')
            FROM test_records
            WHERE result='FAIL' AND (test_date = @d8 OR test_date = @dDash)";
        var d = (now ?? DateTime.Now).Date;
        var target = dateYmd.Length > 0 ? dateYmd : d.ToString("yyyy-MM-dd");
        var alt = target;
        if (DateTime.TryParseExact(target, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt1)) alt = dt1.ToString("yyyyMMdd");
        else if (DateTime.TryParseExact(target, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt2)) alt = dt2.ToString("yyyy-MM-dd");
        cmd.Parameters.AddWithValue("@d8", target.Length == 8 ? target : alt);
        cmd.Parameters.AddWithValue("@dDash", target.Length == 8 ? alt : target);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var s = r.GetString(0);
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
        }
        return list;
    }

    public Dictionary<string, int> CountDismissedByItem()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(fail_item,''), COUNT(*) FROM dismissed_todos GROUP BY fail_item";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var item = r.GetString(0);
            if (item.Length == 0) continue;
            map[item] = r.GetInt32(1);
        }
        return map;
    }

    public int CountFailRecords(string failItem, string stationId)
    {
        if (string.IsNullOrWhiteSpace(failItem)) return 0;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE result='FAIL' AND fail_reason=@item";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id=@st";
        cmd.CommandText = $"SELECT COUNT(*) FROM test_records {where}";
        cmd.Parameters.AddWithValue("@item", failItem);
        if (!string.IsNullOrEmpty(stationId)) cmd.Parameters.AddWithValue("@st", stationId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public List<string> RosterResolvers()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM resolvers ORDER BY name COLLATE NOCASE ASC";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public List<string> ListResolvers(int historyLimit = 30)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var n in RosterResolvers())
            if (seen.Add(n)) result.Add(n);
        foreach (var n in DistinctResolvers(historyLimit))
            if (seen.Add(n)) result.Add(n);
        return result;
    }

    public bool AddResolver(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return false;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO resolvers(name) VALUES(@n)";
        cmd.Parameters.AddWithValue("@n", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteResolver(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return false;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM resolvers WHERE name = @n COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@n", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int RenameResolver(string oldName, string newName, bool syncRecords)
    {
        oldName = (oldName ?? "").Trim();
        newName = (newName ?? "").Trim();
        if (oldName.Length == 0 || newName.Length == 0) return 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        int synced = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                DELETE FROM resolvers WHERE name = @old COLLATE NOCASE
                  AND EXISTS(SELECT 1 FROM resolvers WHERE name = @new COLLATE NOCASE);
                UPDATE resolvers SET name = @new WHERE name = @old COLLATE NOCASE;
                INSERT OR IGNORE INTO resolvers(name) VALUES(@new);";
            cmd.Parameters.AddWithValue("@old", oldName);
            cmd.Parameters.AddWithValue("@new", newName);
            cmd.ExecuteNonQuery();
        }
        if (syncRecords)
        {
            var todo = new List<(int Id, string NewValue)>();
            using (var q = conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = @"SELECT id, resolver FROM maintenance_records
                                   WHERE resolver IS NOT NULL AND TRIM(resolver) <> ''";
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    var field = r.GetString(1);
                    if (!ResolverUtil.Contains(field, oldName)) continue;
                    todo.Add((r.GetInt32(0), ResolverUtil.Replace(field, oldName, newName)));
                }
            }
            foreach (var (id, val) in todo)
            {
                using var up = conn.CreateCommand();
                up.Transaction = tx;
                up.CommandText = @"UPDATE maintenance_records
                                      SET resolver = @v, updated_at = datetime('now','localtime')
                                    WHERE id = @id";
                up.Parameters.AddWithValue("@v", val);
                up.Parameters.AddWithValue("@id", id);
                synced += up.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return synced;
    }

    public int CountRecordsByResolver(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return 0;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT resolver FROM maintenance_records
             WHERE resolver IS NOT NULL AND TRIM(resolver) <> ''";
        int n = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (ResolverUtil.Contains(r.GetString(0), name)) n++;
        return n;
    }

    public List<string> DistinctResolvers(int limit = 30)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT resolver FROM maintenance_records
             WHERE resolver IS NOT NULL AND TRIM(resolver) <> ''";
        var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                foreach (var who in ResolverUtil.Split(r.GetString(0)))
                    count[who] = count.GetValueOrDefault(who) + 1;

        return count.OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(limit).Select(kv => kv.Key).ToList();
    }

    public List<FailItemSource> FailItemSources(string stationId = "", int days = 0, int limit = 2000)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE result='FAIL'";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id=@s";
        string? cutoff = null;
        if (days > 0) { cutoff = DateTime.Today.AddDays(-days).ToString("yyyyMMdd"); where += " AND test_date >= @c"; }
        cmd.CommandText = $@"
            SELECT COALESCE(fail_reason,''), COALESCE(model,''), COALESCE(station_id,''),
                   COALESCE(batch_timestamp,''), COALESCE(test_date,''), COALESCE(xml_path,'')
              FROM test_records {where}
             ORDER BY id DESC
             LIMIT @lim";
        if (!string.IsNullOrEmpty(stationId)) cmd.Parameters.AddWithValue("@s", stationId);
        if (cutoff != null) cmd.Parameters.AddWithValue("@c", cutoff);
        cmd.Parameters.AddWithValue("@lim", limit);
        var list = new List<FailItemSource>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new FailItemSource
            {
                FirstFailItem = r.GetString(0),
                Model = r.GetString(1),
                StationId = r.GetString(2),
                Timestamp = r.GetString(3),
                TestDate = r.GetString(4),
                XmlPath = r.GetString(5),
            });
        return list;
    }

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

    public List<(TestRecord Rec, long Id)> FetchFailRecordsAfter(long afterId, int limit = 5000)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, station_id, model, category, test_date, sn, result, xml_path,
                   fail_reason, tester, panel_status, batch_timestamp, has_fail_items, file_size, fixture_id
              FROM test_records WHERE result='FAIL' AND id > @a ORDER BY id ASC LIMIT @n";
        cmd.Parameters.AddWithValue("@a", afterId);
        cmd.Parameters.AddWithValue("@n", limit);
        var list = new List<(TestRecord, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rec = new TestRecord
            {
                StationId = r.GetString(1),
                Model = r.IsDBNull(2) ? "" : r.GetString(2),
                Category = r.IsDBNull(3) ? "" : r.GetString(3),
                TestDate = r.IsDBNull(4) ? "" : r.GetString(4),
                Sn = r.IsDBNull(5) ? null : r.GetString(5),
                Result = r.IsDBNull(6) ? "" : r.GetString(6),
                XmlPath = r.IsDBNull(7) ? "" : r.GetString(7),
                FailReason = r.IsDBNull(8) ? null : r.GetString(8),
                Tester = r.IsDBNull(9) ? null : r.GetString(9),
                PanelStatus = r.IsDBNull(10) ? null : r.GetString(10),
                BatchTimestamp = r.IsDBNull(11) ? null : r.GetString(11),
                HasFailItems = !r.IsDBNull(12) && r.GetInt32(12) != 0,
                FileSize = r.IsDBNull(13) ? null : r.GetInt64(13),
                FixtureId = r.IsDBNull(14) ? null : r.GetString(14),
            };
            list.Add((rec, r.GetInt64(0)));
        }
        return list;
    }

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
    public int TodayProductCount;
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

public class FailItemSource
{
    public string FirstFailItem = "";
    public string Model = "";
    public string StationId = "";
    public string Timestamp = "";
    public string TestDate = "";
    public string XmlPath = "";
}

public class TodoItem
{
    public int Id;
    public string GroupKey = "";
    public string Title = "";
    public string StationId = "";
    public string Model = "";
    public readonly List<string> Variants = new();
    public int VariantCount = 1;
    public int TotalCount;
    public int RangeCount;
    public int SortCount => RangeCount > 0 ? RangeCount : TotalCount;
    public string FirstSeen = "";
    public string LastSeen = "";
    public string RangeFirstSeen = "";
    public string RangeLastSeen = "";
    public string State = "pending";

    public string PriorityZh => TodoGrouping.PriorityZhOf(SortCount);
}

public class MaintenanceRecord
{
    public int Id;
    public string StationId = "";
    public string EquipmentModel = "";
    public string EquipmentSn = "";
    public string FailItem = "";
    public string FailReason = "";
    public string Severity = "major";
    public string Status = "open";
    public string Resolver = "";
    public string Resolution = "";
    public string Notes = "";
    public string CreatedAt = "";
    public string UpdatedAt = "";

    public MaintenanceRecord Clone() => (MaintenanceRecord)MemberwiseClone();
}

public sealed class TodoSyncRow
{
    public string OriginMachine = "";
    public long TodoId;
    public string? Owner;
    public string State = "";
    public long Version;
    public string UpdatedAt = "";
}

public sealed partial class Database
{
    public long BumpTodoVersion(long todoId)
    {
        var key = $"todo_ver_{todoId}";
        long v = 0;
        using (var conn = Open())
        {
            var cur = GetMeta(conn, key);
            if (cur != null) long.TryParse(cur, out v);
        }
        v++;
        using (var conn = Open()) SetMeta(conn, key, v.ToString());
        return v;
    }

    public void ApplyRemoteTodoEvent(TodoEvent ev)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            long localVer = 0; string? localOwner = null;
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT version, owner FROM todo_sync_state WHERE origin_machine=@m AND todo_id=@t";
                sel.Parameters.AddWithValue("@m", ev.OriginMachine);
                sel.Parameters.AddWithValue("@t", ev.TodoId);
                using var r = sel.ExecuteReader();
                if (r.Read())
                {
                    localVer = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                    localOwner = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }
            bool accept = ev.Version > localVer ||
                          (ev.Version == localVer && string.IsNullOrEmpty(localOwner) && !string.IsNullOrEmpty(ev.Owner));
            if (!accept) { tx.Commit(); return; }

            using var ups = conn.CreateCommand();
            ups.Transaction = tx;
            ups.CommandText = @"
                INSERT INTO todo_sync_state (origin_machine, todo_id, owner, state, version, updated_at)
                VALUES (@m,@t,@owner,@state,@ver,@upd)
                ON CONFLICT(origin_machine, todo_id)
                DO UPDATE SET owner=excluded.owner, state=excluded.state,
                              version=excluded.version, updated_at=excluded.updated_at";
            ups.Parameters.AddWithValue("@m", ev.OriginMachine);
            ups.Parameters.AddWithValue("@t", ev.TodoId);
            ups.Parameters.AddWithValue("@owner", (object?)ev.Owner ?? DBNull.Value);
            ups.Parameters.AddWithValue("@state", ev.State);
            ups.Parameters.AddWithValue("@ver", ev.Version);
            ups.Parameters.AddWithValue("@upd", ev.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            ups.ExecuteNonQuery();
            tx.Commit();
            Logger.Info($"[待办同步] 已合并远端事件 {ev.OriginMachine}#{ev.TodoId} v{ev.Version}（owner={ev.Owner ?? "∅"}）");
        }
        catch (Exception ex) { Logger.Warning($"[待办同步] 应用远端事件失败: {ex.Message}"); }
    }

    public List<MeshQueryService.QueryItem> QueryTestRecords(string localMachine, MeshQueryService.QueryRequest req)
    {
        var where = new List<string>();
        var ps = new List<(string name, object val)>();
        if (!string.IsNullOrWhiteSpace(req.Machine))
        {
            where.Add("station_id = @machine");
            ps.Add(("@machine", req.Machine!.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(req.Sn))
        {
            where.Add("sn LIKE @sn");
            ps.Add(("@sn", $"%{req.Sn!.Trim()}%"));
        }
        if (!string.IsNullOrWhiteSpace(req.Model))
        {
            where.Add("model LIKE @model");
            ps.Add(("@model", $"%{req.Model!.Trim()}%"));
        }
        if (!string.IsNullOrWhiteSpace(req.Result) && !string.Equals(req.Result.Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
        {
            var r = req.Result!.Trim().ToUpperInvariant();
            if (r == "PASS" || r == "FAIL" || r == "INTERRUPTED" || r == "INVALID")
            {
                where.Add("result = @result");
                ps.Add(("@result", r));
            }
        }
        var df = NormalizeQueryDate(req.DateFrom);
        var dt = NormalizeQueryDate(req.DateTo);
        if (!string.IsNullOrEmpty(df)) { where.Add("test_date >= @df"); ps.Add(("@df", df)); }
        if (!string.IsNullOrEmpty(dt)) { where.Add("test_date <= @dt"); ps.Add(("@dt", dt)); }
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var limit = Math.Clamp(req.Limit <= 0 ? 100 : req.Limit, 1, 2000);
        var offset = Math.Max(0, req.Offset);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT id, station_id, sn, model, result, test_date, fail_reason, xml_path, file_size, batch_timestamp, tester
              FROM test_records {whereSql}
             ORDER BY test_date DESC, id DESC
             LIMIT @lim OFFSET @off";
        foreach (var (name, val) in ps) cmd.Parameters.AddWithValue(name, val);
        cmd.Parameters.AddWithValue("@lim", limit);
        cmd.Parameters.AddWithValue("@off", offset);
        var list = new List<MeshQueryService.QueryItem>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            list.Add(new MeshQueryService.QueryItem
            {
                Machine = localMachine,
                Id = rdr.GetInt64(0),
                Sn = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                Model = rdr.IsDBNull(3) ? "" : rdr.GetString(3),
                Result = rdr.IsDBNull(4) ? "" : rdr.GetString(4),
                TestDate = rdr.IsDBNull(5) ? "" : rdr.GetString(5),
                FailReason = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                XmlPath = rdr.IsDBNull(7) ? "" : rdr.GetString(7),
                FileSize = rdr.IsDBNull(8) ? 0 : rdr.GetInt64(8),
                BatchTimestamp = rdr.IsDBNull(9) ? "" : rdr.GetString(9),
                Tester = rdr.IsDBNull(10) ? "" : rdr.GetString(10),
            });
        }
        return list;
    }

    private static string NormalizeQueryDate(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return "";
        var s = d.Trim().Replace("-", "").Replace("/", "");
        if (s.Length >= 8) return s.Substring(0, 8);
        return s;
    }

    public List<TodoSyncRow> GetTodoSyncStates()
    {
        var list = new List<TodoSyncRow>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT origin_machine, todo_id, owner, state, version, updated_at FROM todo_sync_state ORDER BY updated_at DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TodoSyncRow
                {
                    OriginMachine = r.IsDBNull(0) ? "" : r.GetString(0),
                    TodoId = r.IsDBNull(1) ? 0 : r.GetInt64(1),
                    Owner = r.IsDBNull(2) ? null : r.GetString(2),
                    State = r.IsDBNull(3) ? "" : r.GetString(3),
                    Version = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                    UpdatedAt = r.IsDBNull(5) ? "" : r.GetString(5),
                });
        }
        catch (Exception ex) { Logger.Warning($"[待办同步] 查询失败: {ex.Message}"); }
        return list;
    }
}

public class HourlyStatItem
{
    public int Hour { get; set; }
    public int Pass { get; set; }
    public int Fail { get; set; }
    public int Total => Pass + Fail;
    public double YieldRate => Total > 0 ? (double)Pass / Total * 100.0 : 0.0;
}

public class TopFailItem
{
    public string FailItem { get; set; } = "";
    public int Count { get; set; }
    public double Ratio { get; set; }
    public string MainStation { get; set; } = "";
    public string RootCauseHint { get; set; } = "";
}

public sealed record BaselineSourceRecord(long Id, string TestDate, int Hour, string Model, string Sn, string Result);

public class LiveFailAlert
{
    public long Id { get; set; }
    public string Sn { get; set; } = "";
    public string StationId { get; set; } = "";
    public string Model { get; set; } = "";
    public string FailReason { get; set; } = "";
    public string Tester { get; set; } = "";
    public string TimeText { get; set; } = "";
    public string XmlPath { get; set; } = "";
}

