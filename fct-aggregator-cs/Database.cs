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

            // v2.4.0: 待办忽略名单（标记为「不是问题」的故障项，不再出现在待办列表）
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

    // ==================== 待办（FAIL → 确认问题 → 维修记录） ====================

    /// <summary>
    /// 待办项：从 test_records 按故障项聚合，排除已有活跃维修记录或已忽略的项。
    /// 活跃维修记录定义：status 不是 resolved/closed（即还在排查/处理中）。
    /// </summary>
    public List<TodoItem> ListPendingTodos(string stationId = "")
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        var where = "WHERE f.result='FAIL'";
        if (!string.IsNullOrEmpty(stationId)) where += " AND f.station_id=@s";
        cmd.CommandText = $@"
            SELECT
                f.fail_reason,
                f.station_id,
                COALESCE(f.model, ''),
                COALESCE(f.category, ''),
                COUNT(*) AS cnt,
                MIN(COALESCE(NULLIF(f.batch_timestamp,''), f.test_date)) AS first_seen,
                MAX(COALESCE(NULLIF(f.batch_timestamp,''), f.test_date)) AS last_seen,
                MIN(f.test_date) AS first_test_date,
                MAX(f.test_date) AS last_test_date
            FROM test_records f
            WHERE f.result='FAIL'
              AND f.fail_reason IS NOT NULL AND TRIM(f.fail_reason) <> ''
              AND NOT EXISTS (
                  SELECT 1 FROM maintenance_records m
                  WHERE m.fail_item = f.fail_reason
                    AND m.station_id = f.station_id
                    AND m.status NOT IN ('resolved','closed')
              )
              AND NOT EXISTS (
                  SELECT 1 FROM dismissed_todos d
                  WHERE d.fail_item = f.fail_reason
                    AND d.station_id = f.station_id
              )
            GROUP BY f.fail_reason, f.station_id
            ORDER BY last_seen DESC, cnt DESC
            LIMIT 500";
        if (!string.IsNullOrEmpty(stationId)) cmd.Parameters.AddWithValue("@s", stationId);
        var list = new List<TodoItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TodoItem
            {
                FailItem = r.IsDBNull(0) ? "" : r.GetString(0),
                StationId = r.IsDBNull(1) ? "" : r.GetString(1),
                Model = r.IsDBNull(2) ? "" : r.GetString(2),
                Category = r.IsDBNull(3) ? "" : r.GetString(3),
                OccurrenceCount = r.GetInt32(4),
                FirstSeen = r.IsDBNull(5) ? "" : r.GetString(5),
                LastSeen = r.IsDBNull(6) ? "" : r.GetString(6),
                FirstTestDate = r.IsDBNull(7) ? "" : r.GetString(7),
                LastTestDate = r.IsDBNull(8) ? "" : r.GetString(8),
            });
        }
        return list;
    }

    /// <summary>忽略某故障项（不再出现在待办列表）。已存在的维修记录不受影响。</summary>
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
    /// 从待办确认问题：创建一条维修记录（状态=open），并返回其 ID。
    /// 该记录创建后待办列表自动不再显示此故障项（因为有活跃维修记录存在）。
    /// </summary>
    public int AcknowledgeTodo(string failItem, string stationId, string model, string resolver, string severity)
    {
        var rec = new MaintenanceRecord
        {
            StationId = stationId,
            FailItem = failItem,
            FailReason = $"来自 FAIL 待办 — {model}",
            Severity = severity,
            Status = "open",
            Resolver = resolver,
        };
        return CreateMaintenance(rec);
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

public class TodoItem
{
    public string FailItem = "";
    public string StationId = "";
    public string Model = "";
    public string Category = "";
    public int OccurrenceCount;
    public string FirstSeen = "";
    public string LastSeen = "";
    public string FirstTestDate = "";
    public string LastTestDate = "";
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
