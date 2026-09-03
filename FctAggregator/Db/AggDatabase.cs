using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>聚合库一条 FAIL 记录：机台推送的 fail-{seq}.json 解析合并入库后的行。</summary>
public class AggFailRow
{
    public long Id;                 // 聚合库自增 id（仅本批新插入的行会被回填，重复忽略的行保持 0）
    public string Machine = "";     // 机台名（共享目录下的子目录名）
    public long Seq;                // 机台端 test_records 自增 id，(machine, seq) 构成幂等键
    public string Type = "fail";    // 事件类型，目前都是 "fail"
    public string Ts = "";          // 机台推送时间 yyyy-MM-dd HH:mm:ss
    public string IngestTs = "";    // 聚合端入库时间 yyyy-MM-dd HH:mm:ss
    public string StationId = "", Model = "", Category = "", TestDate = "", Sn = "", Result = "";
    public string FailReason = "", Tester = "", PanelStatus = "", BatchTimestamp = "", XmlPath = "";
    /// <summary>治具/机台位 ID（v3.22.0 规格01 fixture 归因；老数据/老对端为空串 → 归因侧前缀回退）。</summary>
    public string FixtureId = "";
    public bool HasFailItems;
    public long FileSize;
}

/// <summary>
/// 聚合数据库（独立 SQLite 文件，部署在共享目录宿主机）。
/// 汇总所有机台推送的 FAIL 记录；UNIQUE(machine, seq) 是幂等核心 ——
/// 初始全量扫描与 FileSystemWatcher 增量对同一文件重复处理也不产生重复行。
/// 与 <see cref="Database"/>（机台本地库）互不相干，只存聚合端数据。
///
/// 并发模型（v3.5.3 读写分离，替代原先「单连接 + 全局锁」的完全串行）：
/// - 写路径：一把写锁 + 一条常驻 WAL 写连接（SQLite 单写者语义，天然串行但不再被读阻塞）；
///   <see cref="InsertBatch"/> 把多条合并进单个事务，WAL fsync 次数与提交次数同数量级下降；
/// - 读路径：每次查询临时建一条短连接（WAL 下读不阻塞写、开销微秒级），不持有任何全局锁 ——
///   看板分页/对账拉取与推送入库互不拖累；刻意**不用** ADO 连接池，避免池化句柄长期占用
///   db 文件导致备份拷贝/测试清理删不掉文件。
/// </summary>
public partial class AggDatabase : IDisposable
{
    private readonly object _writeLock = new();     // 写路径互斥（含建表初始化）
    private readonly string _connString;
    private SqliteConnection? _conn;                // 常驻写连接（懒建，Close 后可重建）

    /// <summary>聚合库文件路径。</summary>
    public string DbPath { get; }

    /// <summary>只存路径不连库；真正连库在 <see cref="Open"/>。</summary>
    public AggDatabase(string dbPath)
    {
        DbPath = dbPath;
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    // v3.10.0 P1-1 观测：累计实际入库行数（InsertFail 单条 + InsertBatch 批量成功数）
    private long _insertCount;

    /// <summary>累计实际入库行数（重复忽略不计）。/api/metrics 与自检观测。</summary>
    public long InsertCount => Interlocked.Read(ref _insertCount);

    /// <summary>建连 + WAL + busy_timeout + schema 迁移（DbMigrator 建表，幂等；Close 后可再次打开）。</summary>
    public void Open()
    {
        lock (_writeLock)
        {
            OpenWriterLocked();
            // schema 版本化迁移（PRAGMA user_version 驱动，见 DbMigrator）：
            // 原 6 条建表 DDL 已移入 DbMigrator.Migrations[0]，每版本单事务、失败整体回滚
            DbMigrator.Migrate(_conn!);
        }
    }

    /// <summary>确保常驻写连接已打开（调用方必须持有 <see cref="_writeLock"/>）。</summary>
    private void OpenWriterLocked()
    {
        if (_conn != null) return;
        var c = new SqliteConnection(_connString);
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            // WAL 让推送入库(写)与看板/对账(读)不互斥；busy_timeout 兜底跨进程共享场景
            cmd.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();
        }
        _conn = c;
    }

