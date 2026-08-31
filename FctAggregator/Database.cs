using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>SQLite 数据库: 建表、去重入库、统计。partial（v3.5.0 全局待办池方法在文件末尾分部声明）。</summary>
public sealed partial class Database
{
    private readonly string _connString;
    private readonly string _dbPath;

    /// <summary>维修记录状态变更事件：参数 = (记录, 旧状态key, 新状态key)。引擎订阅后推送飞书。</summary>
    public event Action<MaintenanceRecord, string, string>? MaintenanceStatusChanged;

    private void NotifyStatusChanged(MaintenanceRecord rec, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        try { MaintenanceStatusChanged?.Invoke(rec, from, to); }
        catch (Exception ex) { Logger.Warning($"状态变更回调异常: {ex.Message}"); }
    }

    /// <summary>test_records 成功写入事件：参数 = (记录, 数据库自增id)，仅含真正插入的行。
    /// 聚合推送器订阅后推送事件文件；本事件回调异常不影响入库主流程。</summary>
    public event Action<List<(TestRecord Rec, long Id)>>? RecordsInserted;

    private void NotifyRecordsInserted(List<(TestRecord, long)> rows)
    {
        if (rows.Count == 0) return;
        try { RecordsInserted?.Invoke(rows); }
        catch (Exception ex) { Logger.Warning($"插入事件回调异常: {ex.Message}"); }
    }

