using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>聚合库一条 FAIL 记录：机台推送的 fail-{seq}.json 解析合并入库后的行。</summary>
public class AggFailRow
{
    public long Id;                 // 聚合库自增 id
    public string Machine = "";     // 机台名（共享目录下的子目录名）
    public long Seq;                // 机台端 test_records 自增 id，(machine, seq) 构成幂等键
    public string Type = "fail";    // 事件类型，目前都是 "fail"
    public string Ts = "";          // 机台推送时间 yyyy-MM-dd HH:mm:ss
    public string IngestTs = "";    // 聚合端入库时间 yyyy-MM-dd HH:mm:ss
    public string StationId = "", Model = "", Category = "", TestDate = "", Sn = "", Result = "";
    public string FailReason = "", Tester = "", PanelStatus = "", BatchTimestamp = "", XmlPath = "";
    public bool HasFailItems;
    public long FileSize;
}

/// <summary>
/// 聚合数据库（独立 SQLite 文件，部署在共享目录宿主机）。
/// 汇总所有机台推送的 FAIL 记录；UNIQUE(machine, seq) 是幂等核心 ——
/// 初始全量扫描与 FileSystemWatcher 增量对同一文件重复处理也不产生重复行。
/// 与 <see cref="Database"/>（机台本地库）互不相干，只存聚合端数据。
/// </summary>
public class AggDatabase : IDisposable
{
    private readonly object _sync = new();
    private readonly string _connString;
    private SqliteConnection? _conn;

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

    /// <summary>建表 + WAL + busy_timeout。可重复调用（已打开则直接返回；Close 后可再次打开）。</summary>
    public void Open()
    {
        lock (_sync)
        {
            if (_conn != null) return;
            var c = new SqliteConnection(_connString);
            c.Open();
            using (var cmd = c.CreateCommand())
            {
                // WAL 让心跳轮询/处理线程/UI 查询之间读写不互斥；写之间靠 busy 等待协调
                cmd.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
                cmd.ExecuteNonQuery();
            }
            using (var ddl = c.CreateCommand())
            {
                ddl.CommandText = @"
                    CREATE TABLE IF NOT EXISTS agg_records (
                      id INTEGER PRIMARY KEY AUTOINCREMENT,
                      machine TEXT NOT NULL,
                      seq INTEGER NOT NULL,
                      type TEXT NOT NULL DEFAULT 'fail',
                      ts TEXT, ingest_ts TEXT,
                      station_id TEXT, model TEXT, category TEXT, test_date TEXT, sn TEXT,
                      result TEXT, fail_reason TEXT, tester TEXT, panel_status TEXT,
                      batch_timestamp TEXT, has_fail_items INTEGER, file_size INTEGER, xml_path TEXT,
                      UNIQUE(machine, seq)
                    );
                    CREATE INDEX IF NOT EXISTS idx_agg_records_ingest ON agg_records(ingest_ts);
                    CREATE INDEX IF NOT EXISTS idx_agg_records_machine ON agg_records(machine);";
                ddl.ExecuteNonQuery();
            }
            _conn = c;
        }
    }

    /// <summary>
    /// INSERT OR IGNORE 入库（(machine, seq) 幂等）。返回 1 = 新入库，0 = 重复忽略。
    /// 插入成功时把自增 id 写回 <paramref name="row"/>.Id（last_insert_rowid 是连接级的）。
    /// </summary>
    public int InsertFail(AggFailRow row)
    {
        lock (_sync)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO agg_records
                (machine, seq, type, ts, ingest_ts, station_id, model, category, test_date, sn,
                 result, fail_reason, tester, panel_status, batch_timestamp, has_fail_items, file_size, xml_path)
                VALUES (@m,@seq,@type,@ts,@ingest,@st,@model,@cat,@date,@sn,
                        @result,@reason,@tester,@panel,@bt,@hasfail,@size,@path)";
            cmd.Parameters.AddWithValue("@m", row.Machine);
            cmd.Parameters.AddWithValue("@seq", row.Seq);
            cmd.Parameters.AddWithValue("@type", string.IsNullOrEmpty(row.Type) ? "fail" : row.Type);
            cmd.Parameters.AddWithValue("@ts", (object?)row.Ts ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ingest", (object?)row.IngestTs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@st", (object?)row.StationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@model", (object?)row.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cat", (object?)row.Category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@date", (object?)row.TestDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sn", (object?)row.Sn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@result", (object?)row.Result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reason", (object?)row.FailReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tester", (object?)row.Tester ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@panel", (object?)row.PanelStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@bt", (object?)row.BatchTimestamp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hasfail", row.HasFailItems ? 1 : 0);
            cmd.Parameters.AddWithValue("@size", (object?)row.FileSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@path", (object?)row.XmlPath ?? DBNull.Value);
            if (cmd.ExecuteNonQuery() <= 0) return 0;   // (machine, seq) 已存在，重复忽略
            using var idCmd = _conn.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            row.Id = Convert.ToInt64(idCmd.ExecuteScalar());
            return 1;
        }
    }