    /// <summary>开一条短租读连接（用完即弃）。WAL 已持久化在文件头，读侧只需重申 busy_timeout。</summary>
    private SqliteConnection OpenReader()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    /// <summary>
    /// INSERT OR IGNORE 入库（(machine, seq) 幂等）。返回 1 = 新入库，0 = 重复忽略。
    /// 插入成功时把自增 id 写回 <paramref name="row"/>.Id（last_insert_rowid 是连接级的）。
    /// </summary>
    public int InsertFail(AggFailRow row)
    {
        lock (_writeLock)
        {
            Open();
            using var cmd = NewInsertCmd(_conn!);
            SetInsertParams(cmd, row);
            if (cmd.ExecuteNonQuery() <= 0) return 0;   // (machine, seq) 已存在，重复忽略
            using var idCmd = _conn!.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            row.Id = Convert.ToInt64(idCmd.ExecuteScalar());
            Interlocked.Increment(ref _insertCount);    // v3.10.0 P1-1 观测
            return 1;
        }
    }

    /// <summary>
    /// 批量 INSERT OR IGNORE 入库（v3.5.3）：整个批次一个事务一次提交，
    /// 新插入的行回填 Id（重复忽略的行保持调用前的 Id）。返回实际新插入条数。
    /// 对账补推大窗口（Gossiper）与接收端组提交都走这里，避免逐条 fsync 放大写入延迟。
    /// 任一行 SQL 失败则整批回滚并抛出 —— 由调用方决定整批重试（幂等键保证重复推不重）。
    /// </summary>
    public int InsertBatch(IEnumerable<AggFailRow> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return 0;
        int inserted = 0;
        lock (_writeLock)
        {
            Open();
            using var tx = _conn!.BeginTransaction();
            // 命令与参数集合只建一次、逐行改值 —— 组提交/对账批量里这是热循环，
            // 每行 AddWithValue 新建参数会把参数对象越积越多（自检已抓到该问题）
            using var cmd = NewInsertCmd(_conn!);
            cmd.Transaction = tx;
            using var idCmd = _conn!.CreateCommand();
            idCmd.Transaction = tx;
            idCmd.CommandText = "SELECT last_insert_rowid()";
            foreach (var row in list)
            {
                SetInsertParams(cmd, row);
                // 不能事后按 Id>0 数：重复行带着上次的旧 Id 回来，会误计；当场统计才准
                if (cmd.ExecuteNonQuery() > 0)
                {
                    row.Id = Convert.ToInt64(idCmd.ExecuteScalar());
                    inserted++;
                }
            }
            tx.Commit();
            Interlocked.Add(ref _insertCount, inserted);   // v3.10.0 P1-1 观测
        }
        return inserted;
    }

    private static readonly string InsertSql = @"
                INSERT OR IGNORE INTO agg_records
                (machine, seq, type, ts, ingest_ts, station_id, model, category, test_date, sn,
                 result, fail_reason, tester, panel_status, batch_timestamp, has_fail_items, file_size, xml_path, fixture_id)
                VALUES (@m,@seq,@type,@ts,@ingest,@st,@model,@cat,@date,@sn,
                        @result,@reason,@tester,@panel,@bt,@hasfail,@size,@path,@fixture)";

    /// <summary>预置好 19 个命名参数的 INSERT 命令（重复使用时只换 Value，不再新建参数对象）。</summary>
    private static SqliteCommand NewInsertCmd(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = InsertSql;
        var names = new[] { "@m", "@seq", "@type", "@ts", "@ingest", "@st", "@model", "@cat", "@date", "@sn",
                            "@result", "@reason", "@tester", "@panel", "@bt", "@hasfail", "@size", "@path", "@fixture" };
        foreach (var n in names)
            cmd.Parameters.AddWithValue(n, DBNull.Value);
        return cmd;
    }