    public Database(string dbPath)
    {
        _dbPath = dbPath;
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
        // 多线程并发写（历史扫描 BatchInsert + 实时事件 Task.Run + SyncTodoItems）
        // 默认 busy_timeout=0 会立刻锁失败; WAL 让读写不互斥, 写之间靠 busy 等待协调
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

            // v2.3.0: 维修人员名单。与历史记录里用过的名字共同组成下拉候选。
            // 名字大小写不敏感唯一 -> 同一个人不会因大小写出两条
            using var cmd3 = conn.CreateCommand();
            cmd3.CommandText = @"
                CREATE TABLE IF NOT EXISTS resolvers (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    created_at TEXT DEFAULT (datetime('now','localtime'))
                );
            ";
            cmd3.ExecuteNonQuery();

            // 域名1 解析失败日志（v3.19.0）：记录解析器判定失败/异常的 xml，分类码用于排障。
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
            // v2.4.0: 待办忽略名单。
            // ⚠ v2.5.1 起**已停用**：待办来自真实不良，不允许忽略/删除，必须确认处理。
            // 建表语句保留（仅为旧库兼容与回退到 v2.4/v2.5 时不报错），但**不再读也不再写**。
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

            // v2.6.0: 待办登记表（**永久保留**）。
            // 与 v2.4/v2.5 的“实时聚合视图”不同：一旦被扫到就落库，
            // 即使它的不良滑出了“近一个月”扫描窗口，待办仍在（除非被处理完）。
            //   group_key : TodoGrouping.KeyOf() 归一化后的合并键（同类测试项合一张卡）
            //   fail_count: 累计不良次数（靠 app_meta 里的水位线增量累加，不重复计）
            //   state     : pending 未确认 / ack 已确认(有活跃维修记录) / resolved 已处理完
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

            // v3.5.0 全局待办池：跨机同步状态/认领。以 (origin_machine, todo_id) 为全局键，
            // 不改动既有 maintenance_records/todo_items 结构（兼容旧库与既有待办逻辑）。
            // version 单调自增用于合并裁决（认领抢占 + last-write-wins）。
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
        }
        MigrateClosedStatus();
    }

    /// <summary>
    /// v2.2.0 迁移: 维修记录状态体系由 4 个合并为 5 个(新增 unknown/investigating,
    /// 取消 closed)，把库里历史的 status='closed' 一次性改写为 'resolved'。
    /// 幂等(无 closed 行时直接返回)。改写前先备份 db 文件；备份失败则跳过迁移
    /// —— 此时 UI 仍能通过 MaintenanceMeta 的 legacy 映射把 closed 显示为「已完成」，不影响使用。
    /// </summary>
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
                // 只改 status, 不动 updated_at —— 保留记录真实的最后人工更新时间
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

    /// <summary>迁移前备份 db 文件为 {db}.bak-yyyyMMdd。已存在同日备份则视为成功。</summary>
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
            // WAL 模式下未 checkpoint 的数据在 -wal 文件里, 先落盘再复制, 否则备份会缺数据
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cmd.ExecuteNonQuery();
            }
            // 释放连接池持有的文件句柄, 确保 File.Copy 拿到完整快照
            SqliteConnection.ClearAllPools();
            File.Copy(_dbPath, bak);
            Logger.Info($"[迁移] 数据库已备份: {Path.GetFileName(bak)}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[迁移] 数据库备份失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 机台本地库每日备份（v3.10.0 P1-2）：checkpoint → 复制 {db}.bak-yyyyMMdd → 只留
    /// <see cref="BackupKeepDays"/> 份。同日幂等。供每日调度（MainForm 定时器/后续服务化）调用。
    /// </summary>
    public const int BackupKeepDays = 7;
    public string? BackupDaily()
    {
        try
        {
            if (!File.Exists(_dbPath)) return null;
            var bak = $"{_dbPath}.bak-{DateTime.Now:yyyyMMdd}";
            if (File.Exists(bak)) return null;   // 当日已备份
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
                try { File.Delete(f); } catch { }
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

    /// <summary>批量插入(忽略重复 xml_path)。返回插入数。
    /// 真正插入的行通过 <see cref="RecordsInserted"/> 通知(带自增 id，供聚合推送做 seq)。
    /// v3.5.3：改用 INSERT ... RETURNING id（SQLite 3.35+，e_sqlite3 2.1.12 = 3.53.3），
    /// 「插入 + 取自增 id」合并成一条语句，历史扫描大事务里每行省掉一次额外命令对象与往返。</summary>
    public int BatchInsert(IEnumerable<TestRecord> records)
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
             fail_reason, tester, panel_status, batch_timestamp, has_fail_items, file_size)
            VALUES (@station,@model,@cat,@date,@sn,@result,@path,
                    @reason,@tester,@panel,@ts,@hasfail,@size)
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
            // RETURNING 只对真正插入的行出结果：OR IGNORE 跳过的行 reader 里没有行
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                inserted++;
                insertedRows.Add((rec, r.GetInt64(0)));
            }
        }
        tx.Commit();
        NotifyRecordsInserted(insertedRows);
        return inserted;
    }

    public int InsertOne(TestRecord rec) => BatchInsert(new[] { rec });
    /// <summary>记录一次解析失败（域名1，v3.19.0）。失败不影响采集主流程，仅留痕。</summary>
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
                COUNT(CASE WHEN result='INTERRUPTED' THEN 1 END),
                COUNT(DISTINCT sn)
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

    // ==================== 维修记录 ====================
    public int CreateMaintenance(MaintenanceRecord m)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // created_at 允许手工指定(记录日期); updated_at 跟随同一取值,
        // 否则录入往期日期时 updated_at 会是"现在", 看板按更新时间排序会把老记录顶到最前。
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

    /// <summary>
    /// 查询维修记录。statusFilter 为空表示全部。
    /// 排序: 最后更新时间倒序(看板语义: 最近动过的置顶), updated_at 缺失时回落 created_at。
    /// </summary>
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

    /// <summary>
    /// 各状态的真实总数(不受列表 LIMIT 影响), 供看板列头徽章显示。
    /// key 为原始入库值, 调用方需用 MaintenanceMeta.Normalize 归并 legacy 'closed'。
    /// </summary>
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

    /// <summary>只改状态(看板拖放用)。状态实际变化时触发 <see cref="MaintenanceStatusChanged"/>。</summary>
    public bool UpdateMaintenanceStatus(int id, string status)
    {
        string from;
        using (var conn = Open())
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT status FROM maintenance_records WHERE id=@id";
            sel.Parameters.AddWithValue("@id", id);
            from = sel.ExecuteScalar() as string ?? "";
            if (string.Equals(from, status, StringComparison.OrdinalIgnoreCase)) return true;   // 无变化不算失败
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

    /// <summary>
    /// 全字段更新(编辑表单 / 拖到「已完成」时补填维修人与措施用)。
    /// 含 created_at(编辑表单允许改「记录日期」); updated_at 置为当前时间。
    /// 状态实际变化时触发 <see cref="MaintenanceStatusChanged"/>。
    /// </summary>
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

    /// <summary>按 id 取单条维修记录；不存在返回 null。</summary>
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

    /// <summary>
    /// 删除维修记录，并**回收自增 id**。
    ///
    /// 建表用的是 `id INTEGER PRIMARY KEY AUTOINCREMENT`，SQLite 会把已用过的最大值
    /// 记在 `sqlite_sequence` 里并**永不复用** —— 于是「就一条记录(id=1)，删了再新建变成 id=2」。
    /// 维修记录的 id 是给人看的流水号(卡片右上角 #N、导出第一列)，不被其它表引用，
    /// 所以删完把游标回抨到当前 MAX(id)：
    ///   ① 删空了 -> 下次新建回到 1；
    ///   ② 删的是最后一条 -> 下次新建接着用那个号；
    ///   ③ 删的是中间那条 -> 不回填空号(避开与已导出报表上的旧 #N 撞车)。
    /// </summary>
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
            // sqlite_sequence 里没这张表的行时(从未插入过) UPDATE 影响 0 行, 无害
            seq.CommandText = @"
                UPDATE sqlite_sequence
                   SET seq = (SELECT COALESCE(MAX(id), 0) FROM maintenance_records)
                 WHERE name = 'maintenance_records'";
            seq.ExecuteNonQuery();
        }

        tx.Commit();
        return affected > 0;
    }

    // ==================== 待办登记表（v2.6.0：扫描窗口 + 大项合并 + 永久保留） ====================

    /// <summary>增量同步的水位线：已并入待办的 test_records 最大 id。</summary>
    private const string TodoWatermarkKey = "todo_sync_last_id";

    /// <summary>待办卡上限（登记表可以很大，但一次只呈现最需要处理的这些）。</summary>
    public const int TodoViewLimit = 300;

    public string? GetMeta(string key)
    {
        using var conn = Open();
        return GetMeta(conn, key);
    }

    /// <summary>写入键值元数据（app_meta 表）。更新器/水位线等复用。</summary>
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

    /// <summary>
    /// 把「近 <paramref name="scanDays"/> 天」内的新 FAIL 并入待办登记表。
    ///
    /// 三条关键规则：
    ///   1. **只扫近 N 天**（默认 30）—— 老库里几年的历史不良不会一次性灌成几千张卡；
    ///   2. **增量**：靠 `app_meta.todo_sync_last_id` 水位线，只处理 id 更大的行，次数不会重复累加；
    ///   3. **永久保留**：登记过的待办行只改状态、**从不删除**，所以哪怕它的不良早已滑出扫描窗口，
    ///      待办仍在（用户可以用时间区间去筛，但不会“到期消失”）。
    ///
    /// 同类测试项按 <see cref="TodoGrouping.KeyOf"/> 合并为一条（大项合并）。
    /// 返回本次**新登记**的待办条数。
    /// </summary>
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

        // 分组累加：key = (group_key, station_id)
        var groups = new Dictionary<(string, string), TodoAgg>();
        if (maxId > watermark)
        {
            // 被用户显式删除的 (大项, 机台) 永久不再登记（v3.3.0）
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
                // 旧日期FAIL仍推进水位但不建待办，防迟到数据永久丢失（C-03 有效水位策略）
                var testDate = r.IsDBNull(4) ? "" : r.GetString(4);
                if (string.CompareOrdinal(testDate, cutoff) < 0) continue;
                var item = r.IsDBNull(0) ? "" : r.GetString(0);
                // 单条隔离：某一条异常格式的 fail_reason 不能拖垮整批待办登记（v3.3.0）
                string key;
                try { key = TodoGrouping.KeyOf(item); }
                catch (Exception ex) { Logger.Warning($"[待办] 合并键计算失败，跳过该项: {ex.Message} | {item}"); continue; }
                if (key.Length == 0) continue;
                var station = r.IsDBNull(1) ? "" : r.GetString(1);
                if (dismissed.Contains((key, station))) continue;   // 已删除的待办不再复现
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
                // 已有则合并累加，没有则新建
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
                if (variants.Count > 40) variants = variants.Take(40).ToList();   // 防止无界膨胀
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

            // 所有 id>watermark 的行已检查（旧日期已显式continue，不会误丢），水位可安全前移至 maxId
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

    /// <summary>时间戳统一成 `yyyy-MM-dd HH:mm:ss`，否则字符串比较会因 `T` / 纯日期而错乱。
    /// 委托 <see cref="TimeUtil.Normalize"/>（统一入口，支持 8/14/17 位数字与 ISO 带时区格式）。</summary>
    internal static string NormalizeTs(string ts) => TimeUtil.Normalize(ts);

    /// <summary>
    /// 状态对账（幂等，每次同步后跑）：
    ///   · 关联的维修记录被删 → 回到 pending（**待办删不掉**）
    ///   · 关联记录仍活跃      → ack（不在待办列显示）
    ///   · 关联记录已完成      → resolved（记 resolved_at）
    ///   · 已完成之后又出新不良 → 回到 pending（复发重现）
    /// </summary>
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

    /// <summary>
    /// 待办视图：未确认（pending）的待办项，**按 fail 次数倒序**（= 处理优先级）。
    ///
    /// <paramref name="from"/> / <paramref name="to"/> 给定时按时间区间筛：
    /// 只保留「该区间内真实出现过不良」的项，且 <see cref="TodoItem.RangeCount"/> = 区间内次数
    /// （区间统计现算，直接从 test_records 聚合，不依赖登记表）。
    /// 两个都传 null = 不限区间，显示全部未处理的待办（永久保留的都在）。
    /// </summary>
    public List<TodoItem> ListTodoView(DateTime? from = null, DateTime? to = null, int limit = TodoViewLimit)
    {
        using var conn = Open();

        // 1) 登记表里所有未确认项
        var list = new List<TodoItem>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT id, group_key, station_id, title, COALESCE(model,''), COALESCE(variants,''),
                       variant_count, fail_count, COALESCE(first_seen,''), COALESCE(last_seen,'')
                  FROM todo_items
                 WHERE state='pending'
                 ORDER BY fail_count DESC, last_seen DESC";
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

        // 2) 区间统计（可选）
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
                if (!ranged.TryGetValue((it.GroupKey, it.StationId), out var v)) continue;  // 区间内没出现
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

        // 3) 按 fail 次数排优先级（区间模式下按区间内次数）
        list = list.OrderByDescending(x => x.SortCount)
                   .ThenByDescending(x => x.LastSeen, StringComparer.Ordinal)
                   .Take(limit)
                   .ToList();
        return list;
    }

    /// <summary>未确认待办条数（列头徽章用，比拉全表便宜）。</summary>
    public int CountPendingTodos()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM todo_items WHERE state='pending'";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// ⚠ **已废弃（v2.5.1）**：待办来自真实不良，不得忽略/删除。
    /// 保留方法仅为不破坏旧调用方的编译；写入仍会发生，但待办视图已不再读它，
    /// 所以**调了也不会让待办消失**。新代码请勿使用。
    /// </summary>
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

    /// <summary>
    /// 确认待办 → 用调用方填好的完整记录建维修记录并与待办行**双向关联**，返回维修记录 ID。
    ///
    /// 关联之后：
    ///   · 待办行 state=ack，不再出现在待办列；
    ///   · 记录被拖到「已完成」→ state=resolved；同故障项再出不良 → 自动回到 pending（复发）；
    ///   · 记录被删 → 自动回到 pending。
    ///
    /// v3.3.0：确认即进入状态流（无 → 目标状态），**无论落到哪一列都触发状态变更推送**。
    /// </summary>
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

        // 确认 = 从「未确认」进入状态流，等同状态变更，总是推送（含确认后落在「待办」列）
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

    /// <summary>确认待办（轻量：只给维修人/严重度/状态，其余字段走默认）。保留给自检与旧调用方。</summary>
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

    /// <summary>
    /// 删除一条待办（用户显式删除，v3.3.0 起支持）。
    /// 行删除后该故障项不再出现在待办列；若它关联了维修记录，记录本身不受影响。
    /// 删除会留下**永久标记**（dismissed_todos），此后同类新不良也不会再自动登记 —— 删除是永久的。
    /// </summary>
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
        // 永久标记：该 (大项, 机台) 不再自动登记新不良（含之后才出现的变体）
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

    /// <summary>
    /// 每个原始测试项各自的真实不良次数（预览里逐项列次数用）。
    /// 一次 IN 查询搞定，不循环发 SQL。
    /// </summary>
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

    /// <summary>根据维修记录反查关联的待办行（预览里拿权威的合并清单与次数）。</summary>
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

    /// <summary>
    /// 某故障项在 test_records 里还有多少条真实 FAIL。
    /// 用于删维修记录时提醒用户：删了它这个故障项会重新回到「待办」列。
    /// </summary>
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

    // ==================== 维修人员名单 ====================

    /// <summary>名单里的人（按名字排）。</summary>
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

    /// <summary>
    /// 下拉候选 = **名单** ∪ **历史记录里用过的名字**（名单在前，大小写不敏感去重）。
    /// 这样既能提前录班组名单，也不会丢掉已经录过的人。
    /// </summary>
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

    /// <summary>加人。已存在（不分大小写）或名字为空时返回 false。</summary>
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

    /// <summary>从名单里删人。**不动已有维修记录**（历史记录里的名字照旧）。</summary>
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

    /// <summary>
    /// 改名。syncRecords=true 时把历史维修记录里的这个名字一并改掉（治错别字）。
    /// 返回同步改动的记录数（不同步时为 0）。
    /// </summary>
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
            // 目标名已在名单里 -> 先删旧名，避开 UNIQUE 冲突
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
            // 一条记录可能有多人，不能整列赋值 —— 逐行拆开、只换那个人、再拼回去
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

    /// <summary>
    /// 某个维修人在多少条维修记录里出现过（删人前提示用）。
    /// **成员级比较** —— 不能用 LIKE，否则「张三」会误中「张三丰」。
    /// </summary>
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

    /// <summary>
    /// 维修记录里用过的维修人（按使用次数倒序）。
    /// 一条记录可能有**多人**（`张三、李四`），这里会拆开按**人**统计，
    /// 否则候选里会出现「张三、李四」这种组合项。
    /// </summary>
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

    /// <summary>
    /// FAIL 记录的「可作为维修依据」的来源行 —— **不含 SN**（维修记录不再关心 SN）。
    /// fail_reason 只是**首个**失败项；要拿全部失败项需要再解析 xml_path（由 UI 侧按需深扫）。
    /// </summary>
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

    /// <summary>聚合推送续推用：取 id 大于 afterId 的 FAIL 记录（按 id 升序，最多 limit 条）。</summary>
    public List<(TestRecord Rec, long Id)> FetchFailRecordsAfter(long afterId, int limit = 5000)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, station_id, model, category, test_date, sn, result, xml_path,
                   fail_reason, tester, panel_status, batch_timestamp, has_fail_items, file_size
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
            };
            list.Add((rec, r.GetInt64(0)));
        }
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
    /// <summary>当日产品数（SN 去重，FetchDailyStats 专用，v3.1.0.3 主页改版）。</summary>
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

