using Microsoft.Data.Sqlite;

namespace FctAggregator;

public partial class AggDatabase
{
    public event Action<MaintenanceRecord, string, string>? MaintenanceStatusChanged;

    private void NotifyStatusChanged(MaintenanceRecord rec, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        try { MaintenanceStatusChanged?.Invoke(rec, from, to); }
        catch (Exception ex) { Logger.Warning($"状态变更回调异常: {ex.Message}"); }
    }

    public int CreateMaintenance(MaintenanceRecord m)
    {
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
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
            cmd.Parameters.AddWithValue("@sev", m.Severity ?? MaintenanceMeta.DefaultSeverity);
            cmd.Parameters.AddWithValue("@status", string.IsNullOrEmpty(m.Status) ? MaintenanceMeta.DefaultStatus : m.Status);
            cmd.Parameters.AddWithValue("@resolver", (object?)m.Resolver ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reso", (object?)m.Resolution ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@notes", (object?)m.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created", (object?)(m.CreatedAt ?? ""));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public List<MaintenanceRecord> ListMaintenance(string statusFilter = "", int limit = 500)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        var where = string.IsNullOrEmpty(statusFilter) ? "" : "WHERE status = @s COLLATE NOCASE";
        cmd.CommandText = $@"SELECT id, station_id, equipment_model, equipment_sn, fail_item, fail_reason,
            severity, status, resolver, resolution, notes, created_at, updated_at
            FROM maintenance_records {where}
            ORDER BY COALESCE(NULLIF(updated_at,''), created_at, '') DESC, id DESC
            LIMIT @lim";
        if (!string.IsNullOrEmpty(statusFilter)) cmd.Parameters.AddWithValue("@s", statusFilter);
        cmd.Parameters.AddWithValue("@lim", limit <= 0 ? 500 : Math.Min(limit, 2000));
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
        using var conn = OpenReader();
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

    public MaintenanceRecord? GetMaintenance(int id)
    {
        using var conn = OpenReader();
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

    public bool UpdateMaintenanceStatus(int id, string status)
    {
        status = MaintenanceMeta.Normalize(status);
        string from;
        using (var conn = OpenReader())
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT status FROM maintenance_records WHERE id=@id";
            sel.Parameters.AddWithValue("@id", id);
            var v = sel.ExecuteScalar();
            from = v == null || v is DBNull ? "" : v.ToString() ?? "";
            if (string.Equals(from, status, StringComparison.OrdinalIgnoreCase)) return true;
        }
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
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
        m.Status = MaintenanceMeta.Normalize(m.Status);
        string from = "";
        using (var conn = OpenReader())
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT status FROM maintenance_records WHERE id=@id";
            sel.Parameters.AddWithValue("@id", m.Id);
            var v = sel.ExecuteScalar();
            from = v == null || v is DBNull ? "" : v.ToString() ?? "";
        }
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
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
            cmd.Parameters.AddWithValue("@sev", m.Severity ?? "major");
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

    public bool DeleteMaintenance(int id)
    {
        int affected;
        lock (_writeLock)
        {
            Open();
            using var tx = _conn!.BeginTransaction();
            int del;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM maintenance_records WHERE id=@id";
                cmd.Parameters.AddWithValue("@id", id);
                del = cmd.ExecuteNonQuery();
            }
            if (del > 0)
            {
                using var seq = _conn.CreateCommand();
                seq.Transaction = tx;
                seq.CommandText = @"UPDATE sqlite_sequence SET seq = (SELECT COALESCE(MAX(id),0) FROM maintenance_records) WHERE name='maintenance_records'";
                seq.ExecuteNonQuery();
            }
            tx.Commit();
            affected = del;
        }
        return affected > 0;
    }

    public List<string> RosterResolvers()
    {
        var list = new List<string>();
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM resolvers ORDER BY name COLLATE NOCASE ASC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public List<string> DistinctResolvers(int limit = 30)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT resolver FROM maintenance_records WHERE resolver IS NOT NULL AND TRIM(resolver) <> ''";
        var count = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                foreach (var who in ResolverUtil.Split(r.GetString(0)))
                    count[who] = count.GetValueOrDefault(who) + 1;
        return count.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(limit).Select(kv => kv.Key).ToList();
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
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO resolvers(name) VALUES(@n)";
            cmd.Parameters.AddWithValue("@n", name);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool DeleteResolver(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return false;
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM resolvers WHERE name = @n COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@n", name);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int RenameResolver(string oldName, string newName, bool syncRecords)
    {
        oldName = (oldName ?? "").Trim();
        newName = (newName ?? "").Trim();
        if (oldName.Length == 0 || newName.Length == 0) return 0;
        lock (_writeLock)
        {
            Open();
            using var tx = _conn!.BeginTransaction();
            int synced = 0;
            using (var cmd = _conn.CreateCommand())
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
                using (var q = _conn.CreateCommand())
                {
                    q.Transaction = tx;
                    q.CommandText = @"SELECT id, resolver FROM maintenance_records WHERE resolver IS NOT NULL AND TRIM(resolver) <> ''";
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
                    using var up = _conn.CreateCommand();
                    up.Transaction = tx;
                    up.CommandText = @"UPDATE maintenance_records SET resolver=@v, updated_at=datetime('now','localtime') WHERE id=@id";
                    up.Parameters.AddWithValue("@v", val);
                    up.Parameters.AddWithValue("@id", id);
                    synced += up.ExecuteNonQuery();
                }
            }
            tx.Commit();
            return synced;
        }
    }

    public int CountRecordsByResolver(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return 0;
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT resolver FROM maintenance_records WHERE resolver IS NOT NULL AND TRIM(resolver) <> ''";
        int n = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (ResolverUtil.Contains(r.GetString(0), name)) n++;
        return n;
    }

    private const string TodoWatermarkKey = "todo_sync_last_id";
    public const int TodoViewLimit = 300;

    public string? GetMeta(string key)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM app_meta WHERE k=@k";
        cmd.Parameters.AddWithValue("@k", key);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : v.ToString();
    }

    public void SetMeta(string key, string value)
    {
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "INSERT INTO app_meta(k,v) VALUES(@k,@v) ON CONFLICT(k) DO UPDATE SET v=@v";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.ExecuteNonQuery();
        }
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
        lock (_writeLock)
        {
            Open();
            long watermark = 0;
            var wm = GetMeta(_conn!, TodoWatermarkKey);
            if (wm != null) long.TryParse(wm, out watermark);
            long maxId;
            using (var c = _conn!.CreateCommand())
            {
                c.CommandText = "SELECT COALESCE(MAX(id),0) FROM agg_records";
                maxId = Convert.ToInt64(c.ExecuteScalar() ?? 0L);
            }
            var groups = new Dictionary<(string, string), AggTodoAgg>();
            if (maxId > watermark)
            {
                var dismissed = new HashSet<(string, string)>();
                using (var dc = _conn!.CreateCommand())
                {
                    dc.CommandText = "SELECT fail_item, station_id FROM dismissed_todos";
                    using var dr = dc.ExecuteReader();
                    while (dr.Read()) dismissed.Add((dr.GetString(0), dr.GetString(1)));
                }
                using var c = _conn!.CreateCommand();
                c.CommandText = @"
                    SELECT fail_reason, station_id, COALESCE(model,''),
                           COALESCE(NULLIF(batch_timestamp,''), test_date),
                           test_date
                      FROM agg_records
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
                    try { key = TodoGrouping.KeyOf(item); } catch { continue; }
                    if (key.Length == 0) continue;
                    var station = r.IsDBNull(1) ? "" : r.GetString(1);
                    if (dismissed.Contains((key, station))) continue;
                    var model = r.IsDBNull(2) ? "" : r.GetString(2);
                    var ts = TimeUtil.Normalize(r.IsDBNull(3) ? "" : r.GetString(3));
                    if (!groups.TryGetValue((key, station), out var agg))
                        groups[(key, station)] = agg = new AggTodoAgg { Model = model };
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
            using (var tx = _conn!.BeginTransaction())
            {
                foreach (var ((key, station), agg) in groups)
                {
                    string? oldVariants = null;
                    int id = 0;
                    using (var sel = _conn.CreateCommand())
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
                        using var ins = _conn.CreateCommand();
                        ins.Transaction = tx;
                        ins.CommandText = @"
                            INSERT INTO todo_items
                                (group_key, station_id, title, model, variants, variant_count, fail_count, first_seen, last_seen, state)
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
                        using var upd = _conn.CreateCommand();
                        upd.Transaction = tx;
                        upd.CommandText = @"
                            UPDATE todo_items SET title=@t, model=CASE WHEN COALESCE(model,'')='' THEN @m ELSE model END,
                                variants=@vs, variant_count=@vc, fail_count=fail_count+@c,
                                first_seen=CASE WHEN COALESCE(first_seen,'')='' OR (@f<>'' AND @f<first_seen) THEN @f ELSE first_seen END,
                                last_seen=CASE WHEN @l>COALESCE(last_seen,'') THEN @l ELSE last_seen END,
                                updated_at=datetime('now','localtime') WHERE id=@id";
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
                SetMeta(_conn!, TodoWatermarkKey, maxId.ToString(), tx);
                tx.Commit();
            }
            ReconcileTodoStates(_conn!);
            return created;
        }
    }

    private sealed class AggTodoAgg
    {
        public int Count;
        public string Model = "";
        public string First = "";
        public string Last = "";
        public readonly List<string> Variants = new();
    }

    private static void ReconcileTodoStates(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE todo_items SET state='pending', maintenance_id=NULL, resolved_at=NULL, updated_at=datetime('now','localtime')
              WHERE maintenance_id IS NOT NULL AND maintenance_id NOT IN (SELECT id FROM maintenance_records);
            UPDATE todo_items SET state='resolved', resolved_at=(SELECT COALESCE(NULLIF(m.updated_at,''), m.created_at) FROM maintenance_records m WHERE m.id=todo_items.maintenance_id), updated_at=datetime('now','localtime')
              WHERE maintenance_id IS NOT NULL AND (SELECT status FROM maintenance_records WHERE id=todo_items.maintenance_id) IN ('resolved','closed');
            UPDATE todo_items SET state='ack', resolved_at=NULL, updated_at=datetime('now','localtime')
              WHERE maintenance_id IS NOT NULL AND (SELECT status FROM maintenance_records WHERE id=todo_items.maintenance_id) NOT IN ('resolved','closed');
            UPDATE todo_items SET state='pending', maintenance_id=NULL, resolved_at=NULL, updated_at=datetime('now','localtime')
              WHERE state='resolved' AND COALESCE(resolved_at,'')<>'' AND COALESCE(last_seen,'') > resolved_at;
        ";
        cmd.ExecuteNonQuery();
    }

    public List<TodoItem> ListTodoView(DateTime? from = null, DateTime? to = null, int limit = TodoViewLimit)
    {
        var list = new List<TodoItem>();
        using (var conn = OpenReader())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, group_key, station_id, title, COALESCE(model,''), COALESCE(variants,''), variant_count, fail_count, COALESCE(first_seen,''), COALESCE(last_seen,'') FROM todo_items WHERE state='pending' ORDER BY fail_count DESC, last_seen DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var it = new TodoItem { Id = r.GetInt32(0), GroupKey = r.GetString(1), StationId = r.GetString(2), Title = r.GetString(3), Model = r.GetString(4), VariantCount = r.GetInt32(6), TotalCount = r.GetInt32(7), FirstSeen = r.GetString(8), LastSeen = r.GetString(9) };
                var vs = r.GetString(5);
                if (vs.Length > 0) it.Variants.AddRange(vs.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                list.Add(it);
            }
        }
        if (list.Count == 0) return list;
        if (from != null || to != null)
        {
            var ranged = new Dictionary<(string, string), (int cnt, string first, string last)>();
            using (var conn = OpenReader())
            using (var cmd = conn.CreateCommand())
            {
                var where = "WHERE result='FAIL' AND fail_reason IS NOT NULL AND TRIM(fail_reason) <> ''";
                if (from != null) where += " AND test_date >= @a";
                if (to != null) where += " AND test_date <= @b";
                cmd.CommandText = $@"SELECT fail_reason, station_id, COUNT(*), MIN(COALESCE(NULLIF(batch_timestamp,''), substr(test_date,1,4)||'-'||substr(test_date,5,2)||'-'||substr(test_date,7,2)||' 00:00:00')), MAX(COALESCE(NULLIF(batch_timestamp,''), substr(test_date,1,4)||'-'||substr(test_date,5,2)||'-'||substr(test_date,7,2)||' 00:00:00')) FROM agg_records {where} GROUP BY fail_reason, station_id";
                if (from != null) cmd.Parameters.AddWithValue("@a", from.Value.ToString("yyyyMMdd"));
                if (to != null) cmd.Parameters.AddWithValue("@b", to.Value.ToString("yyyyMMdd"));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string key;
                    try { key = TodoGrouping.KeyOf(r.IsDBNull(0) ? "" : r.GetString(0)); } catch { continue; }
                    if (key.Length == 0) continue;
                    var station = r.IsDBNull(1) ? "" : r.GetString(1);
                    var cnt = r.GetInt32(2);
                    var f = TimeUtil.Normalize(r.IsDBNull(3) ? "" : r.GetString(3));
                    var l = TimeUtil.Normalize(r.IsDBNull(4) ? "" : r.GetString(4));
                    if (ranged.TryGetValue((key, station), out var old))
                        ranged[(key, station)] = (old.cnt + cnt, old.first.Length == 0 || (f.Length > 0 && string.CompareOrdinal(f, old.first) < 0) ? f : old.first, string.CompareOrdinal(l, old.last) > 0 ? l : old.last);
                    else ranged[(key, station)] = (cnt, f, l);
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
        list = list.OrderByDescending(x => x.SortCount).ThenByDescending(x => x.LastSeen, StringComparer.Ordinal).Take(limit).ToList();
        return list;
    }

    public int CountPendingTodos()
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM todo_items WHERE state='pending'";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public TodoItem? GetTodoItem(int id)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, group_key, station_id, title, COALESCE(model,''), COALESCE(variants,''), variant_count, fail_count, COALESCE(first_seen,''), COALESCE(last_seen,''), state FROM todo_items WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var it = new TodoItem { Id = r.GetInt32(0), GroupKey = r.GetString(1), StationId = r.GetString(2), Title = r.GetString(3), Model = r.GetString(4), VariantCount = r.GetInt32(6), TotalCount = r.GetInt32(7), FirstSeen = r.GetString(8), LastSeen = r.GetString(9), State = r.GetString(10) };
        var vs = r.GetString(5);
        if (vs.Length > 0) it.Variants.AddRange(vs.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        it.RangeCount = it.TotalCount;
        return it;
    }

    public int AcknowledgeTodo(int todoId, MaintenanceRecord rec)
    {
        var todo = GetTodoItem(todoId);
        if (todo == null) throw new InvalidOperationException($"待办 #{todoId} 不存在（可能已被处理）");
        if (string.IsNullOrWhiteSpace(rec.StationId)) rec.StationId = todo.StationId;
        if (string.IsNullOrWhiteSpace(rec.FailItem)) rec.FailItem = todo.Title;
        if (string.IsNullOrEmpty(rec.Status)) rec.Status = "open";
        if (string.IsNullOrWhiteSpace(rec.Notes) && todo.VariantCount > 1)
            rec.Notes = TodoGrouping.BuildSourceItemsNote(todo.Variants.Take(20));
        var id = CreateMaintenance(rec);
        rec.Id = id;
        NotifyStatusChanged(rec, "", rec.Status);
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"UPDATE todo_items SET state='ack', maintenance_id=@mid, resolved_at=NULL, updated_at=datetime('now','localtime') WHERE id=@id";
            cmd.Parameters.AddWithValue("@mid", id);
            cmd.Parameters.AddWithValue("@id", todoId);
            cmd.ExecuteNonQuery();
        }
        return id;
    }

    public bool DeleteTodo(int todoId)
    {
        string? key = null, station = null, model = null;
        using (var conn = OpenReader())
        {
            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT group_key, station_id, COALESCE(model,'') FROM todo_items WHERE id=@id";
            sel.Parameters.AddWithValue("@id", todoId);
            using var r = sel.ExecuteReader();
            if (!r.Read()) return false;
            key = r.GetString(0); station = r.GetString(1); model = r.GetString(2);
        }
        lock (_writeLock)
        {
            Open();
            using var del = _conn!.CreateCommand();
            del.CommandText = "DELETE FROM todo_items WHERE id=@id";
            del.Parameters.AddWithValue("@id", todoId);
            del.ExecuteNonQuery();
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(station))
            {
                using var ins = _conn.CreateCommand();
                ins.CommandText = "INSERT OR IGNORE INTO dismissed_todos(fail_item, station_id, model) VALUES(@k,@s,@m)";
                ins.Parameters.AddWithValue("@k", key);
                ins.Parameters.AddWithValue("@s", station);
                ins.Parameters.AddWithValue("@m", (object?)model ?? "");
                ins.ExecuteNonQuery();
            }
        }
        return true;
    }

    public Dictionary<string, int> CountFailByItems(IEnumerable<string> items, string stationId = "")
    {
        var list = items.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (list.Count == 0) return result;
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        var names = new List<string>();
        for (int i = 0; i < list.Count; i++) { names.Add($"@p{i}"); cmd.Parameters.AddWithValue($"@p{i}", list[i]); }
        var where = $"WHERE result='FAIL' AND fail_reason IN ({string.Join(",", names)})";
        if (!string.IsNullOrEmpty(stationId)) { where += " AND station_id=@st"; cmd.Parameters.AddWithValue("@st", stationId); }
        cmd.CommandText = $"SELECT fail_reason, COUNT(*) FROM agg_records {where} GROUP BY fail_reason";
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.IsDBNull(0) ? "" : r.GetString(0)] = r.GetInt32(1);
        return result;
    }