    private static void SetInsertParams(SqliteCommand cmd, AggFailRow row)
    {
        var p = cmd.Parameters;
        p["@m"].Value = row.Machine;
        p["@seq"].Value = row.Seq;
        p["@type"].Value = string.IsNullOrEmpty(row.Type) ? "fail" : row.Type;
        p["@ts"].Value = (object?)row.Ts ?? DBNull.Value;
        p["@ingest"].Value = (object?)row.IngestTs ?? DBNull.Value;
        p["@st"].Value = (object?)row.StationId ?? DBNull.Value;
        p["@model"].Value = (object?)row.Model ?? DBNull.Value;
        p["@cat"].Value = (object?)row.Category ?? DBNull.Value;
        p["@date"].Value = (object?)row.TestDate ?? DBNull.Value;
        p["@sn"].Value = (object?)row.Sn ?? DBNull.Value;
        p["@result"].Value = (object?)row.Result ?? DBNull.Value;
        p["@reason"].Value = (object?)row.FailReason ?? DBNull.Value;
        p["@tester"].Value = (object?)row.Tester ?? DBNull.Value;
        p["@panel"].Value = (object?)row.PanelStatus ?? DBNull.Value;
        p["@bt"].Value = (object?)row.BatchTimestamp ?? DBNull.Value;
        p["@hasfail"].Value = row.HasFailItems ? 1 : 0;
        p["@size"].Value = (object?)row.FileSize ?? DBNull.Value;
        p["@path"].Value = (object?)row.XmlPath ?? DBNull.Value;
        p["@fixture"].Value = string.IsNullOrEmpty(row.FixtureId) ? DBNull.Value : (object?)row.FixtureId;
    }

    /// <summary>某机台 FAIL 总数；machine 为空串/null 时统计全库。keyword 非空时加模糊过滤（分页总数用）。</summary>
    public long FailCount(string machine, string? keyword = null)
    {
        using var conn = OpenReader();
        var conds = new List<string>();
        if (!string.IsNullOrEmpty(machine)) conds.Add("machine = @m");
        if (!string.IsNullOrEmpty(keyword)) conds.Add("(sn LIKE @kw OR model LIKE @kw OR fail_reason LIKE @kw)");
        var where = conds.Count > 0 ? "WHERE " + string.Join(" AND ", conds) : "";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM agg_records {where}";
        if (!string.IsNullOrEmpty(machine)) cmd.Parameters.AddWithValue("@m", machine);
        if (!string.IsNullOrEmpty(keyword)) cmd.Parameters.AddWithValue("@kw", $"%{keyword}%");
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary><see cref="FailCount"/> 的 5 秒缓存版（看板每页计数 / 邻居状态轮询高频调用）。cacheTtlMs &lt;= 0 表示直查。</summary>
    public long FailCountCached(string machine, string? keyword = null, int cacheTtlMs = 5000)
    {
        if (cacheTtlMs <= 0) return FailCount(machine, keyword);
        var key = (machine, keyword ?? "");
        lock (_countCacheLock)
        {
            if (_countCache.TryGetValue(key, out var e) && (DateTime.UtcNow - e.At).TotalMilliseconds <= cacheTtlMs)
                return e.Count;
        }
        var count = FailCount(machine, keyword);
        lock (_countCacheLock)
        {
            if (_countCache.Count >= 512) _countCache.Clear();   // 防泄漏：机台数有限，正常到不了
            _countCache[key] = (DateTime.UtcNow, count);
        }
        return count;
    }

    private readonly object _countCacheLock = new();
    private readonly Dictionary<(string Machine, string Keyword), (DateTime At, long Count)> _countCache = new();

    /// <summary>清空 FailCount 缓存（自检/维护场景手动失效用）。</summary>
    public void ClearCountCache() { lock (_countCacheLock) _countCache.Clear(); }

    /// <summary>某机台最近一次 FAIL 的 ingest_ts；没有记录返回 null。</summary>
    public string? LastFailAt(string machine)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(ingest_ts) FROM agg_records WHERE machine = @m";
        cmd.Parameters.AddWithValue("@m", machine);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : v.ToString();
    }