/// <summary>一条 FAIL 记录里可用于「维修依据」的信息（**故意不含 SN**）。</summary>
public class FailItemSource
{
    public string FirstFailItem = "";   // test_records.fail_reason（首个失败项）
    public string Model = "";
    public string StationId = "";
    public string Timestamp = "";
    public string TestDate = "";
    public string XmlPath = "";
}

/// <summary>
/// 一条待办（= `todo_items` 一行）。
///
/// 它不是单个测试项，而是**合并后的大项**：同类测试项（只差编号/通道/位号）
/// 经 <see cref="TodoGrouping.KeyOf"/> 归一化后归为一条，<see cref="Variants"/> 里留着原始项名。
/// </summary>
public class TodoItem
{
    public int Id;
    /// <summary>合并键（归一化后的测试项）。</summary>
    public string GroupKey = "";
    /// <summary>展示名（代表性测试项，已去步骤号）。</summary>
    public string Title = "";
    public string StationId = "";
    public string Model = "";
    /// <summary>被合并的原始测试项名。</summary>
    public readonly List<string> Variants = new();
    /// <summary>合并了多少个原始测试项。</summary>
    public int VariantCount = 1;
    /// <summary>累计不良次数（永久累加，不因扫描窗口滑动而减少）。</summary>
    public int TotalCount;
    /// <summary>当前选定时间区间内的不良次数（不限区间时等于累计）。</summary>
    public int RangeCount;
    /// <summary>排优先级用的次数：有区间就用区间内次数，否则用累计。</summary>
    public int SortCount => RangeCount > 0 ? RangeCount : TotalCount;
    public string FirstSeen = "";
    public string LastSeen = "";
    public string RangeFirstSeen = "";
    public string RangeLastSeen = "";
    /// <summary>pending 未确认 / ack 已确认 / resolved 已处理完。</summary>
    public string State = "pending";

