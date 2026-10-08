using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FctAggregator;

public sealed class StorageInventory
{
    public long TestRecords;
    public long Measurements;
    public long FailItems;
    public long TdmsFeatures;
    public long NormalModels;
    public long DeviationEvents;
    public long DeviceSamples;
    public long ParseFailures;
    public long SlowLogs;
}

public sealed class DbPageStats
{
    public long PageCount;
    public long FreePages;
    public double BloatRatio => PageCount > 0 ? (double)FreePages / PageCount : 0;
}

public sealed class StorageStats
{
    public long FileBytes;
    public long WalBytes;
    public long TestRecords;
    public long Measurements;
    public long FailItems;
    public long TdmsFeatures;
    public bool SlimSchema;
    public string Format()
    {
        static string Gb(long b) => b >= 1_000_000_000 ? $"{b / 1_000_000_000.0:F2} GB" : $"{b / 1_000_000.0:F1} MB";
        return $"库文件 {Gb(FileBytes)}" + (WalBytes > 0 ? $"  WAL {Gb(WalBytes)}" : "") +
               $"  主记录 {TestRecords:N0}  测量 {Measurements:N0}  失败项 {FailItems:N0}  TDMS {TdmsFeatures:N0}" +
               (SlimSchema ? "  结构=瘦表" : "  结构=含路径冗余");
    }
}

public sealed class CompactReport
{
    public long BytesBefore;
    public long BytesAfter;
    public bool Rebuilt;
    public bool Vacuumed;
    public string Message = "";
}

public sealed partial class Database
{
    public const string StorageSchemaMeta = "storage_schema";
    public const string StorageSchemaSlim = "slim_v1";