    /// <summary>某机台最早的 ingest_ts（FirstSeenAt 用）；没有记录返回 null。</summary>
    public string? MinIngestTs(string machine)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(ingest_ts) FROM agg_records WHERE machine = @m";
        cmd.Parameters.AddWithValue("@m", machine);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : v.ToString();
    }

    /// <summary>各机台在本副本库的已同步最大 seq（去中心化对账基准）。machine→max(seq)。</summary>
    public Dictionary<string, long> MaxSeqPerMachine()
    {
        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT machine, MAX(seq) FROM agg_records GROUP BY machine";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var m = r.IsDBNull(0) ? "" : r.GetString(0);
            var s = r.IsDBNull(1) ? 0L : r.GetInt64(1);
            if (m.Length > 0) dict[m] = s;
        }
        return dict;
    }

    /// <summary>单次增量对账最大行数（防 from=0&amp;to=long.Max 把整表拉进内存）。
    /// Gossiper 正常对账窗口远小于此；超限时调用方分批（缩小 to 再拉）。</summary>
    public const int MaxRangeRows = 20000;

    /// <summary>Gossiper 增量对账：取指定机台 (from, to] 区间的 FAIL 行（含端点外推，按 seq 升序，最多 MaxRangeRows 行）。
    /// UNIQUE(machine,seq) 自动索引覆盖该查询的过滤与排序。</summary>
    public List<AggFailRow> QueryFailsByMachineSeqRange(string machine, long from, long to)
    {
        var list = new List<AggFailRow>();
        if (string.IsNullOrEmpty(machine) || to <= from) return list;
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
                SELECT id, machine, seq, type, ts, ingest_ts,
                       station_id, model, category, test_date, sn,
                       result, fail_reason, tester, panel_status, batch_timestamp,
                       has_fail_items, file_size, xml_path, fixture_id
                  FROM agg_records
                 WHERE machine = @m AND seq > @from AND seq <= @to
                 ORDER BY seq ASC
                 LIMIT {MaxRangeRows}";
        cmd.Parameters.AddWithValue("@m", machine);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadRow(r));
        return list;
    }

    /// <summary>按 ingest_ts 倒序取最近 FAIL 明细；machine 非空时只取该机台。
    /// offset 用于分页（0=从最新开始）；keyword 非空时对 SN/型号/失败原因做 LIKE 模糊搜索。</summary>
    public List<AggFailRow> QueryFails(int limit, string? machine = null, int offset = 0, string? keyword = null)
    {
        using var conn = OpenReader();
        var where = new List<string>();
        if (!string.IsNullOrEmpty(machine)) where.Add("machine = @m");
        if (!string.IsNullOrEmpty(keyword))
        {
            // 注入防护：keyword 参数化；通配符由用户输入，LIKE 不命中也不出错
            where.Add("(sn LIKE @kw OR model LIKE @kw OR fail_reason LIKE @kw)");
        }
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
                SELECT id, machine, seq, type, ts, ingest_ts,
                       station_id, model, category, test_date, sn,
                       result, fail_reason, tester, panel_status, batch_timestamp,
                       has_fail_items, file_size, xml_path, fixture_id
                  FROM agg_records {whereSql}
                 ORDER BY ingest_ts DESC, id DESC
                 LIMIT @lim OFFSET @off";
        if (!string.IsNullOrEmpty(machine)) cmd.Parameters.AddWithValue("@m", machine);
        if (!string.IsNullOrEmpty(keyword)) cmd.Parameters.AddWithValue("@kw", $"%{keyword}%");
        cmd.Parameters.AddWithValue("@lim", limit <= 0 ? 200 : limit);
        cmd.Parameters.AddWithValue("@off", offset < 0 ? 0 : offset);
        var list = new List<AggFailRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadRow(r));
        return list;
    }

    /// <summary>按 id 取一条 FAIL 记录（Web 看板 /api/file?id= 下载 XML 报告用）；不存在返回 null。</summary>
    public AggFailRow? GetFailById(long id)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
                SELECT id, machine, seq, type, ts, ingest_ts,
                       station_id, model, category, test_date, sn,
                       result, fail_reason, tester, panel_status, batch_timestamp,
                       has_fail_items, file_size, xml_path, fixture_id
                  FROM agg_records WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRow(r) : null;
    }

    /// <summary>按行取全量导出读路径（XML 缓存落盘/异地副本工具用）；供运维脚本拉历史而不占写连接。</summary>
    public IEnumerable<AggFailRow> StreamAll(Action<string>? progress = null)
    {
        Open();
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
                SELECT id, machine, seq, type, ts, ingest_ts,
                       station_id, model, category, test_date, sn,
                       result, fail_reason, tester, panel_status, batch_timestamp,
                       has_fail_items, file_size, xml_path, fixture_id
                  FROM agg_records ORDER BY id ASC";
        using var r = cmd.ExecuteReader();
        long n = 0;
        while (r.Read())
        {
            yield return ReadRow(r);
            if (++n % 1000 == 0) progress?.Invoke($"{n}");
        }
    }

    /// <summary>机台心跳携带的当日统计（总纲 P1 D1，源数据机台本地 test_records 聚合）。</summary>
    public readonly record struct DailyStats(int Total, int Pass, int Fail, int Interrupted, int Products);

    /// <summary>
    /// 幂等 upsert 机台日统计（v3.8.0，总纲 P1 D1）：按 (machine, test_date) 主键写 yld_daily。
    /// 心跳 30s 一次，写入走写锁单写者；调用方应在值未变化时跳过（见 MeshReceiver 缓存比对）。
    /// </summary>
    public void UpsertDailyStats(string machine, string testDateYmd, DailyStats s)
    {
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO yld_daily (machine, test_date, total, pass, fail, interrupted, products, updated_ts)
                VALUES (@m,@d,@total,@pass,@fail,@intr,@prod,@ts)
                ON CONFLICT(machine, test_date) DO UPDATE SET
                    total=excluded.total, pass=excluded.pass, fail=excluded.fail,
                    interrupted=excluded.interrupted, products=excluded.products, updated_ts=excluded.updated_ts";
            cmd.Parameters.AddWithValue("@m", machine);
            cmd.Parameters.AddWithValue("@d", testDateYmd);
            cmd.Parameters.AddWithValue("@total", s.Total);
            cmd.Parameters.AddWithValue("@pass", s.Pass);
            cmd.Parameters.AddWithValue("@fail", s.Fail);
            cmd.Parameters.AddWithValue("@intr", s.Interrupted);
            cmd.Parameters.AddWithValue("@prod", s.Products);
            cmd.Parameters.AddWithValue("@ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 查询机台日统计（v3.8.0，总纲 P1 D1）：按日期倒序返回；machine 为空 = 全机台；
    /// dateFromYmd/dateToYmd 限定日期窗口；days 限返回行数上限（0 = 不限，内部仍封顶防拉爆）。
    /// 良率 = pass/total 由调用方计算。
    /// </summary>
    public List<YldDailyRow> QueryDailyStats(string? machine = null, string? dateFromYmd = null, string? dateToYmd = null, int maxRows = 2000)
    {
        var sql = "SELECT machine, test_date, total, pass, fail, interrupted, products, updated_ts FROM yld_daily";
        var conds = new List<string>();
        var ps = new List<(string name, object val)>();
        if (!string.IsNullOrEmpty(machine)) { conds.Add("machine = @m"); ps.Add(("@m", machine!)); }
        if (!string.IsNullOrEmpty(dateFromYmd)) { conds.Add("test_date >= @df"); ps.Add(("@df", dateFromYmd!)); }
        if (!string.IsNullOrEmpty(dateToYmd)) { conds.Add("test_date <= @dt"); ps.Add(("@dt", dateToYmd!)); }
        if (conds.Count > 0) sql += " WHERE " + string.Join(" AND ", conds);
        sql += " ORDER BY test_date DESC, machine ASC";
        if (maxRows > 0) sql += $" LIMIT {Math.Min(maxRows, 5000)}";   // 封顶防单次拉爆

        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, val) in ps) cmd.Parameters.AddWithValue(name, val);
        var rows = new List<YldDailyRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new YldDailyRow(
                r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3),
                r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7)));
        }
        return rows;
    }

    /// <summary>一行 yld_daily 记录。</summary>
    public record YldDailyRow(string Machine, string TestDate, int Total, int Pass, int Fail, int Interrupted, int Products, string UpdatedTs);

    /// <summary>
    /// 查询机台原始记录（v3.21.0 规格 03 季节性分解用）：按日期窗口拉 agg_records 的 PASS/FAIL 行，
    /// 小时分桶在调用方（YieldDecomposer）完成；batch_timestamp 原样返回（可空/空串，由调用方决定 fallback）。
    /// 30 天数据 ~1 万行，全表扫 < 100ms，本期不加索引（规格 §6 明确）。
    /// </summary>
    public List<HourlyRawRow> QueryHourlyRaw(string machine, string fromYmd, string toYmd)
    {
        var rows = new List<HourlyRawRow>();
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT test_date, batch_timestamp, result
            FROM agg_records
            WHERE machine = @m AND test_date >= @df AND test_date <= @dt
              AND result IN ('PASS','FAIL')";
        cmd.Parameters.AddWithValue("@m", machine);
        cmd.Parameters.AddWithValue("@df", fromYmd);
        cmd.Parameters.AddWithValue("@dt", toYmd);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new HourlyRawRow(
                r.GetString(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.GetString(2)));
        }
        return rows;
    }

    /// <summary>一条 agg_records 原始行（规格 03 用，batch_timestamp 可能为空串）。</summary>
    public sealed record HourlyRawRow(string TestDate, string BatchTimestamp, string Result);

    // ==================== 权限与审计（v3.8.0 总纲 P1 D3）====================

    /// <summary>一条用户记录（role ∈ viewer|engineer|admin；token 为 32hex 静态令牌）。</summary>
    public sealed record UserRow(string Name, string PwdHash, string Role, string Token,
                                 string? Layout, string? Favorites, string CreatedAt);

    /// <summary>新增/更新用户（name 主键 upsert，NOCASE）。新用户自动生成 token；
    /// 更新时保留旧 token（否则已登录会话全失效）。</summary>
    public void UpsertUser(string name, string pwdHash, string role, string? layout = null, string? favorites = null)
    {
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO users (name, pwd_hash, role, token, layout, favorites, created_at)
                VALUES (@n,@p,@r,@t,@l,@f,datetime('now','localtime'))
                ON CONFLICT(name) DO UPDATE SET
                    pwd_hash=excluded.pwd_hash, role=excluded.role,
                    token = CASE WHEN users.token = '' THEN excluded.token ELSE users.token END,
                    layout=excluded.layout, favorites=excluded.favorites";
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@p", pwdHash);
            cmd.Parameters.AddWithValue("@r", role);
            cmd.Parameters.AddWithValue("@t", PasswordHasher.RandomToken());
            cmd.Parameters.AddWithValue("@l", (object?)layout ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@f", (object?)favorites ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>按用户名查用户；不存在返回 null。</summary>
    public UserRow? GetUserByName(string name)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name,pwd_hash,role,token,layout,favorites,created_at FROM users WHERE name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    }

    /// <summary>按令牌查用户（登录态/API 鉴权用）；不存在返回 null。</summary>
    public UserRow? GetUserByToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name,pwd_hash,role,token,layout,favorites,created_at FROM users WHERE token=@t";
        cmd.Parameters.AddWithValue("@t", token);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    }

    /// <summary>全部用户（admin 用户管理用），按名字排序。</summary>
    public List<UserRow> ListUsers()
    {
        var rows = new List<UserRow>();
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name,pwd_hash,role,token,layout,favorites,created_at FROM users ORDER BY name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(ReadUser(r));
        return rows;
    }

    /// <summary>删除用户（name 不区分大小写）。返回是否删到。</summary>
    public bool DeleteUser(string name)
    {
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM users WHERE name=@n";
            cmd.Parameters.AddWithValue("@n", name);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private static UserRow ReadUser(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.GetString(6));

    /// <summary>写一条审计（who=身份（用户名或 agg_token 标识）/ action=操作 / detail=详情）。</summary>
    public void LogAudit(string who, string action, string detail = "")
    {
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "INSERT INTO audit_log (ts, who, action, detail) VALUES (@t,@w,@a,@d)";
            cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@w", who);
            cmd.Parameters.AddWithValue("@a", action);
            cmd.Parameters.AddWithValue("@d", detail);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>一条审计记录。</summary>
    public sealed record AuditRow(long Id, string Ts, string Who, string Action, string Detail);

    /// <summary>审计查询（admin 用）：最近 limit 条，按时间倒序。</summary>
    public List<AuditRow> QueryAudit(int limit = 200)
    {
        var rows = new List<AuditRow>();
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, ts, who, action, detail FROM audit_log ORDER BY id DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", Math.Min(Math.Max(limit, 1), 1000));
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new AuditRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return rows;
    }

    /// <summary>
    /// 数据库低峰维护（v3.5.3，每日由 DbMaintenance 定时调用或手动触发）：
    /// 先 wal_checkpoint(TRUNCATE) 收缩 WAL 文件；库体积超过阈值再 VACUUM 整理碎片。
    /// 在写锁内执行保证与入库串行；03:00 低峰调用时代价可忽略。
    /// 返回人类可读摘要（checkpoint 后 WAL 大小 / 是否执行了 VACUUM 及耗时）。
    /// </summary>
    public string RunMaintenance(long vacuumThresholdBytes)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (_writeLock)
        {
            Open();
            using (var cp = _conn!.CreateCommand())
            {
                cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cp.ExecuteNonQuery();
            }
            long pages, pageSize;
            // ⚠ Microsoft.Data.Sqlite 的 Read() 不自动跨语句取下一结果集，必须分条执行
            //   （否则 page_size 恒 0、永远"未达阈值"，VACUUM 分支成死路 —— 自检已抓到）
            using (var p1 = _conn!.CreateCommand())
            {
                p1.CommandText = "PRAGMA page_count;";
                pages = Convert.ToInt64(p1.ExecuteScalar());
            }
            using (var p2 = _conn.CreateCommand())
            {
                p2.CommandText = "PRAGMA page_size;";
                pageSize = Convert.ToInt64(p2.ExecuteScalar());
            }
            var total = pages * pageSize;
            bool vacuumed = false;
            if (vacuumThresholdBytes > 0 && total > vacuumThresholdBytes)
            {
                using var vc = _conn!.CreateCommand();
                vc.CommandText = "VACUUM;";
                vc.ExecuteNonQuery();
                vacuumed = true;
            }
            sw.Stop();
            return $"wal_checkpoint 完成{(vacuumed ? $", VACUUM 完成（{total / 1024 / 1024} MB 库）" : $"（{total / 1024 / 1024} MB 未达 VACUUM 阈值）")}，耗时 {sw.ElapsedMilliseconds} ms";
        }
    }

    /// <summary>
    /// 每日备份（v3.10.0 P1-2，架构方案 P1-2）：checkpoint(TRUNCATE) 把 WAL 落盘 →
    /// 复制为 {db}.bak-yyyyMMdd → 清理最老备份只留 <see cref="BackupKeepDays"/> 份。
    /// 同日幂等（已存在当日备份直接返回）；写锁内执行（与维护/入库串行）。
    /// 返回备份文件完整路径；同日已存在时返回 null（不算失败）。
    /// </summary>
    public const int BackupKeepDays = 7;
    public string? BackupDaily()
    {
        lock (_writeLock)
        {
            Open();
            var bak = $"{DbPath}.bak-{DateTime.Now:yyyyMMdd}";
            if (File.Exists(bak)) return null;   // 当日已备份，幂等跳过
            using (var cp = _conn!.CreateCommand())
            {
                cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cp.ExecuteNonQuery();
            }
            File.Copy(DbPath, bak, overwrite: true);
            // 清理最老：.bak-YYYYMMDD 排序，超出保留份数删除最旧
            var dir = Path.GetDirectoryName(DbPath)!;
            var prefix = Path.GetFileName(DbPath) + ".bak-";
            var old = Directory.Exists(dir)
                ? Directory.GetFiles(dir, prefix + "*").OrderByDescending(f => f, StringComparer.Ordinal).ToList()
                : new List<string>();
            foreach (var f in old.Skip(BackupKeepDays))
            {
                try { File.Delete(f); } catch { }
            }
            return bak;
        }
    }

    private static AggFailRow ReadRow(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Machine = r.IsDBNull(1) ? "" : r.GetString(1),
        Seq = r.GetInt64(2),
        Type = r.IsDBNull(3) ? "fail" : r.GetString(3),
        Ts = r.IsDBNull(4) ? "" : r.GetString(4),
        IngestTs = r.IsDBNull(5) ? "" : r.GetString(5),
        StationId = r.IsDBNull(6) ? "" : r.GetString(6),
        Model = r.IsDBNull(7) ? "" : r.GetString(7),
        Category = r.IsDBNull(8) ? "" : r.GetString(8),
        TestDate = r.IsDBNull(9) ? "" : r.GetString(9),
        Sn = r.IsDBNull(10) ? "" : r.GetString(10),
        Result = r.IsDBNull(11) ? "" : r.GetString(11),
        FailReason = r.IsDBNull(12) ? "" : r.GetString(12),
        Tester = r.IsDBNull(13) ? "" : r.GetString(13),
        PanelStatus = r.IsDBNull(14) ? "" : r.GetString(14),
        BatchTimestamp = r.IsDBNull(15) ? "" : r.GetString(15),
        HasFailItems = !r.IsDBNull(16) && r.GetInt64(16) != 0,
        FileSize = r.IsDBNull(17) ? 0 : r.GetInt64(17),
        XmlPath = r.IsDBNull(18) ? "" : r.GetString(18),
        FixtureId = r.IsDBNull(19) ? "" : r.GetString(19),
    };

    /// <summary>关闭写连接（幂等；之后可再次 <see cref="Open"/>）。读连接均为短租，无需清理。</summary>
    public void Close()
    {
        lock (_writeLock)
        {
            if (_conn == null) return;
            _conn.Dispose();
            _conn = null;
        }
    }

    public void Dispose() => Close();
}

