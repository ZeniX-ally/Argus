using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FctAggregator;

public partial class AggDatabase
{

    public void UpsertDeviceInfo(DeviceInfoRow r)
    {
        if (string.IsNullOrWhiteSpace(r.Machine)) return;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var lastSeen = string.IsNullOrEmpty(r.LastSeen) ? now : r.LastSeen;
        var updated = string.IsNullOrEmpty(r.UpdatedAt) ? now : r.UpdatedAt;
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO device_info
                  (machine, hostname, os, os_version, ip, mac, cpu_model, cpu_cores, cpu_usage,
                   mem_total_mb, mem_used_mb, disk_total_gb, disk_free_gb, uptime_sec, argus_version, last_seen, updated_at)
                VALUES (@m,@hn,@os,@osv,@ip,@mac,@cpuM,@cores,@cpuU,@memT,@memU,@diskT,@diskF,@up,@ver,@last,@upd)
                ON CONFLICT(machine) DO UPDATE SET
                  hostname=excluded.hostname, os=excluded.os, os_version=excluded.os_version,
                  ip=excluded.ip, mac=excluded.mac, cpu_model=excluded.cpu_model, cpu_cores=excluded.cpu_cores,
                  cpu_usage=excluded.cpu_usage, mem_total_mb=excluded.mem_total_mb, mem_used_mb=excluded.mem_used_mb,
                  disk_total_gb=excluded.disk_total_gb, disk_free_gb=excluded.disk_free_gb,
                  uptime_sec=excluded.uptime_sec, argus_version=excluded.argus_version,
                  last_seen=excluded.last_seen, updated_at=excluded.updated_at";
            cmd.Parameters.AddWithValue("@m", r.Machine);
            cmd.Parameters.AddWithValue("@hn", (object?)r.Hostname ?? "");
            cmd.Parameters.AddWithValue("@os", (object?)r.Os ?? "");
            cmd.Parameters.AddWithValue("@osv", (object?)r.OsVersion ?? "");
            cmd.Parameters.AddWithValue("@ip", (object?)r.Ip ?? "");
            cmd.Parameters.AddWithValue("@mac", (object?)r.Mac ?? "");
            cmd.Parameters.AddWithValue("@cpuM", (object?)r.CpuModel ?? "");
            cmd.Parameters.AddWithValue("@cores", r.CpuCores);
            cmd.Parameters.AddWithValue("@cpuU", r.CpuUsage);
            cmd.Parameters.AddWithValue("@memT", r.MemTotalMb);
            cmd.Parameters.AddWithValue("@memU", r.MemUsedMb);
            cmd.Parameters.AddWithValue("@diskT", r.DiskTotalGb);
            cmd.Parameters.AddWithValue("@diskF", r.DiskFreeGb);
            cmd.Parameters.AddWithValue("@up", r.UptimeSec);
            cmd.Parameters.AddWithValue("@ver", (object?)r.ArgusVersion ?? "");
            cmd.Parameters.AddWithValue("@last", lastSeen);
            cmd.Parameters.AddWithValue("@upd", updated);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpsertDeviceLight(string machine, double cpuUsage, int memUsedMb, int memTotalMb)
    {
        if (string.IsNullOrWhiteSpace(machine)) return;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock (_writeLock)
        {
            Open();
            using var ins = _conn!.CreateCommand();
            ins.CommandText = @"INSERT OR IGNORE INTO device_info (machine, last_seen, updated_at) VALUES (@m,@last,@upd)";
            ins.Parameters.AddWithValue("@m", machine);
            ins.Parameters.AddWithValue("@last", now);
            ins.Parameters.AddWithValue("@upd", now);
            ins.ExecuteNonQuery();
            using var upd = _conn.CreateCommand();
            upd.CommandText = @"UPDATE device_info SET cpu_usage=@cpu, mem_used_mb=@used, mem_total_mb=@total, last_seen=@last, updated_at=@upd WHERE machine=@m";
            upd.Parameters.AddWithValue("@cpu", cpuUsage);
            upd.Parameters.AddWithValue("@used", memUsedMb);
            upd.Parameters.AddWithValue("@total", memTotalMb);
            upd.Parameters.AddWithValue("@last", now);
            upd.Parameters.AddWithValue("@upd", now);
            upd.Parameters.AddWithValue("@m", machine);
            upd.ExecuteNonQuery();
        }
    }

    public List<DeviceInfoRow> ListDeviceInfos()
    {
        var list = new List<DeviceInfoRow>();
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT machine, hostname, os, os_version, ip, mac, cpu_model, cpu_cores, cpu_usage, mem_total_mb, mem_used_mb, disk_total_gb, disk_free_gb, uptime_sec, argus_version, last_seen, updated_at FROM device_info ORDER BY machine COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DeviceInfoRow
            {
                Machine = r.IsDBNull(0) ? "" : r.GetString(0),
                Hostname = r.IsDBNull(1) ? "" : r.GetString(1),
                Os = r.IsDBNull(2) ? "" : r.GetString(2),
                OsVersion = r.IsDBNull(3) ? "" : r.GetString(3),
                Ip = r.IsDBNull(4) ? "" : r.GetString(4),
                Mac = r.IsDBNull(5) ? "" : r.GetString(5),
                CpuModel = r.IsDBNull(6) ? "" : r.GetString(6),
                CpuCores = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                CpuUsage = r.IsDBNull(8) ? 0 : r.GetDouble(8),
                MemTotalMb = r.IsDBNull(9) ? 0 : r.GetInt32(9),
                MemUsedMb = r.IsDBNull(10) ? 0 : r.GetInt32(10),
                DiskTotalGb = r.IsDBNull(11) ? 0 : r.GetDouble(11),
                DiskFreeGb = r.IsDBNull(12) ? 0 : r.GetDouble(12),
                UptimeSec = r.IsDBNull(13) ? 0 : r.GetInt64(13),
                ArgusVersion = r.IsDBNull(14) ? "" : r.GetString(14),
                LastSeen = r.IsDBNull(15) ? "" : r.GetString(15),
                UpdatedAt = r.IsDBNull(16) ? "" : r.GetString(16),
            });
        }
        return list;
    }

    public DeviceInfoRow? GetDeviceInfo(string machine)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT machine, hostname, os, os_version, ip, mac, cpu_model, cpu_cores, cpu_usage, mem_total_mb, mem_used_mb, disk_total_gb, disk_free_gb, uptime_sec, argus_version, last_seen, updated_at FROM device_info WHERE machine=@m";
        cmd.Parameters.AddWithValue("@m", machine);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new DeviceInfoRow
        {
            Machine = r.IsDBNull(0) ? "" : r.GetString(0),
            Hostname = r.IsDBNull(1) ? "" : r.GetString(1),
            Os = r.IsDBNull(2) ? "" : r.GetString(2),
            OsVersion = r.IsDBNull(3) ? "" : r.GetString(3),
            Ip = r.IsDBNull(4) ? "" : r.GetString(4),
            Mac = r.IsDBNull(5) ? "" : r.GetString(5),
            CpuModel = r.IsDBNull(6) ? "" : r.GetString(6),
            CpuCores = r.IsDBNull(7) ? 0 : r.GetInt32(7),
            CpuUsage = r.IsDBNull(8) ? 0 : r.GetDouble(8),
            MemTotalMb = r.IsDBNull(9) ? 0 : r.GetInt32(9),
            MemUsedMb = r.IsDBNull(10) ? 0 : r.GetInt32(10),
            DiskTotalGb = r.IsDBNull(11) ? 0 : r.GetDouble(11),
            DiskFreeGb = r.IsDBNull(12) ? 0 : r.GetDouble(12),
            UptimeSec = r.IsDBNull(13) ? 0 : r.GetInt64(13),
            ArgusVersion = r.IsDBNull(14) ? "" : r.GetString(14),
            LastSeen = r.IsDBNull(15) ? "" : r.GetString(15),
            UpdatedAt = r.IsDBNull(16) ? "" : r.GetString(16),
        };
    }

    public void InsertDeviceSample(DeviceSampleRow s)
    {
        if (string.IsNullOrWhiteSpace(s.Machine)) return;
        var ts = string.IsNullOrEmpty(s.Ts) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : s.Ts;
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "INSERT INTO device_samples (machine, ts, cpu_usage, mem_used_mb, disk_free_gb) VALUES (@m,@ts,@cpu,@mem,@disk)";
            cmd.Parameters.AddWithValue("@m", s.Machine);
            cmd.Parameters.AddWithValue("@ts", ts);
            cmd.Parameters.AddWithValue("@cpu", s.CpuUsage);
            cmd.Parameters.AddWithValue("@mem", s.MemUsedMb);
            cmd.Parameters.AddWithValue("@disk", s.DiskFreeGb);
            cmd.ExecuteNonQuery();
        }
    }

    public List<DeviceSampleRow> QueryDeviceSamples(string machine, int limit = 200, string? fromTs = null, string? toTs = null)
    {
        var list = new List<DeviceSampleRow>();
        if (limit <= 0) limit = 200;
        limit = Math.Min(limit, 2000);
        var sql = "SELECT id, machine, ts, cpu_usage, mem_used_mb, disk_free_gb FROM device_samples WHERE machine=@m";
        var ps = new List<(string n, object v)> { ("@m", machine) };
        if (!string.IsNullOrEmpty(fromTs)) { sql += " AND ts >= @from"; ps.Add(("@from", fromTs!)); }
        if (!string.IsNullOrEmpty(toTs)) { sql += " AND ts <= @to"; ps.Add(("@to", toTs!)); }
        sql += " ORDER BY ts DESC LIMIT @lim";
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new DeviceSampleRow
            {
                Id = r.GetInt64(0),
                Machine = r.IsDBNull(1) ? "" : r.GetString(1),
                Ts = r.IsDBNull(2) ? "" : r.GetString(2),
                CpuUsage = r.IsDBNull(3) ? 0 : r.GetDouble(3),
                MemUsedMb = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                DiskFreeGb = r.IsDBNull(5) ? 0 : r.GetDouble(5),
            });
        }
        list.Reverse();
        return list;
    }

    public int PurgeOldDeviceSamples(int retainDays)
    {
        if (retainDays <= 0) retainDays = 7;
        var cutoff = DateTime.Now.AddDays(-retainDays).ToString("yyyy-MM-dd HH:mm:ss");
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM device_samples WHERE ts < @cut";
            cmd.Parameters.AddWithValue("@cut", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    public void UpsertDeviceFct(DeviceFctRow r)
    {
        if (string.IsNullOrWhiteSpace(r.Machine)) return;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var lastSeen = string.IsNullOrEmpty(r.LastSeen) ? now : r.LastSeen;
        var updated = string.IsNullOrEmpty(r.UpdatedAt) ? now : r.UpdatedAt;
        var modelsJson = JsonSerializer.Serialize(r.Models);
        var fwJson = JsonSerializer.Serialize(r.FwVersions.Select(x => new { label = x.Label, version = x.Version }));
        var devJson = JsonSerializer.Serialize(r.Devices.Select(d => new { name = d.Name, port = d.Port, type = d.Type, online = d.Online }));
        var a2lJson = JsonSerializer.Serialize(r.A2lFiles.Select(x => new { label = x.Label, file = x.File }));
        lock (_writeLock)
        {
            Open();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO device_fct (machine, ini_path, found, error, models, fw_versions, devices, a2l_files, last_seen, updated_at)
                VALUES (@m,@path,@found,@err,@models,@fw,@dev,@a2l,@last,@upd)
                ON CONFLICT(machine) DO UPDATE SET
                  ini_path=excluded.ini_path, found=excluded.found, error=excluded.error,
                  models=excluded.models, fw_versions=excluded.fw_versions, devices=excluded.devices, a2l_files=excluded.a2l_files,
                  last_seen=excluded.last_seen, updated_at=excluded.updated_at";
            cmd.Parameters.AddWithValue("@m", r.Machine);
            cmd.Parameters.AddWithValue("@path", (object?)r.IniPath ?? "");
            cmd.Parameters.AddWithValue("@found", r.Found ? 1 : 0);
            cmd.Parameters.AddWithValue("@err", (object?)r.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@models", modelsJson);
            cmd.Parameters.AddWithValue("@fw", fwJson);
            cmd.Parameters.AddWithValue("@dev", devJson);
            cmd.Parameters.AddWithValue("@a2l", a2lJson);
            cmd.Parameters.AddWithValue("@last", lastSeen);
            cmd.Parameters.AddWithValue("@upd", updated);
            cmd.ExecuteNonQuery();
        }
    }

    public DeviceFctRow? GetDeviceFct(string machine)
    {
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT machine, ini_path, found, error, models, fw_versions, devices, a2l_files, last_seen, updated_at FROM device_fct WHERE machine=@m";
        cmd.Parameters.AddWithValue("@m", machine);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var row = new DeviceFctRow
        {
            Machine = r.IsDBNull(0) ? "" : r.GetString(0),
            IniPath = r.IsDBNull(1) ? "" : r.GetString(1),
            Found = !r.IsDBNull(2) && r.GetInt64(2) != 0,
            Error = r.IsDBNull(3) ? null : r.GetString(3),
            LastSeen = r.IsDBNull(8) ? "" : r.GetString(8),
            UpdatedAt = r.IsDBNull(9) ? "" : r.GetString(9),
        };
        try
        {
            var modelsJson = r.IsDBNull(4) ? "[]" : r.GetString(4);
            using var doc = JsonDocument.Parse(modelsJson);
            foreach (var e in doc.RootElement.EnumerateArray()) row.Models.Add(e.GetString() ?? "");
        }
        catch { }
        try
        {
            var fwJson = r.IsDBNull(5) ? "[]" : r.GetString(5);
            using var doc = JsonDocument.Parse(fwJson);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var label = e.TryGetProperty("label", out var la) ? la.GetString() ?? "" : "";
                var ver = e.TryGetProperty("version", out var va) ? va.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(label)) row.FwVersions.Add((label, ver));
            }
        }
        catch { }
        try
        {
            var devJson = r.IsDBNull(6) ? "[]" : r.GetString(6);
            using var doc = JsonDocument.Parse(devJson);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var d = new FctDeviceInfo();
                d.Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                d.Port = e.TryGetProperty("port", out var p) ? p.GetString() ?? "" : "";
                d.Type = e.TryGetProperty("type", out var t) ? t.GetString() ?? "com" : "com";
                if (e.TryGetProperty("online", out var o)) d.Online = o.ValueKind == JsonValueKind.True;
                row.Devices.Add(d);
            }
        }
        catch { }
        try
        {
            var a2lJson = r.IsDBNull(7) ? "[]" : r.GetString(7);
            using var doc = JsonDocument.Parse(a2lJson);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var label = e.TryGetProperty("label", out var la) ? la.GetString() ?? "" : "";
                var file = e.TryGetProperty("file", out var fa) ? fa.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(label)) row.A2lFiles.Add((label, file));
            }
        }
        catch { }
        return row;
    }

    public List<DeviceFctRow> ListDeviceFcts()
    {
        var list = new List<DeviceFctRow>();
        using var conn = OpenReader();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT machine, ini_path, found, error, models, fw_versions, devices, a2l_files, last_seen, updated_at FROM device_fct ORDER BY machine COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new DeviceFctRow
            {
                Machine = r.IsDBNull(0) ? "" : r.GetString(0),
                IniPath = r.IsDBNull(1) ? "" : r.GetString(1),
                Found = !r.IsDBNull(2) && r.GetInt64(2) != 0,
                Error = r.IsDBNull(3) ? null : r.GetString(3),
                LastSeen = r.IsDBNull(8) ? "" : r.GetString(8),
                UpdatedAt = r.IsDBNull(9) ? "" : r.GetString(9),
            };
            try
            {
                var modelsJson = r.IsDBNull(4) ? "[]" : r.GetString(4);
                using var doc = JsonDocument.Parse(modelsJson);
                foreach (var e in doc.RootElement.EnumerateArray()) row.Models.Add(e.GetString() ?? "");
            }
            catch { }
            list.Add(row);
        }
        return list;
    }
}