    public TodoItem? GetTodoByMaintenance(int maintenanceId)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM todo_items WHERE maintenance_id=@id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", maintenanceId);
        var v = cmd.ExecuteScalar();
        if (v == null || v is DBNull) return null;
        return GetTodoItem(Convert.ToInt32(v));
    }

    public List<FailItemSource> FailItemSources(string stationId = "", int days = 0, int limit = 2000)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        var where = "WHERE result='FAIL'";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id=@s";
        string? cutoff = null;
        if (days > 0) { cutoff = DateTime.Today.AddDays(-days).ToString("yyyyMMdd"); where += " AND test_date >= @c"; }
        cmd.CommandText = $@"SELECT COALESCE(fail_reason,''), COALESCE(model,''), COALESCE(station_id,''), COALESCE(batch_timestamp,''), COALESCE(test_date,''), COALESCE(xml_path,'') FROM agg_records {where} ORDER BY id DESC LIMIT @lim";
        if (!string.IsNullOrEmpty(stationId)) cmd.Parameters.AddWithValue("@s", stationId);
        if (cutoff != null) cmd.Parameters.AddWithValue("@c", cutoff);
        cmd.Parameters.AddWithValue("@lim", limit);
        var list = new List<FailItemSource>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new FailItemSource { FirstFailItem = r.GetString(0), Model = r.GetString(1), StationId = r.GetString(2), Timestamp = r.GetString(3), TestDate = r.GetString(4), XmlPath = r.GetString(5) });
        return list;
    }

    public int CountFailRecords(string failItem, string stationId)
    {
        if (string.IsNullOrWhiteSpace(failItem)) return 0;
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        var where = "WHERE result='FAIL' AND fail_reason=@item";
        if (!string.IsNullOrEmpty(stationId)) where += " AND station_id=@st";
        cmd.CommandText = $"SELECT COUNT(*) FROM agg_records {where}";
        cmd.Parameters.AddWithValue("@item", failItem);
        if (!string.IsNullOrEmpty(stationId)) cmd.Parameters.AddWithValue("@st", stationId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
