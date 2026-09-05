using Microsoft.Data.Sqlite;

namespace FctAggregator;

public class AggFailRow
{
    public long Id;
    public string Machine = "";
    public long Seq;
    public string Type = "fail";
    public string Ts = "";
    public string IngestTs = "";
    public string StationId = "", Model = "", Category = "", TestDate = "", Sn = "", Result = "";
    public string FailReason = "", Tester = "", PanelStatus = "", BatchTimestamp = "", XmlPath = "";
    public string FixtureId = "";
    public bool HasFailItems;
    public long FileSize;
}

public partial class AggDatabase : IDisposable
{
    private readonly object _writeLock = new();
    private readonly string _connString;
    private SqliteConnection? _conn;

    public string DbPath { get; }

    public AggDatabase(string dbPath)
    {
        DbPath = dbPath;
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    private long _insertCount;

    public long InsertCount => Interlocked.Read(ref _insertCount);

    public void Open()
    {
        lock (_writeLock)
        {
            OpenWriterLocked();
            DbMigrator.Migrate(_conn!);
        }
    }

    private void OpenWriterLocked()
    {
        if (_conn != null) return;
        var c = new SqliteConnection(_connString);
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
            cmd.ExecuteNonQuery();
        }
        _conn = c;
    }

    private SqliteConnection OpenReader()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public int InsertFail(AggFailRow row)
    {
        lock (_writeLock)
        {
            Open();
            using var cmd = NewInsertCmd(_conn!);
            SetInsertParams(cmd, row);
            if (cmd.ExecuteNonQuery() <= 0) return 0;
            using var idCmd = _conn!.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            row.Id = Convert.ToInt64(idCmd.ExecuteScalar());
            Interlocked.Increment(ref _insertCount);
            return 1;
        }
    }

    public int InsertBatch(IEnumerable<AggFailRow> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return 0;
        int inserted = 0;
        lock (_writeLock)
        {
            Open();
            using var tx = _conn!.BeginTransaction();
            using var cmd = NewInsertCmd(_conn!);
            cmd.Transaction = tx;
            using var idCmd = _conn!.CreateCommand();
            idCmd.Transaction = tx;
            idCmd.CommandText = "SELECT last_insert_rowid()";
            foreach (var row in list)
            {
                SetInsertParams(cmd, row);
                if (cmd.ExecuteNonQuery() > 0)
                {
                    row.Id = Convert.ToInt64(idCmd.ExecuteScalar());
                    inserted++;
                }
            }
            tx.Commit();
            Interlocked.Add(ref _insertCount, inserted);
        }
        return inserted;
    }

    private static readonly string InsertSql = @"
                INSERT OR IGNORE INTO agg_records
                (machine, seq, type, ts, ingest_ts, station_id, model, category, test_date, sn,
                 result, fail_reason, tester, panel_status, batch_timestamp, has_fail_items, file_size, xml_path, fixture_id)
                VALUES (@m,@seq,@type,@ts,@ingest,@st,@model,@cat,@date,@sn,
                        @result,@reason,@tester,@panel,@bt,@hasfail,@size,@path,@fixture)";

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
            if (_countCache.Count >= 512) _countCache.Clear();
            _countCache[key] = (DateTime.UtcNow, count);
        }
        return count;
    }

    private readonly object _countCacheLock = new();
    private readonly Dictionary<(string Machine, string Keyword), (DateTime At, long Count)> _countCache = new();

    public void ClearCountCache() { lock (_countCacheLock) _countCache.Clear(); }

    public string? LastFailAt(string machine)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(ingest_ts) FROM agg_records WHERE machine = @m";
        cmd.Parameters.AddWithValue("@m", machine);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : v.ToString();
    }

    public string? MinIngestTs(string machine)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(ingest_ts) FROM agg_records WHERE machine = @m";
        cmd.Parameters.AddWithValue("@m", machine);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : v.ToString();
    }

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

    public const int MaxRangeRows = 20000;

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

    public List<AggFailRow> QueryFails(int limit, string? machine = null, int offset = 0, string? keyword = null)
    {
        using var conn = OpenReader();
        var where = new List<string>();
        if (!string.IsNullOrEmpty(machine)) where.Add("machine = @m");
        if (!string.IsNullOrEmpty(keyword))
        {
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

    public readonly record struct DailyStats(int Total, int Pass, int Fail, int Interrupted, int Products);

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
        if (maxRows > 0) sql += $" LIMIT {Math.Min(maxRows, 5000)}";

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

    public record YldDailyRow(string Machine, string TestDate, int Total, int Pass, int Fail, int Interrupted, int Products, string UpdatedTs);

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

    public sealed record HourlyRawRow(string TestDate, string BatchTimestamp, string Result);

    public sealed record UserRow(string Name, string PwdHash, string Role, string Token,
                                 string? Layout, string? Favorites, string CreatedAt);

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

    public UserRow? GetUserByName(string name)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name,pwd_hash,role,token,layout,favorites,created_at FROM users WHERE name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    }

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

    public sealed record AuditRow(long Id, string Ts, string Who, string Action, string Detail);

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

    public const int BackupKeepDays = 7;
    public string? BackupDaily()
    {
        lock (_writeLock)
        {
            Open();
            var bak = $"{DbPath}.bak-{DateTime.Now:yyyyMMdd}";
            if (File.Exists(bak)) return null;
            using (var cp = _conn!.CreateCommand())
            {
                cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cp.ExecuteNonQuery();
            }
            File.Copy(DbPath, bak, overwrite: true);
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

public static class PasswordHasher
{
    public const int Iterations = 10_000;

    public static string Hash(string password)
    {
        var salt = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
        var hash = Derive(password, salt, Iterations);
        return $"{Iterations}.{Convert.ToHexString(salt).ToLowerInvariant()}.{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

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

    public static string RandomToken()
    {
        var b = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b).ToLowerInvariant();
    }
}
