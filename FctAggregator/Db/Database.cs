using Microsoft.Data.Sqlite;

namespace FctAggregator;

public sealed partial class Database
{
    private readonly string _connString;
    private readonly string _dbPath;
    /// <summary>精确匹配一天（yyyyMMdd 或 yyyy-MM-dd）。参数 @d8 / @dDash。</summary>
    public const string TestDateEqDay = "(test_date = @d8 OR test_date = @dDash)";
    /// <summary>闭区间双格式（参数 @from8/@to8/@fromDash/@toDash）。</summary>
    public const string TestDateRangeClosed =
        "((test_date >= @from8 AND test_date <= @to8) OR (test_date >= @fromDash AND test_date <= @toDash))";
    /// <summary>开区间下界，必须归一化：裸 >= 在 dash 与 8 位之间会比反。</summary>
    public const string TestDateGeNorm =
        "length(replace(test_date,'-',''))=8 AND replace(test_date,'-','') >= @c";
    public const string RTestDateRangeClosed =
        "((r.test_date >= @from8 AND r.test_date <= @to8) OR (r.test_date >= @fromDash AND r.test_date <= @toDash))";

    public static (string Ymd8, string Dash) SplitDay(string? dateYmd)
    {
        var raw = (dateYmd ?? "").Replace("-", "").Trim();
        if (raw.Length >= 8 && DateTime.TryParseExact(raw[..8], "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
            return (d.ToString("yyyyMMdd"), d.ToString("yyyy-MM-dd"));
        return (dateYmd ?? "", dateYmd ?? "");
    }

    public static string NormalizeYmdKey(string? testDate)
    {
        var raw = (testDate ?? "").Replace("-", "").Trim();
        return raw.Length >= 8 ? raw[..8] : raw;
    }

    private static void BindDayDual(SqliteCommand cmd, string dateYmd)
    {
        var (d8, dash) = SplitDay(dateYmd);
        cmd.Parameters.AddWithValue("@d8", d8);
        cmd.Parameters.AddWithValue("@dDash", dash);
    }

    private static void BindRangeDual(SqliteCommand cmd, string fromYmd, string toYmdInclusive)
    {
        var (f8, fDash) = SplitDay(fromYmd);
        var (t8, tDash) = SplitDay(toYmdInclusive);
        cmd.Parameters.AddWithValue("@from8", f8);
        cmd.Parameters.AddWithValue("@to8", t8);
        cmd.Parameters.AddWithValue("@fromDash", fDash);
        cmd.Parameters.AddWithValue("@toDash", tDash);
    }

    private volatile bool _detailTablesFat;

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

    public string DbPath => _dbPath;

    public Database(string dbPath)
        : this(dbPath, setAsCurrent: true)
    {
    }

    /// <summary>次级打开：与主构造一致，但**不改写全局 Current**。
    /// 审计：UpdateChecker 只为读写一个 app_meta 键就 new Database(...)，原来会把 Current 顶成临时实例，
    /// 污染其它模块拿到的"当前库"。</summary>
    public Database(string dbPath, bool setAsCurrent)
    {
        _dbPath = dbPath;
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        Init();
        if (setAsCurrent) Current = this;
    }

    /// <summary>每连接执行的 PRAGMA（WAL + 写性能 + 读缓存）。synchronous=NORMAL 为 WAL 下官方推荐组合，
    /// 断电最多丢最近几条已提交写入，防库损坏能力不变；cache_size/temp_store 纯读加速。常量化供 selftest 断言。
    /// B7：busy_timeout 5s→30s——深度优化 VACUUM 持写锁可达数分钟，写入侧只等 5s 就 SQLITE_BUSY，
    /// 历史扫描批次重试一次即放弃整批（漏采）。30s 覆盖绝大多数锁竞争；VACUUM 期间的停顿由后台线程承担。</summary>
    internal const string ConnectionPragmas =
        "PRAGMA busy_timeout = 30000; " +
        "PRAGMA journal_mode = WAL; " +
        "PRAGMA synchronous = NORMAL; " +
        "PRAGMA cache_size = -20000; " +
        "PRAGMA temp_store = MEMORY;";

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = ConnectionPragmas;
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
                -- v3.36.0 性能：看板/月度聚合常用 (test_date + result) 组合，复合索引避免回表
                CREATE INDEX IF NOT EXISTS idx_tr_date_result ON test_records(test_date, result);
            ";
            cmd.ExecuteNonQuery();

            using (var cmdFix = conn.CreateCommand())
            {
                cmdFix.CommandText = "ALTER TABLE test_records ADD COLUMN fixture_id TEXT;";
                try { cmdFix.ExecuteNonQuery(); }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
            }
            using (var cmdAlert = conn.CreateCommand())
            {
                cmdAlert.CommandText = "ALTER TABLE test_records ADD COLUMN fail_alerted INTEGER NOT NULL DEFAULT 0;";
                try { cmdAlert.ExecuteNonQuery(); }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
            }
            using (var cmdCycle = conn.CreateCommand())
            {
                cmdCycle.CommandText = @"
                    CREATE TABLE IF NOT EXISTS test_cycle (
                        xml_path TEXT PRIMARY KEY,
                        seconds REAL
                    );";
                cmdCycle.ExecuteNonQuery();
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
                -- 审计修复：就地更新（UPDATE，id 不变）的 FAIL 记录进不了「id > 水位」的待办增量扫描，
                -- 导致「告警有、待办无」。BatchUpsert 更新 FAIL 行时把 id 记这里，SyncTodoItems 消费后清空。
                CREATE TABLE IF NOT EXISTS todo_rescan_ids (
                    id INTEGER PRIMARY KEY
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
                );
                CREATE INDEX IF NOT EXISTS idx_fi_name_ts ON fail_items(test_name, ts);
            ";
            cmd8.ExecuteNonQuery();

            // 规格06期3：TDMS 特征化（每通道基础统计，analyze_tdms_enabled 默认关）
            using var cmd9 = conn.CreateCommand();
            cmd9.CommandText = @"
                CREATE TABLE IF NOT EXISTS tdms_features (
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
                );
                CREATE INDEX IF NOT EXISTS idx_tf_ts ON tdms_features(ts);
                CREATE INDEX IF NOT EXISTS idx_tf_channel_ts ON tdms_features(group_name, channel_name, ts);
            ";
            cmd9.ExecuteNonQuery();

            // v3.40.0：Seq 代码管理（变更监控 / 基线对比）已整块移除，把旧库里遗留的变更记录表一并清掉。
            // 幂等：首次启动真正删表，之后 DROP IF EXISTS 是 no-op。
            using (var cmdSeq = conn.CreateCommand())
            {
                cmdSeq.CommandText = "DROP TABLE IF EXISTS fct_seq_change_log";
                cmdSeq.ExecuteNonQuery();
            }

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

            using var cmd11 = conn.CreateCommand();
            cmd11.CommandText = @"
                CREATE TABLE IF NOT EXISTS deviation_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    model TEXT NOT NULL,
                    source TEXT NOT NULL,
                    signal_count INTEGER NOT NULL,
                    top_signal TEXT NOT NULL,
                    top_score REAL NOT NULL,
                    detail_json TEXT NOT NULL,
                    seen INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_de_ts ON deviation_events(ts);
            ";
            cmd11.ExecuteNonQuery();

            // 审计 M13：归因统计四条查询以 substr(...) 过滤，非 sargable 全表扫描×4（维护窗口拉长）——
            // 补虚拟生成列 batch_ts_date + 索引（幂等；虚拟列不回填历史，老库零成本升级）。
            // 幂等用 try/catch 吞 duplicate（pragma_table_info 表值函数在重复 Init 场景下判定不稳，实测会误报 0）
            try
            {
                using var cmdTsDate = conn.CreateCommand();
                cmdTsDate.CommandText = "ALTER TABLE test_records ADD COLUMN batch_ts_date TEXT GENERATED ALWAYS AS (substr(COALESCE(NULLIF(batch_timestamp,''),created_at),1,10)) VIRTUAL";
                cmdTsDate.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column"))
            {
                // 老库已升级过，幂等跳过
            }
            using var cmdIdxTsDate = conn.CreateCommand();
            cmdIdxTsDate.CommandText = "CREATE INDEX IF NOT EXISTS idx_tr_tsdate ON test_records(batch_ts_date)";
            cmdIdxTsDate.ExecuteNonQuery();
            // 审计修复：v3.48.4 前每条 DDL 自动提交，瘦表重建中途崩溃会留下「部分表已瘦、部分仍胖」的库。
            // 原实现只看 test_measurements，于是 fail_items/tdms_features 永久保留冗余列、存储优化被判「已完成」
            // 而静默失效。这里做幂等自愈：任一张仍是胖表就补齐重建（各表独立单事务）。
            try
            {
                if (HasColumn(conn, "test_measurements", "xml_path")
                    || HasColumn(conn, "fail_items", "xml_path")
                    || HasColumn(conn, "tdms_features", "xml_path"))
                {
                    bool healed = false;
                    healed |= RebuildSlimMeasurements(conn);
                    healed |= RebuildSlimFailItems(conn);
                    healed |= RebuildSlimTdms(conn);
                    if (healed) Logger.Info("[存储] 启动自愈：已补齐半成品库的瘦表转换（去路径冗余列）");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[存储] 启动自愈瘦表失败，程序继续用现表: {ex.Message}");
            }
            _detailTablesFat = HasColumn(conn, "test_measurements", "xml_path");
            if (_detailTablesFat)
                Logger.Warning("[存储] 明细表仍含路径冗余列。请到调试工具「压缩数据库」，或等凌晨维护自动压缩。");
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
                // B8：状态终态字面量收口 MaintenanceMeta（LegacyClosed/DoneStatus），Meta 增删终态时自动同步
                cmd.CommandText = "SELECT COUNT(*) FROM maintenance_records WHERE status=@legacy";
                cmd.Parameters.AddWithValue("@legacy", MaintenanceMeta.LegacyClosed);
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
                cmd.CommandText = "UPDATE maintenance_records SET status=@done WHERE status=@legacy";
                cmd.Parameters.AddWithValue("@done", MaintenanceMeta.DoneStatus);
                cmd.Parameters.AddWithValue("@legacy", MaintenanceMeta.LegacyClosed);
                var done = cmd.ExecuteNonQuery();
                Logger.Info($"[迁移] {done} 条维修记录状态「已关闭」-> 「已完成」(v2.2.0 状态体系合并)");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[迁移] 维修记录状态迁移失败: {ex.Message}");
        }
    }

    /// <summary>PRAGMA wal_checkpoint(TRUNCATE) 并读返回值。busy≠0 = 有连接占写锁、截断未完成，
    /// 此时裸拷 .db 缺近期提交。纯读返回值，供自检与备份路径断言。</summary>
    internal static bool CheckpointTruncate(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.FieldCount == 0 || r.IsDBNull(0)) return false;
        return Convert.ToInt64(r.GetValue(0)) == 0;
    }

    /// <summary>热升级备份 / 迁移备份前调用：清池 + WAL 截断进主库，避免备份缺近期提交。失败仅告警不抛。</summary>
    public void CheckpointTruncateForBackup()
    {
        SqliteConnection.ClearAllPools();
        using var conn = Open();
        if (!CheckpointTruncate(conn))
            Logger.Warning("[数据库] WAL checkpoint 未完成(busy)，备份可能不含最后一批提交");
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
            // B5：wal_checkpoint 的 busy≠0 说明有连接占着写锁、截断没完成——此时裸拷 .db 会缺近期提交，
            // 而本备份是 closed->resolved 迁移与冷归档 DELETE 的安全闸门，宁可跳过也不能留残缺备份。
            SqliteConnection.ClearAllPools();
            using (var conn = Open())
            {
                if (!CheckpointTruncate(conn))
                {
                    Logger.Warning("[迁移] WAL checkpoint 未完成(busy)，跳过本次备份（避免丢近期提交的残缺备份）");
                    return false;
                }
            }
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
            // B5：见 BackupDbFile——checkpoint 未完成时留残缺备份比不备份更危险
            SqliteConnection.ClearAllPools();
            using (var conn = Open())
            {
                if (!CheckpointTruncate(conn))
                {
                    Logger.Warning("[数据库] WAL checkpoint 未完成(busy)，跳过今日备份（下次维护再试）");
                    return null;
                }
            }
            File.Copy(_dbPath, bak);
            var dir = Path.GetDirectoryName(_dbPath)!;
            var prefix = Path.GetFileName(_dbPath) + ".bak-";
            // B4：glob 必须排除 .sha256——否则校验文件占额度，名义「保留 7 份」实际只留 3 个 .bak
            var old = Directory.Exists(dir)
                ? Directory.GetFiles(dir, prefix + "*")
                    .Where(f => !f.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f, StringComparer.Ordinal).ToList()
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
        RememberCycles(list);
        return inserted;
    }

    /// <summary>审计 A2：单条入库并返回记录 id（重复 xml_path 时按内容变化覆盖更新）。</summary>
    public long InsertOneReturnId(TestRecord rec) => UpsertTestRecord(rec).RecordId;

    /// <summary>按 xml_path 幂等入库：新记录插入；已存在且结果/尺寸/失败项变化则覆盖更新（修复半成品先入库）。</summary>
    public IngestResult UpsertTestRecord(TestRecord rec)
    {
        var list = new List<IngestResult>(1);
        BatchUpsert(new[] { rec }, null, list);
        if (list.Count > 0) return list[0];
        var ids = GetRecordIdsByPaths(new[] { rec.XmlPath });
        ids.TryGetValue(rec.XmlPath, out var rid);
        return new IngestResult { RecordId = rid };
    }

    public bool IsFailAlerted(string xmlPath)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fail_alerted FROM test_records WHERE xml_path = @p LIMIT 1";
        cmd.Parameters.AddWithValue("@p", xmlPath);
        using var r = cmd.ExecuteReader();
        return r.Read() && !r.IsDBNull(0) && r.GetInt32(0) != 0;
    }

    public void MarkFailAlerted(string xmlPath)
    {
        MarkFailAlertedMany(new[] { xmlPath });
    }

    /// <summary>单连接单事务内批量标记已推送。空路径跳过。</summary>
    public int MarkFailAlertedMany(IEnumerable<string> xmlPaths)
    {
        var paths = xmlPaths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE test_records SET fail_alerted = 1 WHERE xml_path = @p";
        var p = cmd.Parameters.Add("@p", SqliteType.Text);
        var n = 0;
        foreach (var path in paths)
        {
            p.Value = path;
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    /// <summary>今日 FAIL 且尚未推送告警的记录（补扫后批量补推）。</summary>
    public List<TestRecord> ListTodayFailsNeedingAlert(string testDateYmd, string? stationId)
    {
        var list = new List<TestRecord>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE " + TestDateEqDay + " AND result = 'FAIL' AND COALESCE(fail_alerted, 0) = 0";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id = @s";
        cmd.CommandText = $@"
            SELECT station_id, model, category, test_date, sn, result, xml_path,
                   fail_reason, tester, panel_status, fixture_id, batch_timestamp, has_fail_items, file_size
            FROM test_records {where} ORDER BY id";
        BindDayDual(cmd, testDateYmd);
        if (!string.IsNullOrEmpty(stationId)) cmd.Parameters.AddWithValue("@s", stationId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TestRecord
            {
                StationId = r.GetString(0),
                Model = r.IsDBNull(1) ? "" : r.GetString(1),
                Category = r.IsDBNull(2) ? "" : r.GetString(2),
                TestDate = r.GetString(3),
                Sn = r.IsDBNull(4) ? null : r.GetString(4),
                Result = r.IsDBNull(5) ? "" : r.GetString(5),
                XmlPath = r.GetString(6),
                FailReason = r.IsDBNull(7) ? null : r.GetString(7),
                Tester = r.IsDBNull(8) ? null : r.GetString(8),
                PanelStatus = r.IsDBNull(9) ? null : r.GetString(9),
                FixtureId = r.IsDBNull(10) ? null : r.GetString(10),
                BatchTimestamp = r.IsDBNull(11) ? null : r.GetString(11),
                HasFailItems = !r.IsDBNull(12) && r.GetInt32(12) != 0,
                FileSize = r.IsDBNull(13) ? null : r.GetInt64(13),
            });
        }
        return list;
    }

    /// <summary>批量 upsert（历史扫描/实时共用）。outcomes 与 insertedRows 二选一或同时填充。</summary>
    public int BatchUpsert(IEnumerable<TestRecord> records, List<long>? insertedIds = null, List<IngestResult>? outcomes = null)
    {
        var list = records.ToList();
        if (list.Count == 0) return 0;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        int newCount = 0;
        var touchedRows = new List<(TestRecord Rec, long Id)>();
        var newRows = new List<(TestRecord Rec, long Id)>();
        var localOutcomes = new List<IngestResult>();
        var failCsvDates = new List<string>();

        using var sel = conn.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = @"SELECT id, result, COALESCE(file_size,0), COALESCE(has_fail_items,0), COALESCE(fail_alerted,0)
                            FROM test_records WHERE xml_path = @p LIMIT 1";
        var pSel = sel.Parameters.Add("@p", SqliteType.Text);

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = @"
            INSERT INTO test_records
            (station_id, model, category, test_date, sn, result, xml_path,
             fail_reason, tester, panel_status, batch_timestamp, has_fail_items, file_size, fixture_id, fail_alerted)
            VALUES (@station,@model,@cat,@date,@sn,@result,@path,
                    @reason,@tester,@panel,@ts,@hasfail,@size,@fixture,0)
            RETURNING id";
        BindRecordParams(ins);

        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = @"
            UPDATE test_records SET
                station_id=@station, model=@model, category=@cat, test_date=@date, sn=@sn,
                result=@result, fail_reason=@reason, tester=@tester, panel_status=@panel,
                batch_timestamp=@ts, has_fail_items=@hasfail, file_size=@size, fixture_id=@fixture,
                fail_alerted = CASE WHEN @result <> 'FAIL' THEN 0 ELSE fail_alerted END
            WHERE id=@id";
        BindRecordParams(upd);
        var pUpdId = upd.Parameters.Add("@id", SqliteType.Integer);

        using var delFail = conn.CreateCommand();
        delFail.Transaction = tx;
        delFail.CommandText = "DELETE FROM fail_items WHERE record_id = @id";
        var pDelFailId = delFail.Parameters.Add("@id", SqliteType.Integer);

        // 审计修复：待办增量扫描只看「id > 水位」，而更新已有行不改 id——就地变 FAIL / 失败项变多的记录
        // 永远进不了待办。这里把被更新的 FAIL 行登记进重扫集合，由 SyncTodoItems 一并消费。
        using var rescan = conn.CreateCommand();
        rescan.Transaction = tx;
        rescan.CommandText = "INSERT OR IGNORE INTO todo_rescan_ids(id) VALUES(@id)";
        var pRescanId = rescan.Parameters.Add("@id", SqliteType.Integer);

        foreach (var rec in list)
        {
            pSel.Value = rec.XmlPath;
            long id = 0;
            string? prevResult = null;
            bool isNew = false, wasUpdated = false;
            using (var r = sel.ExecuteReader())
            {
                if (!r.Read())
                {
                    SetRecordParamValues(ins, rec);
                    using var ir = ins.ExecuteReader();
                    if (!ir.Read()) continue;
                    id = ir.GetInt64(0);
                    isNew = true;
                    newCount++;
                }
                else
                {
                    id = r.GetInt64(0);
                    prevResult = r.IsDBNull(1) ? null : r.GetString(1);
                    var oldSize = r.GetInt64(2);
                    var oldHasFail = r.GetInt32(3) != 0;
                    if (!ShouldRefreshRecord(prevResult, oldSize, oldHasFail, rec))
                    {
                        localOutcomes.Add(new IngestResult
                        {
                            RecordId = id, IsNew = false, WasUpdated = false, PreviousResult = prevResult,
                            NeedsFailAlert = rec.Result == "FAIL" && r.GetInt32(4) == 0,
                        });
                        continue;
                    }
                    SetRecordParamValues(upd, rec);
                    pUpdId.Value = id;
                    upd.ExecuteNonQuery();
                    wasUpdated = true;
                    if (rec.Result == "FAIL")
                    {
                        pDelFailId.Value = id;
                        delFail.ExecuteNonQuery();
                        pRescanId.Value = id;
                        rescan.ExecuteNonQuery();
                    }
                }
            }

            var needsAlert = rec.Result == "FAIL" &&
                (isNew || !string.Equals(prevResult, "FAIL", StringComparison.OrdinalIgnoreCase));
            localOutcomes.Add(new IngestResult
            {
                RecordId = id, IsNew = isNew, WasUpdated = wasUpdated, PreviousResult = prevResult,
                NeedsFailAlert = needsAlert,
            });
            touchedRows.Add((rec, id));
            if (isNew) newRows.Add((rec, id));
            if (rec.Result == "FAIL" || string.Equals(prevResult, "FAIL", StringComparison.OrdinalIgnoreCase))
                failCsvDates.Add(rec.TestDate ?? "");
            insertedIds?.Add(id);
        }

        tx.Commit();
        outcomes?.AddRange(localOutcomes);
        if (newRows.Count > 0) NotifyRecordsInserted(newRows);
        if (touchedRows.Count > 0)
        {
            InsertMeasurementsFor(touchedRows);
            InsertFailItemsFor(touchedRows.Where(t => t.Rec.Result == "FAIL").ToList());
        }
        MirrorFailMonthCsv(failCsvDates);
        RememberCycles(list);
        return newCount;
    }

    /// <summary>有 FAIL 落库或离开 FAIL 时，按测试日期所在月重写那一张 CSV。写失败不影响入库。</summary>
    private void MirrorFailMonthCsv(List<string> testDates)
    {
        if (testDates.Count == 0) return;
        try
        {
            var months = new HashSet<(int Year, int Month)>();
            foreach (var raw in testDates)
            {
                var ymd = NormalizeYmdKey(raw);
                if (ymd.Length < 6) continue;
                if (!int.TryParse(ymd[..4], out var year)) continue;
                if (!int.TryParse(ymd.Substring(4, 2), out var month) || month is < 1 or > 12) continue;
                months.Add((year, month));
            }
            if (_failCsvDeferDepth > 0)
            {
                _deferredFailMonths ??= new HashSet<(int, int)>();
                foreach (var m in months) _deferredFailMonths.Add(m);
                return;
            }
            foreach (var (year, month) in months) WriteFailMonthCsv(year, month);
        }
        catch (Exception ex) { Logger.Warning($"FAIL 月表写入失败: {ex.Message}"); }
    }

    [ThreadStatic] private static int _failCsvDeferDepth;
    [ThreadStatic] private static HashSet<(int Year, int Month)>? _deferredFailMonths;

    /// <summary>历史扫描期间先攒受影响的月份，结束后每个月只重写一次。实时入库不走这里。</summary>
    public void RunWithDeferredFailMonthCsv(Action body)
    {
        _failCsvDeferDepth++;
        try { body(); }
        finally
        {
            _failCsvDeferDepth--;
            if (_failCsvDeferDepth == 0)
            {
                var pending = _deferredFailMonths;
                _deferredFailMonths = null;
                if (pending != null)
                {
                    foreach (var (year, month) in pending)
                    {
                        try { WriteFailMonthCsv(year, month); }
                        catch (Exception ex) { Logger.Warning($"FAIL 月表写入失败: {ex.Message}"); }
                    }
                }
            }
        }
    }

    private const int FailMonthCsvMaxRows = 200000;

    private void WriteFailMonthCsv(int year, int month)
    {
        var from = new DateTime(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var rows = QueryFailItemDetails("", from.ToString("yyyyMMdd"), to.ToString("yyyyMMdd"),
            maxRows: FailMonthCsvMaxRows + 1, orderByItem: false);
        if (rows.Count > FailMonthCsvMaxRows)
        {
            Logger.Warning($"FAIL 月表 {year:0000}-{month:00} 超过 {FailMonthCsvMaxRows} 行，CSV 只保留 {FailMonthCsvMaxRows} 行");
            rows.RemoveRange(FailMonthCsvMaxRows, rows.Count - FailMonthCsvMaxRows);
        }
        FailExporter.RewriteMonth(FailExporter.MonthLogPath(_dbPath, year, month), rows);
        try { EnsureRepeatFailTodos(from.ToString("yyyyMMdd"), to.ToString("yyyyMMdd")); }
        catch (Exception ex) { Logger.Warning($"重复故障待办补卡失败: {ex.Message}"); }
    }

    private static bool ShouldRefreshRecord(string? prevResult, long oldSize, bool oldHasFail, TestRecord rec)
    {
        if (!string.Equals(prevResult, rec.Result, StringComparison.OrdinalIgnoreCase)) return true;
        var newSize = rec.FileSize ?? 0;
        if (newSize > oldSize) return true;
        if (rec.Result == "FAIL" && rec.HasFailItems && !oldHasFail) return true;
        if (rec.Result == "FAIL" && !string.IsNullOrWhiteSpace(rec.FailReason) &&
            !string.Equals(prevResult, "FAIL", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void BindRecordParams(SqliteCommand cmd)
    {
        cmd.Parameters.Add("@station", SqliteType.Text);
        cmd.Parameters.Add("@model", SqliteType.Text);
        cmd.Parameters.Add("@cat", SqliteType.Text);
        cmd.Parameters.Add("@date", SqliteType.Text);
        cmd.Parameters.Add("@sn", SqliteType.Text);
        cmd.Parameters.Add("@result", SqliteType.Text);
        cmd.Parameters.Add("@path", SqliteType.Text);
        cmd.Parameters.Add("@reason", SqliteType.Text);
        cmd.Parameters.Add("@tester", SqliteType.Text);
        cmd.Parameters.Add("@panel", SqliteType.Text);
        cmd.Parameters.Add("@ts", SqliteType.Text);
        cmd.Parameters.Add("@hasfail", SqliteType.Integer);
        cmd.Parameters.Add("@size", SqliteType.Integer);
        cmd.Parameters.Add("@fixture", SqliteType.Text);
    }

    private static void SetRecordParamValues(SqliteCommand cmd, TestRecord rec)
    {
        cmd.Parameters["@station"].Value = rec.StationId;
        cmd.Parameters["@model"].Value = (object?)rec.Model ?? DBNull.Value;
        cmd.Parameters["@cat"].Value = (object?)rec.Category ?? DBNull.Value;
        cmd.Parameters["@date"].Value = rec.TestDate;
        cmd.Parameters["@sn"].Value = (object?)rec.Sn ?? DBNull.Value;
        cmd.Parameters["@result"].Value = rec.Result;
        cmd.Parameters["@path"].Value = rec.XmlPath;
        cmd.Parameters["@reason"].Value = (object?)rec.FailReason ?? DBNull.Value;
        cmd.Parameters["@tester"].Value = (object?)rec.Tester ?? DBNull.Value;
        cmd.Parameters["@panel"].Value = (object?)rec.PanelStatus ?? DBNull.Value;
        cmd.Parameters["@ts"].Value = (object?)rec.BatchTimestamp ?? DBNull.Value;
        cmd.Parameters["@hasfail"].Value = rec.HasFailItems ? 1 : 0;
        cmd.Parameters["@size"].Value = (object?)rec.FileSize ?? DBNull.Value;
        cmd.Parameters["@fixture"].Value = (object?)rec.FixtureId ?? DBNull.Value;
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
            bool fat = _detailTablesFat;
            cmd.CommandText = fat
                ? @"INSERT OR IGNORE INTO test_measurements
                    (record_id, xml_path, station_id, model, sn, test_date, ts, test_name, value, value_text, lolim, hilim, unit, rule)
                    VALUES (@rid, @path, @station, @model, @sn, @date, @ts, @name, @value, @vtext, @lolim, @hilim, @unit, @rule)"
                : @"INSERT OR IGNORE INTO test_measurements
                    (record_id, ts, test_name, value, value_text, lolim, hilim, unit, rule)
                    VALUES (@rid, @ts, @name, @value, @vtext, @lolim, @hilim, @unit, @rule)";
            var pRid = cmd.Parameters.Add("@rid", SqliteType.Integer);
            var pTs = cmd.Parameters.Add("@ts", SqliteType.Text);
            var pName = cmd.Parameters.Add("@name", SqliteType.Text);
            var pValue = cmd.Parameters.Add("@value", SqliteType.Real);
            var pVText = cmd.Parameters.Add("@vtext", SqliteType.Text);
            var pLo = cmd.Parameters.Add("@lolim", SqliteType.Real);
            var pHi = cmd.Parameters.Add("@hilim", SqliteType.Real);
            var pUnit = cmd.Parameters.Add("@unit", SqliteType.Text);
            var pRule = cmd.Parameters.Add("@rule", SqliteType.Text);
            var pPath = fat ? cmd.Parameters.Add("@path", SqliteType.Text) : null;
            var pStation = fat ? cmd.Parameters.Add("@station", SqliteType.Text) : null;
            var pModel = fat ? cmd.Parameters.Add("@model", SqliteType.Text) : null;
            var pSn = fat ? cmd.Parameters.Add("@sn", SqliteType.Text) : null;
            var pDate = fat ? cmd.Parameters.Add("@date", SqliteType.Text) : null;
            foreach (var (rec, id) in withM)
            {
                var ts = (object?)rec.BatchTimestamp ?? DBNull.Value;
                foreach (var m in rec.Measurements)
                {
                    pRid.Value = id;
                    pTs.Value = ts;
                    pName.Value = m.TestName;
                    pValue.Value = (object?)m.Value ?? DBNull.Value;
                    pVText.Value = (object?)m.ValueText ?? DBNull.Value;
                    pLo.Value = (object?)m.Lolim ?? DBNull.Value;
                    pHi.Value = (object?)m.Hilim ?? DBNull.Value;
                    pUnit.Value = (object?)m.Unit ?? DBNull.Value;
                    pRule.Value = (object?)m.Rule ?? DBNull.Value;
                    if (fat)
                    {
                        pPath!.Value = rec.XmlPath ?? "";
                        pStation!.Value = (object?)rec.StationId ?? DBNull.Value;
                        pModel!.Value = (object?)rec.Model ?? DBNull.Value;
                        pSn!.Value = (object?)rec.Sn ?? DBNull.Value;
                        pDate!.Value = (object?)rec.TestDate ?? DBNull.Value;
                    }
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
        // 审计修复：原条件 COALESCE(ts,'')<>'' 让 ts 为空/NULL 的明细行永不参与滚动删除（无界增长）。
        // 这里补一条：ts 为空的按「父记录的自然日」判定——既不会误删当天新记录，也不会永久残留。
        cmd.CommandText = $@"
            DELETE FROM test_measurements
             WHERE (COALESCE(ts,'') <> '' AND substr(COALESCE(ts,''),1,10) < @cutoff)
                OR (COALESCE(ts,'') = '' AND record_id IN (
                       SELECT id FROM test_records WHERE {NormalizedDayExpr} < @cutoff))";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    public int PurgeOldFailItems(int retentionDays = 90)
    {
        var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, retentionDays)).ToString("yyyy-MM-dd");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 审计修复：同 PurgeOldMeasurements——空 ts 行按父记录自然日判定，避免永久残留。
        cmd.CommandText = $@"
            DELETE FROM fail_items
             WHERE (COALESCE(ts,'') <> '' AND substr(COALESCE(ts,''),1,10) < @cutoff)
                OR (COALESCE(ts,'') = '' AND record_id IN (
                       SELECT id FROM test_records WHERE {NormalizedDayExpr} < @cutoff))";
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
            bool fat = _detailTablesFat;
            cmd.CommandText = fat
                ? @"INSERT OR IGNORE INTO fail_items
                    (record_id, xml_path, station_id, model, sn, test_date, ts, fixture_id, tester, hour, test_name, value, value_text, lolim, hilim, unit, rule)
                    VALUES (@rid, @path, @station, @model, @sn, @date, @ts, @fixture, @tester, @hour, @name, @value, @vtext, @lolim, @hilim, @unit, @rule)"
                : @"INSERT OR IGNORE INTO fail_items
                    (record_id, ts, fixture_id, tester, hour, test_name, value, value_text, lolim, hilim, unit, rule)
                    VALUES (@rid, @ts, @fixture, @tester, @hour, @name, @value, @vtext, @lolim, @hilim, @unit, @rule)";
            var pRid = cmd.Parameters.Add("@rid", SqliteType.Integer);
            var pTs = cmd.Parameters.Add("@ts", SqliteType.Text);
            var pFixture = cmd.Parameters.Add("@fixture", SqliteType.Text);
            var pTester = cmd.Parameters.Add("@tester", SqliteType.Text);
            var pHour = cmd.Parameters.Add("@hour", SqliteType.Integer);
            var pName = cmd.Parameters.Add("@name", SqliteType.Text);
            var pValue = cmd.Parameters.Add("@value", SqliteType.Real);
            var pVText = cmd.Parameters.Add("@vtext", SqliteType.Text);
            var pLo = cmd.Parameters.Add("@lolim", SqliteType.Real);
            var pHi = cmd.Parameters.Add("@hilim", SqliteType.Real);
            var pUnit = cmd.Parameters.Add("@unit", SqliteType.Text);
            var pRule = cmd.Parameters.Add("@rule", SqliteType.Text);
            var pPath = fat ? cmd.Parameters.Add("@path", SqliteType.Text) : null;
            var pStation = fat ? cmd.Parameters.Add("@station", SqliteType.Text) : null;
            var pModel = fat ? cmd.Parameters.Add("@model", SqliteType.Text) : null;
            var pSn = fat ? cmd.Parameters.Add("@sn", SqliteType.Text) : null;
            var pDate = fat ? cmd.Parameters.Add("@date", SqliteType.Text) : null;
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
                    pTs.Value = ts;
                    pFixture.Value = (object?)rec.FixtureId ?? DBNull.Value;
                    pTester.Value = (object?)rec.Tester ?? DBNull.Value;
                    pHour.Value = hour;
                    pName.Value = f.Name;
                    if (fat)
                    {
                        pPath!.Value = rec.XmlPath ?? "";
                        pStation!.Value = (object?)rec.StationId ?? DBNull.Value;
                        pModel!.Value = (object?)rec.Model ?? DBNull.Value;
                        pSn!.Value = (object?)rec.Sn ?? DBNull.Value;
                        pDate!.Value = (object?)rec.TestDate ?? DBNull.Value;
                    }
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
            bool fat = _detailTablesFat;
            cmd.CommandText = fat
                ? @"INSERT OR IGNORE INTO tdms_features
                    (record_id, xml_path, station_id, model, sn, test_date, ts, tdms_path, group_name, channel_name, section, n, vmin, vmax, vmean, vstd, vfirst, vlast)
                    VALUES (@rid, @path, @station, @model, @sn, @date, @ts, @tdms, @group, @channel, @section, @n, @vmin, @vmax, @vmean, @vstd, @vfirst, @vlast)"
                : @"INSERT OR IGNORE INTO tdms_features
                    (record_id, ts, tdms_path, group_name, channel_name, section, n, vmin, vmax, vmean, vstd, vfirst, vlast)
                    VALUES (@rid, @ts, @tdms, @group, @channel, @section, @n, @vmin, @vmax, @vmean, @vstd, @vfirst, @vlast)";
            var pRid = cmd.Parameters.Add("@rid", SqliteType.Integer);
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
            var pPath = fat ? cmd.Parameters.Add("@path", SqliteType.Text) : null;
            var pStation = fat ? cmd.Parameters.Add("@station", SqliteType.Text) : null;
            var pModel = fat ? cmd.Parameters.Add("@model", SqliteType.Text) : null;
            var pSn = fat ? cmd.Parameters.Add("@sn", SqliteType.Text) : null;
            var pDate = fat ? cmd.Parameters.Add("@date", SqliteType.Text) : null;
            var ts = (object?)rec.BatchTimestamp ?? DBNull.Value;
            foreach (var f in rows)
            {
                pRid.Value = id;
                pTs.Value = ts;
                pTdms.Value = tdmsPath;
                if (fat)
                {
                    pPath!.Value = rec.XmlPath ?? "";
                    pStation!.Value = (object?)rec.StationId ?? DBNull.Value;
                    pModel!.Value = (object?)rec.Model ?? DBNull.Value;
                    pSn!.Value = (object?)rec.Sn ?? DBNull.Value;
                    pDate!.Value = (object?)rec.TestDate ?? DBNull.Value;
                }
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

    /// <summary>UI 用：最近 TDMS 通道特征（型号/SN 从 test_records JOIN）。</summary>
    public List<(long RecordId, string Model, string Sn, string GroupName, string ChannelName, string Section, int N, double? Min, double? Max, double? Mean, double? Std, string Ts)>
        GetRecentTdmsFeatures(int limit = 50)
    {
        var list = new List<(long, string, string, string, string, string, int, double?, double?, double?, double?, string)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT f.record_id, COALESCE(r.model,''), COALESCE(r.sn,''), f.group_name, f.channel_name,
                   COALESCE(f.section,''), COALESCE(f.n,0), f.vmin, f.vmax, f.vmean, f.vstd, COALESCE(f.ts,'')
            FROM tdms_features f
            LEFT JOIN test_records r ON r.id = f.record_id
            ORDER BY f.id DESC LIMIT @n";
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

    public void InsertDeviationEvent(DeviationEvent ev)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO deviation_events (ts, model, source, signal_count, top_signal, top_score, detail_json, seen)
            VALUES (@ts, @m, @src, @n, @top, @score, @detail, @seen)";
        cmd.Parameters.AddWithValue("@ts", ev.Ts ?? "");
        cmd.Parameters.AddWithValue("@m", ev.Model ?? "");
        cmd.Parameters.AddWithValue("@src", ev.Source ?? "");
        cmd.Parameters.AddWithValue("@n", ev.SignalCount);
        cmd.Parameters.AddWithValue("@top", ev.TopSignal ?? "");
        cmd.Parameters.AddWithValue("@score", ev.TopScore);
        cmd.Parameters.AddWithValue("@detail", ev.DetailJson ?? "[]");
        cmd.Parameters.AddWithValue("@seen", ev.Seen);
        cmd.ExecuteNonQuery();
    }

    public int CountDeviationEventsOnDay(string ymd)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM deviation_events WHERE substr(ts,1,10)=@d";
        cmd.Parameters.AddWithValue("@d", ymd ?? "");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<DeviationEvent> ListDeviationEvents(int limit = 100)
    {
        var list = new List<DeviationEvent>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ts, model, source, signal_count, top_signal, top_score, COALESCE(detail_json,''), seen
            FROM deviation_events ORDER BY id DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", Math.Clamp(limit, 1, 1000));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DeviationEvent
            {
                Ts = r.GetString(0), Model = r.GetString(1), Source = r.GetString(2),
                SignalCount = r.GetInt32(3), TopSignal = r.GetString(4), TopScore = r.GetDouble(5),
                DetailJson = r.GetString(6), Seen = r.GetInt32(7),
            });
        }
        return list;
    }

    public (int Total, int Ready, int Learning, int Stale) CountNormalModels(DateTime now, int staleDays)
    {
        int total = 0, ready = 0, learning = 0, stale = 0;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, COALESCE(last_ts,''), n FROM normal_models";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            total++;
            var row = new NormalModelRow { Status = r.GetString(0), LastTs = r.GetString(1), N = r.GetInt64(2) };
            var st = NormalModelStore.EffectiveStatus(row, now, staleDays);
            if (st == "ready") ready++;
            else if (st == "stale") stale++;
            else learning++;
        }
        return (total, ready, learning, stale);
    }

    public List<NormalModelRow> ListNormalModels(string? source = null, string? model = null, int limit = 500)
    {
        var list = new List<NormalModelRow>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(source)) { where.Add("source=@s"); cmd.Parameters.AddWithValue("@s", source); }
        if (!string.IsNullOrWhiteSpace(model)) { where.Add("model=@m"); cmd.Parameters.AddWithValue("@m", model); }
        var filter = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $@"
            SELECT source, model, signal_key, n, COALESCE(mean,0), COALESCE(sigma,0), COALESCE(p01,0), COALESCE(p50,0), COALESCE(p99,0),
                   COALESCE(min_v,0), COALESCE(max_v,0), COALESCE(last_ts,''), status, COALESCE(qstate,'')
            FROM normal_models {filter} ORDER BY source, model, signal_key LIMIT @n";
        // 审计：限定上限远大于单机模型数（机型×测项），仅为防失控查询，不构成截断漏线
        cmd.Parameters.AddWithValue("@n", Math.Clamp(limit, 1, 1_000_000));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new NormalModelRow
            {
                Source = r.GetString(0), Model = r.GetString(1), SignalKey = r.GetString(2),
                N = r.GetInt64(3), Mean = r.GetDouble(4), Sigma = r.GetDouble(5),
                P01 = r.GetDouble(6), P50 = r.GetDouble(7), P99 = r.GetDouble(8),
                MinV = r.GetDouble(9), MaxV = r.GetDouble(10),
                LastTs = r.GetString(11), Status = r.GetString(12), QState = r.GetString(13),
            });
        }
        return list;
    }

    public void DeleteNormalModel(string source, string model, string signalKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM normal_models WHERE source=@s AND model=@m AND signal_key=@k";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@m", model ?? "");
        cmd.Parameters.AddWithValue("@k", signalKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>复制库文件前合并 WAL，避免现场回放/备份读到半写入数据。</summary>
    public void CheckpointForCopy()
    {
        if (!File.Exists(_dbPath)) return;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>回放专用：清空正常态模型与偏离事件（不动 test_records）。</summary>
    public void ClearReplayLearningTables()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM normal_models; DELETE FROM deviation_events;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>窗口内出现过的全部 PASS 测项名（去重排序）。</summary>
    public List<string> ListPassTestNamesInWindow(string fromYmd, string toYmdInclusive)
    {
        var list = new List<string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT m.test_name
            FROM test_measurements m
            JOIN test_records r ON r.id = m.record_id
            WHERE r.result='PASS' AND m.value IS NOT NULL
              AND m.test_name IS NOT NULL AND m.test_name <> ''
              AND m.ts >= @from AND m.ts < @toExcl
            ORDER BY m.test_name COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@from", fromYmd ?? "");
        cmd.Parameters.AddWithValue("@toExcl", ToExclusiveIso(toYmdInclusive));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>折线图：PASS 测量点（日期闭区间），按 ts/record 排序；testNames 为空则不过滤测项。</summary>
    public List<(long RecordId, string Model, string Ts, string TestName, double Value)> ListPassMeasurementPoints(
        string fromYmd, string toYmdInclusive, IEnumerable<string>? testNames = null, int maxRows = 5000)
    {
        var list = new List<(long, string, string, string, double)>();
        var names = testNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = @"
            SELECT m.record_id, COALESCE(r.model,''), COALESCE(m.ts,''), m.test_name, m.value
            FROM test_measurements m
            JOIN test_records r ON r.id = m.record_id
            WHERE r.result='PASS' AND m.value IS NOT NULL
              AND m.ts >= @from AND m.ts < @toExcl";
        if (names is { Count: > 0 })
        {
            var ph = new List<string>();
            for (int i = 0; i < names.Count; i++) ph.Add($"@n{i}");
            sql += $" AND m.test_name IN ({string.Join(",", ph)})";
        }
        sql += " ORDER BY m.ts, m.record_id, m.id LIMIT @lim";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@from", fromYmd ?? "");
        cmd.Parameters.AddWithValue("@toExcl", ToExclusiveIso(toYmdInclusive));
        cmd.Parameters.AddWithValue("@lim", Math.Max(1, maxRows));
        if (names is { Count: > 0 })
            for (int i = 0; i < names.Count; i++)
                cmd.Parameters.AddWithValue($"@n{i}", names[i]);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4)));
        return list;
    }

    /// <summary>
    /// 趋势图：窗口内测项级统计（限值/量程/单位），用于归一化基准。
    /// 限值取 MAX(lolim)/MIN(hilim)（行间限值变化时取最保守的内侧带）；NULL 行被聚合忽略。
    /// </summary>
    public List<TrendItemStat> QueryPassTrendItemStats(
        string fromIso, string toIsoExclusive, IEnumerable<string>? testNames = null)
    {
        var list = new List<TrendItemStat>();
        var names = NamesOrNull(testNames);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = @"
            SELECT m.test_name, MAX(m.lolim), MIN(m.hilim), MIN(m.value), MAX(m.value),
                   COALESCE(MAX(m.unit),''), COUNT(*)
            FROM test_measurements m
            JOIN test_records r ON r.id = m.record_id
            WHERE r.result='PASS' AND m.value IS NOT NULL
              AND m.ts >= @from AND m.ts < @to";
        if (names != null) sql += $" AND m.test_name COLLATE NOCASE IN ({Placeholders(names.Count)})";
        sql += " GROUP BY m.test_name ORDER BY m.test_name COLLATE NOCASE";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@from", fromIso ?? "");
        cmd.Parameters.AddWithValue("@to", toIsoExclusive ?? "");
        BindNames(cmd, names);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TrendItemStat
            {
                TestName = r.GetString(0),
                Lolim = r.IsDBNull(1) ? null : r.GetDouble(1),
                Hilim = r.IsDBNull(2) ? null : r.GetDouble(2),
                MinValue = r.IsDBNull(3) ? null : r.GetDouble(3),
                MaxValue = r.IsDBNull(4) ? null : r.GetDouble(4),
                Unit = r.GetString(5),
                N = r.GetInt64(6),
            });
        }
        return list;
    }

    /// <summary>
    /// 趋势图：按时间桶聚合段内原始均值（桶宽秒，桶 0 = 区间起点）。
    /// 桶序号用 strftime('%s', …) 整数分秒后除以桶宽，两端都在 SQL 内同一下取整口径，
    /// 避免 C# 侧换算把「本地时间」当「UTC」导致按天分桶错位。
    /// </summary>
    public List<TrendBucketRow> QueryPassTrendBuckets(
        string fromIso, string toIsoExclusive, long bucketSeconds, IEnumerable<string>? testNames = null)
    {
        var list = new List<TrendBucketRow>();
        var names = NamesOrNull(testNames);
        long sec = Math.Max(60, bucketSeconds);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = @"
            SELECT m.test_name,
                   CAST((CAST(strftime('%s', m.ts) AS INTEGER) - CAST(strftime('%s', @from) AS INTEGER)) / @sec AS INTEGER) AS b,
                   AVG(m.value), COUNT(*)
            FROM test_measurements m
            JOIN test_records r ON r.id = m.record_id
            WHERE r.result='PASS' AND m.value IS NOT NULL
              AND m.ts >= @from AND m.ts < @to
              AND strftime('%s', m.ts) IS NOT NULL";
        if (names != null) sql += $" AND m.test_name COLLATE NOCASE IN ({Placeholders(names.Count)})";
        sql += " GROUP BY m.test_name, b ORDER BY m.test_name COLLATE NOCASE, b";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@from", fromIso ?? "");
        cmd.Parameters.AddWithValue("@to", toIsoExclusive ?? "");
        cmd.Parameters.AddWithValue("@sec", sec);
        BindNames(cmd, names);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(1)) continue;
            list.Add(new TrendBucketRow
            {
                TestName = r.GetString(0),
                Bucket = r.GetInt32(1),
                MeanValue = r.GetDouble(2),
                N = r.GetInt64(3),
            });
        }
        return list;
    }

    /// <summary>
    /// 趋势图：**FAIL 失败项**的按桶均值。与 PASS 桶完全同口径（同区间、同桶宽、段内 AVG），
    /// 便于两条线画在同一纵轴上直接比较。
    /// 只取 <c>value</c> 能解析成数字的失败项——value_text 文本项（如 CONT=Open）没有数值可归一化，跳过。
    /// </summary>
    public List<TrendBucketRow> QueryFailTrendBuckets(
        string fromIso, string toIsoExclusive, long bucketSeconds, IEnumerable<string>? testNames = null)
    {
        var list = new List<TrendBucketRow>();
        var names = NamesOrNull(testNames);
        long sec = Math.Max(60, bucketSeconds);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var sql = @"
            SELECT fi.test_name,
                   CAST((CAST(strftime('%s', fi.ts) AS INTEGER) - CAST(strftime('%s', @from) AS INTEGER)) / @sec AS INTEGER) AS b,
                   AVG(fi.value), COUNT(*)
            FROM fail_items fi
            JOIN test_records r ON r.id = fi.record_id
            WHERE r.result='FAIL' AND fi.value IS NOT NULL
              AND fi.ts >= @from AND fi.ts < @to
              AND strftime('%s', fi.ts) IS NOT NULL";
        if (names != null) sql += $" AND fi.test_name COLLATE NOCASE IN ({Placeholders(names.Count)})";
        sql += " GROUP BY fi.test_name, b ORDER BY fi.test_name COLLATE NOCASE, b";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@from", fromIso ?? "");
        cmd.Parameters.AddWithValue("@to", toIsoExclusive ?? "");
        cmd.Parameters.AddWithValue("@sec", sec);
        BindNames(cmd, names);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(1)) continue;
            list.Add(new TrendBucketRow
            {
                TestName = r.GetString(0),
                Bucket = r.GetInt32(1),
                MeanValue = r.GetDouble(2),
                N = r.GetInt64(3),
            });
        }
        return list;
    }

    private static List<string>? NamesOrNull(IEnumerable<string>? testNames)
    {
        var names = testNames?.Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names is { Count: > 0 } ? names : null;
    }

    private static string Placeholders(int n)
    {
        var parts = new string[n];
        for (int i = 0; i < n; i++) parts[i] = "@n" + i;
        return string.Join(",", parts);
    }

    private static void BindNames(SqliteCommand cmd, List<string>? names)
    {
        if (names == null) return;
        for (int i = 0; i < names.Count; i++)
            cmd.Parameters.AddWithValue($"@n{i}", names[i]);
    }

    /// <summary>回放专用：PASS 测量在 [fromYmd, toYmdExclusive) 日期闭开区间。</summary>
    public List<(long RecordId, string Model, string Ts, string TestName, double Value)> ListPassMeasurementsBetween(string fromYmd, string toYmdExclusive)
    {
        var list = new List<(long, string, string, string, double)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT m.record_id, COALESCE(r.model,''), COALESCE(m.ts,''), m.test_name, m.value
            FROM test_measurements m
            JOIN test_records r ON r.id = m.record_id
            WHERE r.result='PASS' AND m.value IS NOT NULL
              AND m.ts >= @from AND m.ts < @to
            ORDER BY m.record_id, m.id";
        cmd.Parameters.AddWithValue("@from", fromYmd ?? "");
        cmd.Parameters.AddWithValue("@to", toYmdExclusive ?? "");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4)));
        return list;
    }

    /// <summary>审计修复：test_records「自然日」的统一归一表达式（yyyy-MM-dd）。
    /// test_date 存在 yyyyMMdd / yyyy-MM-dd 双格式混存，batch_timestamp 可能为空——原实现直接 substr 后
    /// 与 dash 参数做字符串比较：8 位日期第 5 位 '0'(0x30) &gt; '-'(0x2D) 会被判为「大于上界」而整行落选，
    /// C# 端还有 length&lt;10 过滤会再丢一次。此处统一铸成 dash 形式再比较。</summary>
    private const string NormalizedDayExpr = @"CASE
                    WHEN COALESCE(NULLIF(batch_timestamp,''),'') <> '' THEN substr(batch_timestamp,1,10)
                    WHEN length(replace(COALESCE(test_date,''),'-','')) = 8
                        THEN substr(replace(test_date,'-',''),1,4)||'-'||substr(replace(test_date,'-',''),5,2)||'-'||substr(replace(test_date,'-',''),7,2)
                    ELSE substr(COALESCE(created_at,''),1,10)
                END";

    /// <summary>库内最早 PASS 记录日期（yyyy-MM-dd），无 PASS 则 null。</summary>
    public string? GetEarliestPassRecordYmd()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT MIN({NormalizedDayExpr})
            FROM test_records WHERE result='PASS'";
        var v = cmd.ExecuteScalar();
        if (v == null || v is DBNull) return null;
        var s = v.ToString();
        return string.IsNullOrEmpty(s) || s.Length < 10 ? null : s;
    }

    /// <summary>PASS 测量缓存行数（test_measurements）。</summary>
    public long CountPassMeasurementRows()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) FROM test_measurements m
            JOIN test_records r ON r.id = m.record_id WHERE r.result='PASS'";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>回放专用：取 PASS 测量日期范围（yyyy-MM-dd），无数据则 null。</summary>
    public (string Min, string Max)? GetPassMeasurementDateRange()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MIN(substr(COALESCE(m.ts,''),1,10)), MAX(substr(COALESCE(m.ts,''),1,10))
            FROM test_measurements m
            JOIN test_records r ON r.id = m.record_id
            WHERE r.result='PASS' AND m.value IS NOT NULL AND length(COALESCE(m.ts,'')) >= 10";
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0) || r.IsDBNull(1)) return null;
        var min = r.GetString(0);
        var max = r.GetString(1);
        if (string.IsNullOrEmpty(min) || string.IsNullOrEmpty(max)) return null;
        return (min, max);
    }

    /// <summary>回放专用：按日统计 FAIL 条数（test_date 或 batch_timestamp 前 10 位）。</summary>
    public Dictionary<string, int> CountDailyFailsBetween(string fromYmd, string toYmdInclusive)
    {
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 审计修复：与 GetEarliestPassRecordYmd 同一归一表达式——原实现让 8 位 test_date 的行在与
        // dash 参数比较时被判为超出上界而整行落选（回放异常日推断会少算这些 FAIL）。
        cmd.CommandText = $@"
            SELECT {NormalizedDayExpr} AS d, COUNT(*)
            FROM test_records
            WHERE result='FAIL'
              AND {NormalizedDayExpr} >= @from
              AND {NormalizedDayExpr} <= @to
            GROUP BY d";
        cmd.Parameters.AddWithValue("@from", fromYmd ?? "");
        cmd.Parameters.AddWithValue("@to", toYmdInclusive ?? "");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var d = r.GetString(0);
            if (d.Length >= 10) dict[d[..10]] = r.GetInt32(1);
        }
        return dict;
    }

    public List<(long RecordId, string Model, string Ts, string TestName, double Value)> ListPassMeasurementsAfter(long afterRecordId, string fromYmd)
    {
        var list = new List<(long, string, string, string, double)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT m.record_id, COALESCE(r.model,''), COALESCE(m.ts,''), m.test_name, m.value
            FROM test_measurements m
            JOIN test_records r ON r.id = m.record_id
            WHERE r.result='PASS' AND m.value IS NOT NULL AND m.record_id > @wm
              AND m.ts >= @from
            ORDER BY m.record_id, m.id";
        cmd.Parameters.AddWithValue("@wm", afterRecordId);
        cmd.Parameters.AddWithValue("@from", fromYmd ?? "");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4)));
        return list;
    }

    public List<(long RecordId, string Model, string Ts, string Group, string Channel, double? Mean, double? Std, double? Max)> ListPassTdmsAfter(long afterRecordId, string fromYmd)
    {
        var list = new List<(long, string, string, string, string, double?, double?, double?)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT f.record_id, COALESCE(r.model,''), COALESCE(f.ts,''), f.group_name, f.channel_name, f.vmean, f.vstd, f.vmax
            FROM tdms_features f
            JOIN test_records r ON r.id = f.record_id
            WHERE r.result='PASS' AND f.record_id > @wm
              AND f.ts >= @from
            ORDER BY f.record_id, f.id";
        cmd.Parameters.AddWithValue("@wm", afterRecordId);
        cmd.Parameters.AddWithValue("@from", fromYmd ?? "");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetDouble(5),
                r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7)));
        }
        return list;
    }

    public List<(long Id, string Ts, double Cpu, double Mem, double Disk)> ListDeviceSamplesAfter(long afterId, string fromYmd)
    {
        var list = new List<(long, string, double, double, double)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, ts, cpu_usage, mem_used_pct, disk_free_gb
            FROM device_samples_local
            WHERE id > @wm AND substr(COALESCE(ts,''),1,10) >= @from
            ORDER BY id";
        cmd.Parameters.AddWithValue("@wm", afterId);
        cmd.Parameters.AddWithValue("@from", fromYmd ?? "");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4)));
        return list;
    }

    public List<(string Model, string TestName, double Value)> ListFailItemValuesSince(string fromYmd)
    {
        var list = new List<(string, string, double)>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(r.model,''), f.test_name, f.value
            FROM fail_items f
            JOIN test_records r ON r.id = f.record_id
            WHERE f.value IS NOT NULL AND f.ts >= @from
            ORDER BY f.id DESC LIMIT 500";
        cmd.Parameters.AddWithValue("@from", fromYmd ?? "");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetDouble(2)));
        return list;
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
            SELECT COALESCE(r.model,''), m.test_name, COALESCE(r.tester,''), COUNT(*),
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
            SELECT COALESCE(r.model,''), m.test_name, COUNT(*), AVG(m.value), AVG(m.value*m.value),
                   MIN(m.lolim), MAX(m.hilim), MAX(m.unit)
            FROM test_measurements m
            JOIN test_records r ON r.id = m.record_id
            WHERE m.ts >= @from AND m.ts < @to AND m.value IS NOT NULL
            GROUP BY 1, 2";
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
            // 审计：DBNull.ToString() 返回 ""（非 null），?? "ok" 兜不住——空库/特殊库会误报"integrity_check 异常"。
            var raw = cmd.ExecuteScalar();
            var r = raw is null or DBNull ? "ok" : raw.ToString() ?? "ok";
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

    /// <summary>冷数据判定条件（三处 COUNT/SELECT/DELETE 必须同口径）。
    /// 审计：test_date 存在 yyyyMMdd 与 yyyy-MM-dd 两种格式（见 IsWithinRetention 与双格式查询），
    /// 直接拿 "yyyyMMdd" 字面量与 dash 格式做字符串比较时 "2026-09-10" &lt; "20260612" 恒成立（'-' &lt; '0'），
    /// 会把近期记录当冷数据导出后 DELETE（静默数据丢失）。归一化并只认 8 位日期后再比较。
    /// 常量化供自检断言同口径。</summary>
    public const string ColdDataPredicate =
        "length(replace(test_date,'-',''))=8 AND replace(test_date,'-','') < @c";

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
                cmd.CommandText = $"SELECT COUNT(*) FROM test_records WHERE {ColdDataPredicate}";
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
                        cmd.CommandText = $"SELECT {string.Join(", ", cols)} FROM test_records WHERE {ColdDataPredicate} ORDER BY id";
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
                        cmd.CommandText = $"DELETE FROM test_records WHERE {ColdDataPredicate}";
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
                COUNT(DISTINCT COALESCE(NULLIF(TRIM(COALESCE(sn,'')),''), xml_path))
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

    /// <summary>今日 KPI（公司 FPY 口径）：同 SN 当日只要有 FAIL 就记 FAIL（复测 PASS 不抹掉不良）；
    /// Offline PASS 不计（复测台），Offline FAIL 计入；空 SN 按 xml_path。总测试 = PASS+FAIL。</summary>
    public StatsData FetchDailyStats(string stationId, string dateYmd)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH ranked AS (
                {SnLatestCte(stationId)}
            )
            SELECT
                COUNT(CASE WHEN result='PASS' THEN 1 END),
                COUNT(CASE WHEN result='FAIL' THEN 1 END),
                COUNT(CASE WHEN result='INTERRUPTED' THEN 1 END),
                COUNT(CASE WHEN result IN ('PASS','FAIL') THEN 1 END)
            FROM ranked WHERE rn = 1";
        BindDayParams(cmd, stationId, dateYmd);
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

    /// <summary>今日按型号拆分（与 FetchDailyStats 同一套 SN 终检口径）。</summary>
    public List<ModelDayStat> FetchDailyStatsByModel(string stationId, string dateYmd)
    {
        var list = new List<ModelDayStat>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH ranked AS (
                {SnLatestCte(stationId)}
            )
            SELECT COALESCE(NULLIF(TRIM(model), ''), '(未知)'),
                COUNT(CASE WHEN result='PASS' THEN 1 END),
                COUNT(CASE WHEN result='FAIL' THEN 1 END),
                COUNT(CASE WHEN result='INTERRUPTED' THEN 1 END)
            FROM ranked WHERE rn = 1
            GROUP BY 1
            ORDER BY 1";
        BindDayParams(cmd, stationId, dateYmd);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new ModelDayStat
            {
                Model = r.GetString(0),
                Pass = r.GetInt32(1),
                Fail = r.GetInt32(2),
                Interrupted = r.GetInt32(3),
            };
            list.Add(row);
        }
        return list;
    }

    private static string SnLatestCte(string stationId)
    {
        var where = "WHERE " + TestDateEqDay + " AND NOT (result = 'PASS' AND COALESCE(category, '') = 'Offline')";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id = @s";
        return $@"
                SELECT id, result, model, sn, xml_path, fail_reason, station_id, tester,
                       batch_timestamp, created_at,
                    ROW_NUMBER() OVER (
                        PARTITION BY COALESCE(NULLIF(TRIM(sn), ''), xml_path)
                        ORDER BY CASE result WHEN 'FAIL' THEN 0 WHEN 'PASS' THEN 1 ELSE 2 END,
                                 REPLACE(COALESCE(NULLIF(batch_timestamp,''), created_at, ''), 'T', ' ') DESC,
                                 id DESC
                    ) AS rn
                FROM test_records
                {where}";
    }

    private static void BindDayParams(SqliteCommand cmd, string stationId, string dateYmd)
    {
        BindDayDual(cmd, dateYmd);
        if (!string.IsNullOrEmpty(stationId))
            cmd.Parameters.AddWithValue("@s", stationId);
    }

    private static string ToExclusiveIso(string? ymdInclusive)
    {
        var raw = (ymdInclusive ?? "").Replace("-", "").Trim();
        if (raw.Length >= 8 && DateTime.TryParseExact(raw[..8], "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
            return d.AddDays(1).ToString("yyyy-MM-dd");
        return (ymdInclusive ?? "") + "\uFFFF";
    }

    /// <summary>当月统计（log 条数口径）：每条测试 log（xml_path 唯一）计一次，
    /// 同路径重复入库只保留最新一条；良率 = PASS 条数 / (PASS + FAIL)，中断不计分母；
    /// 产品数 = 当月去重 SN（空 SN 按 xml_path 兜底）。</summary>
    public StatsData FetchMonthlyStats(string stationId, string dateYm)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // 双格式闭区间（@to 上界取 "31" / "-31" 作字符串哨兵），可走 idx_date
        var from8 = dateYm + "01";
        var to8 = dateYm + "31";
        var fromDash = dateYm.Length == 6
            ? $"{dateYm[..4]}-{dateYm[4..6]}-01"
            : dateYm + "-01";
        var toDash = dateYm.Length == 6
            ? $"{dateYm[..4]}-{dateYm[4..6]}-31"
            : dateYm + "-31";
        var where = $"WHERE {TestDateRangeClosed}";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id = @s";
        cmd.CommandText = $@"
            SELECT
                COUNT(CASE WHEN result='PASS' THEN 1 END),
                COUNT(CASE WHEN result='FAIL' THEN 1 END),
                COUNT(CASE WHEN result='INTERRUPTED' THEN 1 END),
                COUNT(DISTINCT COALESCE(NULLIF(TRIM(COALESCE(sn,'')),''), xml_path))
            FROM (
                SELECT result, sn, xml_path
                FROM test_records {where}
                GROUP BY xml_path
            )";
        cmd.Parameters.AddWithValue("@from8", from8);
        cmd.Parameters.AddWithValue("@to8", to8);
        cmd.Parameters.AddWithValue("@fromDash", fromDash);
        cmd.Parameters.AddWithValue("@toDash", toDash);
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
            items.Add(new HourlyStatItem { Hour = h });

        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                WITH ranked AS (
                    {SnLatestCte(stationId)}
                )
                SELECT COALESCE(result,''), COALESCE(batch_timestamp,''), COALESCE(created_at,''), COALESCE(xml_path,'')
                FROM ranked
                WHERE rn = 1 AND result IN ('PASS','FAIL')";
            BindDayParams(cmd, stationId, dateYmd);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    var res = r.GetString(0);
                    var batchTs = r.GetString(1);
                    var created = r.GetString(2);
                    var xml = r.GetString(3);

                    int h = -1;
                    try { h = ExtractHourFromRecord(xml, batchTs, created); }
                    catch { h = -1; }
                    if (h < 0 || h > 23)
                    {
                        if (!TryHourFromRaw(created, out h) && !TryHourFromNormalized(created, out h))
                            continue;
                    }
                    if (h < 0 || h > 23) continue;

                    if (string.Equals(res, "PASS", StringComparison.OrdinalIgnoreCase))
                        items[h].Pass++;
                    else if (string.Equals(res, "FAIL", StringComparison.OrdinalIgnoreCase))
                        items[h].Fail++;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[看板] 小时趋势单行解析失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[看板] 小时趋势查询失败: {ex.Message}");
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
        cmd.CommandText = $@"
            WITH ranked AS (
                {SnLatestCte(stationId)}
            )
            SELECT COALESCE(fail_reason,''), COALESCE(station_id,'')
            FROM ranked
            WHERE rn = 1 AND result = 'FAIL'";
        BindDayParams(cmd, stationId, dateYmd);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stationDist = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int totalFailCount = 0;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            try
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
                    try
                    {
                        key = FailReasonMerger.GetMergedKey(cleaned, true, level);
                        if (!hints.ContainsKey(key))
                        {
                            var pr = FailReasonMerger.Parse(cleaned);
                            if (!string.IsNullOrEmpty(pr.RootCauseHint)) hints[key] = pr.RootCauseHint;
                        }
                    }
                    catch { key = cleaned; }
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
            catch (Exception ex) { Logger.Warning($"[看板] Top5 单行解析失败: {ex.Message}"); }
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

    public List<LiveFailAlert> FetchRecentFailAlerts(string stationId, int limit = 10, string? dateYmd = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH ranked AS (
                {SnLatestCte(stationId)}
            )
            SELECT id, COALESCE(sn,''), COALESCE(station_id,''), COALESCE(model,''),
                   COALESCE(fail_reason,''), COALESCE(tester,''),
                   COALESCE(batch_timestamp,''), COALESCE(created_at,''), COALESCE(xml_path,'')
            FROM ranked
            WHERE rn = 1 AND result = 'FAIL'
            ORDER BY id DESC LIMIT @lim";
        if (!string.IsNullOrEmpty(dateYmd))
            BindDayParams(cmd, stationId, dateYmd);
        else
        {
            cmd.CommandText = $@"
                SELECT id, COALESCE(sn,''), COALESCE(station_id,''), COALESCE(model,''),
                       COALESCE(fail_reason,''), COALESCE(tester,''),
                       COALESCE(batch_timestamp,''), COALESCE(created_at,''), COALESCE(xml_path,'')
                FROM test_records
                WHERE result = 'FAIL'{(string.IsNullOrEmpty(stationId) ? "" : " AND station_id = @s")}
                ORDER BY id DESC LIMIT @lim";
            if (!string.IsNullOrEmpty(stationId))
                cmd.Parameters.AddWithValue("@s", stationId);
        }
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
        if (TryHourFromNormalized(TimeUtil.ExtractFileNameTime(xmlPath), out var h)) return h;
        if (TryHourFromNormalized(TimeUtil.Normalize(batchTs), out h)) return h;
        if (TryHourFromRaw(batchTs, out h)) return h;
        if (TryHourFromNormalized(TimeUtil.Normalize(createdAt), out h)) return h;
        if (TryHourFromRaw(createdAt, out h)) return h;
        return -1;
    }

    private static bool TryHourFromNormalized(string ts, out int hour)
    {
        hour = -1;
        if (string.IsNullOrEmpty(ts) || ts.Length < 13) return false;
        return int.TryParse(ts.AsSpan(11, 2), out hour) && hour >= 0 && hour <= 23;
    }

    private static bool TryHourFromRaw(string ts, out int hour)
    {
        hour = -1;
        if (string.IsNullOrEmpty(ts) || ts.Length < 10) return false;
        int idx = ts.IndexOf('T');
        if (idx < 0) idx = ts.IndexOf(' ');
        if (idx < 0 || idx + 2 >= ts.Length) return false;
        return int.TryParse(ts.AsSpan(idx + 1, 2), out hour) && hour >= 0 && hour <= 23;
    }

    public static string FormatLogTime(string xmlPath, string batchTs, string createdAt)
    {
        try
        {
            if (!string.IsNullOrEmpty(xmlPath))
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(xmlPath);
                var fromFile = TimeUtil.ExtractFileNameTime(fileName);
                if (!string.IsNullOrEmpty(fromFile)) return fromFile;
            }
            var fromBatch = TimeUtil.Normalize(batchTs);
            if (!string.IsNullOrEmpty(fromBatch)) return fromBatch;
            var fromCreated = TimeUtil.Normalize(createdAt);
            if (!string.IsNullOrEmpty(fromCreated)) return fromCreated;
        }
        catch { }
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public int CreateMaintenance(MaintenanceRecord m)
    {
        int id;
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
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
            id = Convert.ToInt32(cmd.ExecuteScalar());
        }
        MirrorMaintenanceCsv(id);
        return id;
    }

    /// <summary>维修日志与库同目录，一条记录一行。写失败不影响入库。</summary>
    private void MirrorMaintenanceCsv(int id, bool remove = false)
    {
        try
        {
            if (remove) { MaintenanceExporter.RemoveLogRow(_dbPath, id); return; }
            var saved = GetMaintenance(id);
            if (saved != null) MaintenanceExporter.UpsertLogRow(_dbPath, saved);
        }
        catch (Exception ex) { Logger.Warning($"维修日志写入失败: {ex.Message}"); }
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

    /// <summary>审计修复：超期告警专用——按创建时间「升序」取未闭环待办。
    /// 原实现用 ListMaintenance("", 5000)（按 updated_at 倒序取最近 5000 条）后在内存筛超期：
    /// 未被处理的老待办 updated_at == created_at，排序落在最末，表一超过 5000 行就正好被 LIMIT 截掉——
    /// 最该告警的超期项反而静默消失。改为 SQL 侧按状态过滤 + 创建时间升序。</summary>
    public List<MaintenanceRecord> ListUnclosedMaintenanceAsc(int limit = 5000)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, station_id, equipment_model, equipment_sn, fail_item, fail_reason,
            severity, status, resolver, resolution, notes, created_at, updated_at
            FROM maintenance_records
            WHERE status IN (@open, @doing)
            ORDER BY COALESCE(NULLIF(created_at,''),'') ASC, id ASC
            LIMIT @lim";
        cmd.Parameters.AddWithValue("@open", MaintenanceMeta.DefaultStatus);
        cmd.Parameters.AddWithValue("@doing", "in_progress");
        cmd.Parameters.AddWithValue("@lim", limit <= 0 ? 5000 : limit);
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
        // B14：读旧状态与写库合并为单连接单事务——原实现两个连接，并发状态变更会丢更新
        //（A 读到 open、B 改成 resolved、A 再写 in_progress，B 的变更被静默覆盖）。
        string from;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT status FROM maintenance_records WHERE id=@id";
            sel.Parameters.AddWithValue("@id", id);
            from = sel.ExecuteScalar() as string ?? "";
        }
        if (string.Equals(from, status, StringComparison.OrdinalIgnoreCase)) return true;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE maintenance_records SET status=@s, updated_at=datetime('now','localtime') WHERE id=@id";
            cmd.Parameters.AddWithValue("@s", status);
            cmd.Parameters.AddWithValue("@id", id);
            if (cmd.ExecuteNonQuery() == 0) return false;
        }
        tx.Commit();
        MirrorMaintenanceCsv(id);
        var snapshot = GetMaintenance(id);
        if (snapshot != null) NotifyStatusChanged(snapshot, from, status);
        return true;
    }

    public bool UpdateMaintenance(MaintenanceRecord m)
    {
        // B14：同上，读旧状态与写库单事务
        string from;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT status FROM maintenance_records WHERE id=@id";
            sel.Parameters.AddWithValue("@id", m.Id);
            from = sel.ExecuteScalar() as string ?? "";
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
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
        tx.Commit();
        MirrorMaintenanceCsv(m.Id);
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
            // 审计修复：id 游标回退到 MAX(id) 是本产品「已用自检断言锁定」的行为（删掉最大的 id 后新记录
            // 会复用该 id、不填空号），因此保留不动。但 ReconcileTodoStates 是靠「maintenance_id 不在
            // maintenance_records 里」判断「记录已删」，id 一旦被复用该判定就失效 → 待办会静默挂到一条
            // 无关的新记录上。这里在删除的同一事务内「显式解绑」，直接兑现界面「删掉本记录后待办自动
            // 回到未确认」的承诺，不再依赖上述启发式。
            using (var detach = conn.CreateCommand())
            {
                detach.Transaction = tx;
                detach.CommandText = @"
                    UPDATE todo_items
                       SET state='pending', maintenance_id=NULL, resolved_at=NULL,
                           updated_at=datetime('now','localtime')
                     WHERE maintenance_id=@id";
                detach.Parameters.AddWithValue("@id", id);
                detach.ExecuteNonQuery();
            }

            using var seq = conn.CreateCommand();
            seq.Transaction = tx;
            seq.CommandText = @"
                UPDATE sqlite_sequence
                   SET seq = (SELECT COALESCE(MAX(id), 0) FROM maintenance_records)
                 WHERE name = 'maintenance_records'";
            seq.ExecuteNonQuery();
        }

        tx.Commit();
        if (affected > 0) MirrorMaintenanceCsv(id, remove: true);
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

        long rescanPending;
        using (var rc = conn.CreateCommand())
        {
            rc.CommandText = "SELECT COUNT(*) FROM todo_rescan_ids";
            rescanPending = Convert.ToInt64(rc.ExecuteScalar() ?? 0L);
        }

        var groups = new Dictionary<(string, string), TodoAgg>();
        if (maxId > watermark || rescanPending > 0)
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
                   AND (id > @wm AND id <= @max OR id IN (SELECT id FROM todo_rescan_ids))";
            c.Parameters.AddWithValue("@wm", watermark);
            c.Parameters.AddWithValue("@max", maxId);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var testDate = r.IsDBNull(4) ? "" : r.GetString(4);
                if (string.CompareOrdinal(NormalizeYmdKey(testDate), cutoff) < 0) continue;
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

            // 审计修复：重扫集合已消费完毕，同一事务内清空（崩溃则下次重来，幂等）
            using (var clr = conn.CreateCommand())
            {
                clr.Transaction = tx;
                clr.CommandText = "DELETE FROM todo_rescan_ids";
                clr.ExecuteNonQuery();
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
        // 审计修复：四条 UPDATE 属于同一个多语句命令，原本没有事务——第 2~4 条遇 SQLITE_BUSY/中断会让
        // 待办状态只改一半（最长到次日维护才自愈），期间看板缺卡/错卡且无任何报错。
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // B8：终态集合收口 MaintenanceMeta（DoneStatus/LegacyClosed），与状态机单一来源同步
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
                   IN (@done,@legacy);

            -- 记录仍活跃 -> ack
            UPDATE todo_items
               SET state='ack', resolved_at=NULL, updated_at=datetime('now','localtime')
             WHERE maintenance_id IS NOT NULL
               AND (SELECT status FROM maintenance_records WHERE id=todo_items.maintenance_id)
                   NOT IN (@done,@legacy);

            -- 处理完之后又出现新不良 -> 复发，回到未确认
            UPDATE todo_items
               SET state='pending', maintenance_id=NULL, resolved_at=NULL,
                   updated_at=datetime('now','localtime')
             WHERE state='resolved' AND COALESCE(resolved_at,'')<>''
               AND COALESCE(last_seen,'') > resolved_at;
        ";
        cmd.Parameters.AddWithValue("@done", MaintenanceMeta.DoneStatus);
        cmd.Parameters.AddWithValue("@legacy", MaintenanceMeta.LegacyClosed);
        cmd.ExecuteNonQuery();
        tx.Commit();
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
            // 审计修复：区间模式必须先取全量候选再按区间过滤。原实现把「累计 fail_count 前 300 条」当候选，
            // 区间内高发但累计计数低的待办会被整体丢弃（区间视图反而比累计视图还少）。
            // 非区间仍由 SQL 截断；区间模式传 -1（SQLite LIMIT -1 = 不限制），过滤后再按 limit 截断。
            cmd.Parameters.AddWithValue("@limit", (from != null || to != null) ? -1 : limit);
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
                if (from != null || to != null) where += " AND " + TestDateRangeClosed;
                cmd.CommandText = $@"
                    SELECT fail_reason, station_id, COUNT(*),
                           MIN(COALESCE(NULLIF(batch_timestamp,''),
                                        substr(replace(test_date,'-',''),1,4)||'-'||
                                        substr(replace(test_date,'-',''),5,2)||'-'||
                                        substr(replace(test_date,'-',''),7,2)||' 00:00:00')),
                           MAX(COALESCE(NULLIF(batch_timestamp,''),
                                        substr(replace(test_date,'-',''),1,4)||'-'||
                                        substr(replace(test_date,'-',''),5,2)||'-'||
                                        substr(replace(test_date,'-',''),7,2)||' 00:00:00'))
                      FROM test_records {where}
                     GROUP BY fail_reason, station_id";
                if (from != null || to != null)
                {
                    var a = from ?? DateTime.Today.AddYears(-50);
                    var b = to ?? DateTime.Today;
                    BindRangeDual(cmd, a.ToString("yyyyMMdd"), b.ToString("yyyyMMdd"));
                }
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string key;
                    try { key = TodoGrouping.MergeKeyOf(r.IsDBNull(0) ? "" : r.GetString(0)); }
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
            // 区间模式的截断放在过滤之后（SQL 端已传 -1 不限制）
            if (limit > 0 && list.Count > limit) list = list.Take(limit).ToList();
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

        // B14：建维修记录与挂待办原先是两个独立连接，中断会留下孤儿维修记录。
        // 挂接失败时补偿删除刚建的记录（DeleteMaintenance 幂等），把窗口缩到「通知已发但记录已回滚」。
        try
        {
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
        }
        catch
        {
            try { DeleteMaintenance(id); } catch { }
            throw;
        }
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

    public long InsertLocalDeviceSample(double cpuUsage, double memUsedPct, double diskFreeGb, string? ts = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO device_samples_local (ts, cpu_usage, mem_used_pct, disk_free_gb)
            VALUES (@ts, @cpu, @mem, @disk);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@ts", string.IsNullOrEmpty(ts) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : ts);
        cmd.Parameters.AddWithValue("@cpu", Math.Round(cpuUsage, 2));
        cmd.Parameters.AddWithValue("@mem", Math.Round(memUsedPct, 2));
        cmd.Parameters.AddWithValue("@disk", Math.Round(diskFreeGb, 2));
        return Convert.ToInt64(cmd.ExecuteScalar());
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
        if (days > 0) { cutoff = DateTime.Today.AddDays(-days).ToString("yyyyMMdd"); where += " AND " + TestDateGeNorm; }
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

public class ModelDayStat
{
    public string Model = "";
    public int Pass, Fail, Interrupted;
    public int Total => Pass + Fail;
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
    /// <summary>本自然月失败明细次数（看板加载时填入，不入库）。</summary>
    public int MonthCount;

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

public class HourlyStatItem
{
    public int Hour { get; set; }
    public int Pass { get; set; }
    public int Fail { get; set; }
    public int Total => Pass + Fail;
    public double YieldRate => StatsUtil.YieldPctDivFirst(Pass, Fail);
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