    /// <summary>优先级中文（按 fail 次数）。</summary>
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
    public string Severity = "major";   // critical/major/minor
    public string Status = "open";       // unknown/investigating/open/in_progress/resolved (legacy: closed)
    public string Resolver = "";
    public string Resolution = "";
    public string Notes = "";
    public string CreatedAt = "";
    public string UpdatedAt = "";

    /// <summary>浅拷贝，供编辑对话框「取消」时不污染原对象。</summary>
    public MaintenanceRecord Clone() => (MaintenanceRecord)MemberwiseClone();
}

// ==================== 全局待办池（v3.5.0 去中心化跨机同步） ====================

/// <summary>跨机待办同步状态行（todo_sync_state 一行）。</summary>
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
    /// <summary>本地版号自增（每次本地变更 +1），供 <see cref="TodoSync"/> 生成广播事件版本。</summary>
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

    /// <summary>应用邻居广播来的待办事件：按 (origin_machine, todo_id) upsert，
    /// version 更大才覆盖（last-write-wins）；相同版本时认领态优先（owner 非空的胜）。</summary>
    public void ApplyRemoteTodoEvent(TodoEvent ev)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            // 读当前本地版本
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
            // 裁决：远端版本更高，或版本相同但本地未认领而远端已认领（认领抢占）
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

    /// <summary>
    /// 源机检索（P4 D5）：按 SN/型号/结果/日期过滤本地 test_records 全量（含 PASS）。
    /// 索引覆盖：sn / test_date / result 均有索引（Init 建表），历时扫描不走全表。
    /// 调用方（/api/mesh/query/local / 聚合扇出）应做参数化，防注入。
    /// </summary>
    public List<MeshQueryService.QueryItem> QueryTestRecords(string localMachine, MeshQueryService.QueryRequest req)
    {
        var where = new List<string>();
        var ps = new List<(string name, object val)>();
        if (!string.IsNullOrWhiteSpace(req.Machine))
        {
            // 本机只有自己的 station_id，过滤时按 station_id 精确匹配（大小写不敏感 LIKE 转 =）
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

    /// <summary>查询跨机待办同步状态（供看板展示全产线待办 owner/state）。</summary>
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