    /// <summary>某机台 FAIL 总数；machine 为空串/null 时统计全库。keyword 非空时加模糊过滤（分页总数用）。</summary>
    public long FailCount(string machine, string? keyword = null)
    {
        lock (_sync)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            var conds = new List<string>();
            if (!string.IsNullOrEmpty(machine)) conds.Add("machine = @m");
            if (!string.IsNullOrEmpty(keyword)) conds.Add("(sn LIKE @kw OR model LIKE @kw OR fail_reason LIKE @kw)");
            var where = conds.Count > 0 ? "WHERE " + string.Join(" AND ", conds) : "";
            cmd.CommandText = $"SELECT COUNT(*) FROM agg_records {where}";
            if (!string.IsNullOrEmpty(machine)) cmd.Parameters.AddWithValue("@m", machine);
            if (!string.IsNullOrEmpty(keyword)) cmd.Parameters.AddWithValue("@kw", $"%{keyword}%");
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>某机台最近一次 FAIL 的 ingest_ts；没有记录返回 null。</summary>
    public string? LastFailAt(string machine)
    {
        lock (_sync)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT MAX(ingest_ts) FROM agg_records WHERE machine = @m";
            cmd.Parameters.AddWithValue("@m", machine);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? null : v.ToString();
        }
    }

    /// <summary>某机台最早的 ingest_ts（FirstSeenAt 用）；没有记录返回 null。</summary>
    public string? MinIngestTs(string machine)
    {
        lock (_sync)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT MIN(ingest_ts) FROM agg_records WHERE machine = @m";
            cmd.Parameters.AddWithValue("@m", machine);
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? null : v.ToString();
        }
    }

    /// <summary>按 ingest_ts 倒序取最近 FAIL 明细；machine 非空时只取该机台。
    /// offset 用于分页（0=从最新开始）；keyword 非空时对 SN/型号/失败原因做 LIKE 模糊搜索。</summary>
    public List<AggFailRow> QueryFails(int limit, string? machine = null, int offset = 0, string? keyword = null)
    {
        lock (_sync)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            var where = new List<string>();
            if (!string.IsNullOrEmpty(machine)) where.Add("machine = @m");
            if (!string.IsNullOrEmpty(keyword))
            {
                // 注入防护：keyword 参数化；通配符由用户输入，LIKE 不命中也不出错
                where.Add("(sn LIKE @kw OR model LIKE @kw OR fail_reason LIKE @kw)");
            }
            var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
            cmd.CommandText = $@"
                SELECT id, machine, seq, type, ts, ingest_ts,
                       station_id, model, category, test_date, sn,
                       result, fail_reason, tester, panel_status, batch_timestamp,
                       has_fail_items, file_size, xml_path
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
    }

    /// <summary>按 id 取一条 FAIL 记录（Web 看板 /api/file?id= 下载 XML 报告用）；不存在返回 null。</summary>
    public AggFailRow? GetFailById(long id)
    {
        lock (_sync)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"
                SELECT id, machine, seq, type, ts, ingest_ts,
                       station_id, model, category, test_date, sn,
                       result, fail_reason, tester, panel_status, batch_timestamp,
                       has_fail_items, file_size, xml_path
                  FROM agg_records WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRow(r) : null;
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
    };

    /// <summary>关闭连接（幂等；之后可再次 <see cref="Open"/>）。</summary>
    public void Close()
    {
        lock (_sync)
        {
            if (_conn == null) return;
            _conn.Dispose();
            _conn = null;
        }
    }

    public void Dispose() => Close();
}