/// <summary>
/// 密码哈希（v3.8.0 P1 D3）：PBKDF2-SHA256（.NET 内置，无第三方依赖），
/// 存储格式 "iterations.saltHex.hashHex"，随机 16B salt，10000 次迭代（OWASP 基线）。
/// 不用裸 SHA256：内网系统也要防彩虹表/撞库（密码可能复用自其他系统）。
/// </summary>
public static class PasswordHasher
{
    public const int Iterations = 10_000;

    /// <summary>哈希密码，产出 "iterations.saltHex.hashHex"（salt 每次随机）。</summary>
    public static string Hash(string password)
    {
        var salt = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
        var hash = Derive(password, salt, Iterations);
        return $"{Iterations}.{Convert.ToHexString(salt).ToLowerInvariant()}.{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    /// <summary>校验密码与存储值是否匹配（格式不合法直接 false，不抛）。</summary>
    public static bool Verify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('.');
            if (parts.Length != 3) return false;
            var iters = int.Parse(parts[0]);
            var salt = Convert.FromHexString(parts[1]);
            var want = Convert.FromHexString(parts[2]);
            var got = Derive(password, salt, iters);
            return FixedTimeEquals(want, got);
        }
        catch { return false; }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);

    private static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    /// <summary>32hex 随机令牌（与 agg_token 同风格，用户静态 token 用）。</summary>
    public static string RandomToken()
    {
        var b = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b).ToLowerInvariant();
    }
}