    /// <summary>test_date（yyyyMMdd 或 yyyy-MM-dd）是否落在保留窗内；日期无法解析时放行，避免漏采当天。</summary>
    public static bool IsWithinRetention(string? testDate, int retentionDays)
    {
        if (retentionDays <= 0) return true;
        var raw = (testDate ?? "").Replace("-", "").Trim();
        if (raw.Length < 8) return true;
        if (!DateTime.TryParseExact(raw[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return true;
        return d.Date >= DateTime.Today.AddDays(-Math.Max(1, retentionDays));
    }

    public StorageInventory GetStorageInventory()
    {
        var inv = new StorageInventory();
        using var conn = Open();
        inv.TestRecords = ScalarCount(conn, "SELECT COUNT(*) FROM test_records");
        inv.Measurements = ScalarCount(conn, "SELECT COUNT(*) FROM test_measurements");
        inv.FailItems = ScalarCount(conn, "SELECT COUNT(*) FROM fail_items");
        inv.TdmsFeatures = ScalarCount(conn, "SELECT COUNT(*) FROM tdms_features");
        inv.NormalModels = TryScalarCount(conn, "SELECT COUNT(*) FROM normal_models");
        inv.DeviationEvents = TryScalarCount(conn, "SELECT COUNT(*) FROM deviation_events");
        inv.DeviceSamples = TryScalarCount(conn, "SELECT COUNT(*) FROM device_samples_local");
        inv.ParseFailures = ScalarCount(conn, "SELECT COUNT(*) FROM parse_failure_log");
        inv.SlowLogs = TryScalarCount(conn, "SELECT COUNT(*) FROM db_slow_log");
        return inv;
    }

    public DbPageStats GetPageStats()
    {
        var p = new DbPageStats();
        using var conn = Open();
        p.PageCount = ScalarPragmaLong(conn, "page_count");
        p.FreePages = ScalarPragmaLong(conn, "freelist_count");
        return p;
    }

    public int PurgeOldParseFailures(int retentionDays = 30)
    {
        var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, retentionDays)).ToString("yyyy-MM-dd");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM parse_failure_log WHERE substr(COALESCE(created_at,''),1,10) < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    public int PurgeOldSlowLogs(int retentionDays = 14)
    {
        var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, retentionDays)).ToString("yyyy-MM-dd");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM db_slow_log WHERE substr(COALESCE(ts,''),1,10) < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    public StorageStats GetStorageStats()
    {
        var s = new StorageStats();
        try
        {
            if (File.Exists(_dbPath)) s.FileBytes = new FileInfo(_dbPath).Length;
            var wal = _dbPath + "-wal";
            if (File.Exists(wal)) s.WalBytes = new FileInfo(wal).Length;
        }
        catch { }
        using var conn = Open();
        s.TestRecords = ScalarCount(conn, "SELECT COUNT(*) FROM test_records");
        s.Measurements = ScalarCount(conn, "SELECT COUNT(*) FROM test_measurements");
        s.FailItems = ScalarCount(conn, "SELECT COUNT(*) FROM fail_items");
        s.TdmsFeatures = ScalarCount(conn, "SELECT COUNT(*) FROM tdms_features");
        // 审计修复：原实现只查 test_measurements——半成品库（部分表已瘦）会被判为「已是瘦表」，
        // CompactStorage 永不再执行，冗余列永久保留。
        s.SlimSchema = !HasColumn(conn, "test_measurements", "xml_path")
                    && !HasColumn(conn, "fail_items", "xml_path")
                    && !HasColumn(conn, "tdms_features", "xml_path");
        return s;
    }

    /// <summary>清过期明细、去掉每行重复的路径/机台/型号/SN，VACUUM 回收文件空洞。</summary>
    public CompactReport CompactStorage(int retentionDays, bool vacuum = true, int failItemRetentionDays = 0)
    {
        var report = new CompactReport();
        try { if (File.Exists(_dbPath)) report.BytesBefore = new FileInfo(_dbPath).Length; } catch { }
        // 钳制与 LearnPipeline.EffectiveMeasureRetentionDays 同源：下限 7、上限 365。
        // 曾硬钳 180：现场把保留期配到 365 时，深度优化会用 180 口径多删 180~365 天测量/失败明细。
        retentionDays = Math.Clamp(retentionDays, LearnPipeline.MeasureRetentionMin, LearnPipeline.MeasureRetentionMax);
        // 审计修复：fail_items 有独立保留期键（analyze_failitem_retention_days），不能沿用测量口径。
        // 原实现三类明细共用 retainM——现场 failitem=180 / measure=30 时会把 30~180 天的失败证据删掉，
        // 且这次删除量不计入 StorageOptimizeResult，日志少报。
        int fiRetention = failItemRetentionDays > 0
            ? Math.Clamp(failItemRetentionDays, LearnPipeline.MeasureRetentionMin, LearnPipeline.MeasureRetentionMax)
            : retentionDays;

        PurgeOldMeasurements(retentionDays);
        PurgeOldFailItems(fiRetention);
        PurgeOldTdmsFeatures(retentionDays);

        using (var conn = Open())
        {
            bool rebuilt = false;
            rebuilt |= RebuildSlimMeasurements(conn);
            rebuilt |= RebuildSlimFailItems(conn);
            rebuilt |= RebuildSlimTdms(conn);
            report.Rebuilt = rebuilt;
        }
        // 审计修复：瘦表转换事务已提交，必须立刻复位标志。原实现放在方法末尾——vacuum:true 时要等
        // 数分钟 VACUUM 结束才复位，窗口内并发入库会拼出引用 xml_path 等已删列的胖表 SQL，
        // 异常被 catch 成一条 Warning（"不影响主记录"）→ 整批测量值/失败项/TDMS 静默丢失。
        _detailTablesFat = false;

        if (vacuum)
        {
            Logger.Info("[存储] 开始 VACUUM 回收磁盘（大库可能需数分钟）…");
            Vacuum();
            report.Vacuumed = true;
        }
        SetMeta(StorageSchemaMeta, StorageSchemaSlim);
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) report.BytesAfter = new FileInfo(_dbPath).Length; } catch { }
            report.Message = $"压缩前 {ByteUtil.MbGb(report.BytesBefore)} → 压缩后 {ByteUtil.MbGb(report.BytesAfter)}";
        Logger.Info($"[存储] {report.Message}");
        return report;
    }

    public void Vacuum()
    {
        SqliteConnection.ClearAllPools();
        using var conn = Open();
        using (var busy = conn.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout = 600000";
            busy.ExecuteNonQuery();
        }
        using (var av = conn.CreateCommand())
        {
            av.CommandText = "PRAGMA auto_vacuum = INCREMENTAL";
            av.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 600;
        cmd.CommandText = "VACUUM";
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    // B3：整组 DDL（CREATE/INSERT…SELECT/DROP/RENAME/INDEX）包进单事务——
    // 原实现每条自动提交，DROP 与 RENAME 之间崩溃只剩 test_measurements_slim 空表，Init 另建空表后数据不可达。
    private bool RebuildSlimMeasurements(SqliteConnection conn)
    {
        if (!HasColumn(conn, "test_measurements", "xml_path")) return false;
        using var tx = conn.BeginTransaction();
        // 上次重建在 CREATE 之后中断时，半成品表已提交。胖表仍是数据源，先丢掉半成品再重建。
        Exec(conn, "DROP TABLE IF EXISTS test_measurements_slim", tx);
        Exec(conn, @"
            CREATE TABLE test_measurements_slim (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                record_id INTEGER NOT NULL,
                ts TEXT,
                test_name TEXT NOT NULL,
                value REAL,
                value_text TEXT,
                lolim REAL,
                hilim REAL,
                unit TEXT,
                rule TEXT,
                UNIQUE(record_id, test_name)
            )", tx);
        Exec(conn, @"
            INSERT OR IGNORE INTO test_measurements_slim
                (record_id, ts, test_name, value, value_text, lolim, hilim, unit, rule)
            SELECT record_id, ts, test_name, value, value_text, lolim, hilim, unit, rule
            FROM test_measurements", tx);
        Exec(conn, "DROP TABLE test_measurements", tx);
        Exec(conn, "ALTER TABLE test_measurements_slim RENAME TO test_measurements", tx);
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_tm_name_ts ON test_measurements(test_name, ts)", tx);
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_tm_ts ON test_measurements(ts)", tx);
        TryExec(conn, "DROP INDEX IF EXISTS idx_tm_daystr", tx);
        tx.Commit();
        Logger.Info("[存储] test_measurements 已去掉 xml_path/station/model/sn/test_date 冗余列");
        return true;
    }

    private bool RebuildSlimFailItems(SqliteConnection conn)
    {
        if (!HasColumn(conn, "fail_items", "xml_path")) return false;
        using var tx = conn.BeginTransaction();
        Exec(conn, "DROP TABLE IF EXISTS fail_items_slim", tx);
        Exec(conn, @"
            CREATE TABLE fail_items_slim (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                record_id INTEGER NOT NULL,
                ts TEXT,
                fixture_id TEXT,
                tester TEXT,
                hour INTEGER,
                test_name TEXT NOT NULL,
                value REAL,
                value_text TEXT,
                lolim REAL,
                hilim REAL,
                unit TEXT,
                rule TEXT,
                UNIQUE(record_id, test_name)
            )", tx);
        Exec(conn, @"
            INSERT OR IGNORE INTO fail_items_slim
                (record_id, ts, fixture_id, tester, hour, test_name, value, value_text, lolim, hilim, unit, rule)
            SELECT record_id, ts, fixture_id, tester, hour, test_name, value, value_text, lolim, hilim, unit, rule
            FROM fail_items", tx);
        Exec(conn, "DROP TABLE fail_items", tx);
        Exec(conn, "ALTER TABLE fail_items_slim RENAME TO fail_items", tx);
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_fi_name_ts ON fail_items(test_name, ts)", tx);
        TryExec(conn, "DROP INDEX IF EXISTS idx_fi_daystr", tx);
        TryExec(conn, "DROP INDEX IF EXISTS idx_fi_ts", tx);
        TryExec(conn, "DROP INDEX IF EXISTS idx_fi_fixture_ts", tx);
        TryExec(conn, "DROP INDEX IF EXISTS idx_fi_tester_ts", tx);
        tx.Commit();
        Logger.Info("[存储] fail_items 已去掉路径冗余列");
        return true;
    }

    private bool RebuildSlimTdms(SqliteConnection conn)
    {
        if (!HasColumn(conn, "tdms_features", "xml_path")) return false;
        using var tx = conn.BeginTransaction();
        Exec(conn, "DROP TABLE IF EXISTS tdms_features_slim", tx);
        Exec(conn, @"
            CREATE TABLE tdms_features_slim (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                record_id INTEGER NOT NULL,
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
            )", tx);
        Exec(conn, @"
            INSERT OR IGNORE INTO tdms_features_slim
                (record_id, ts, tdms_path, group_name, channel_name, section, n, vmin, vmax, vmean, vstd, vfirst, vlast)
            SELECT record_id, ts, tdms_path, group_name, channel_name, section, n, vmin, vmax, vmean, vstd, vfirst, vlast
            FROM tdms_features", tx);
        Exec(conn, "DROP TABLE tdms_features", tx);
        Exec(conn, "ALTER TABLE tdms_features_slim RENAME TO tdms_features", tx);
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_tf_ts ON tdms_features(ts)", tx);
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_tf_channel_ts ON tdms_features(group_name, channel_name, ts)", tx);
        TryExec(conn, "DROP INDEX IF EXISTS idx_tf_daystr", tx);
        TryExec(conn, "DROP INDEX IF EXISTS idx_tf_model_ts", tx);
        tx.Commit();
        Logger.Info("[存储] tdms_features 已去掉路径冗余列");
        return true;
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static long ScalarCount(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long TryScalarCount(SqliteConnection conn, string sql)
    {
        try { return ScalarCount(conn, sql); } catch { return 0; }
    }

    private static long ScalarPragmaLong(SqliteConnection conn, string pragma)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma}";
        var o = cmd.ExecuteScalar();
        return o == null || o is DBNull ? 0 : Convert.ToInt64(o);
    }

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.CommandTimeout = 600;
        cmd.ExecuteNonQuery();
    }

    private static void TryExec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        try { Exec(conn, sql, tx); } catch { }
    }
}
