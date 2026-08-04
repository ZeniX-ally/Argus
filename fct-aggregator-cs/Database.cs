using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>SQLite 数据库: 建表、去重入库、统计。</summary>
public class Database
{
    private readonly string _connString;
    private readonly string _dbPath;

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

    /// <summary>只改状态(看板拖放用)。</summary>
    public bool UpdateMaintenanceStatus(int id, string status)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE maintenance_records SET status=@s, updated_at=datetime('now','localtime') WHERE id=@id";
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 全字段更新(编辑表单 / 拖到「已完成」时补填维修人与措施用)。
    /// 含 created_at(编辑表单允许改「记录日期」); updated_at 置为当前时间。
    /// </summary>
    public bool UpdateMaintenance(MaintenanceRecord m)
    {
        using var conn = Open();
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
        return cmd.ExecuteNonQuery() > 0;
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
            using var c = conn.CreateCommand();
            c.CommandText = @"
                SELECT fail_reason, station_id, COALESCE(model,''),
                       COALESCE(NULLIF(batch_timestamp,''), test_date)
                  FROM test_records
                 WHERE result='FAIL'
                   AND fail_reason IS NOT NULL AND TRIM(fail_reason) <> ''
                   AND id > @wm AND id <= @max
                   AND test_date >= @cut";
            c.Parameters.AddWithValue("@wm", watermark);
            c.Parameters.AddWithValue("@max", maxId);
            c.Parameters.AddWithValue("@cut", cutoff);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var item = r.IsDBNull(0) ? "" : r.GetString(0);
                var key = TodoGrouping.KeyOf(item);
                if (key.Length == 0) continue;
                var station = r.IsDBNull(1) ? "" : r.GetString(1);
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

            SetMeta(conn, TodoWatermarkKey, maxId.ToString(), tx);
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

    /// <summary>时间戳统一成 `yyyy-MM-dd HH:mm:ss`，否则字符串比较会因 `T` / 纯日期而错乱。</summary>
    private static string NormalizeTs(string ts)
    {
        ts = (ts ?? "").Trim();
        if (ts.Length == 0) return "";
        if (ts.Length == 8 && ts.All(char.IsDigit))                  // yyyyMMdd
            return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} 00:00:00";
        if (DateTime.TryParse(ts, out var dt)) return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return ts.Replace('T', ' ');
    }

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
                           MIN(COALESCE(NULLIF(batch_timestamp,''), test_date)),
                           MAX(COALESCE(NULLIF(batch_timestamp,''), test_date))
                      FROM test_records {where}
                     GROUP BY fail_reason, station_id";
                if (from != null) cmd.Parameters.AddWithValue("@a", from.Value.ToString("yyyyMMdd"));
                if (to != null) cmd.Parameters.AddWithValue("@b", to.Value.ToString("yyyyMMdd"));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = TodoGrouping.KeyOf(r.IsDBNull(0) ? "" : r.GetString(0));
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
    /// 确认待办 → 建一条维修记录并与待办行**双向关联**，返回维修记录 ID。
    ///
    /// 关联之后：
    ///   · 待办行 state=ack，不再出现在待办列；
    ///   · 记录被拖到「已完成」→ state=resolved；同故障项再出不良 → 自动回到 pending（复发）；
    ///   · 记录被删 → 自动回到 pending（**待办删不掉**）。
    /// </summary>
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
        var id = CreateMaintenance(rec);

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
    public List<FailItemSource> FailItemSources(string stationId = "", int limit = 2000)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE result='FAIL'";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id=@s";
        cmd.CommandText = $@"
            SELECT COALESCE(fail_reason,''), COALESCE(model,''), COALESCE(station_id,''),
                   COALESCE(batch_timestamp,''), COALESCE(test_date,''), COALESCE(xml_path,'')
              FROM test_records {where}
             ORDER BY id DESC
             LIMIT @lim";
        if (!string.IsNullOrEmpty(stationId)) cmd.Parameters.AddWithValue("@s", stationId);
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
