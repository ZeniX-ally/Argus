using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FctAggregator;

public static class FctIniWatcher
{
    public static string ComputeHash(DeviceFctRow row)
    {
        var payload = new
        {
            models = row.Models.OrderBy(x=>x).ToArray(),
            fw = row.FwVersions.OrderBy(x=>x.Label).Select(x=> x.Label+":"+x.Version).ToArray(),
            devs = row.Devices.OrderBy(x=>x.Port).Select(d=> d.Port+"|"+d.Name+"|"+d.Type).ToArray(),
            a2l = row.A2lFiles.OrderBy(x=>x.Label).Select(x=> x.Label+":"+x.File).ToArray(),
            found = row.Found,
            path = row.IniPath ?? ""
        };
        var json = JsonSerializer.Serialize(payload);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static List<string> Diff(DeviceFctRow? oldRow, DeviceFctRow newRow)
    {
        var changes = new List<string>();
        if(oldRow==null) { changes.Add("首次上报"); return changes; }
        var oldModels = new HashSet<string>(oldRow.Models, StringComparer.OrdinalIgnoreCase);
        var newModels = new HashSet<string>(newRow.Models, StringComparer.OrdinalIgnoreCase);
        foreach(var m in newModels.Except(oldModels)) changes.Add($"型号新增: {m}");
        foreach(var m in oldModels.Except(newModels)) changes.Add($"型号移除: {m}");
        var oldFw = oldRow.FwVersions.ToDictionary(x=>x.Label, x=>x.Version, StringComparer.OrdinalIgnoreCase);
        var newFw = newRow.FwVersions.ToDictionary(x=>x.Label, x=>x.Version, StringComparer.OrdinalIgnoreCase);
        foreach(var kv in newFw)
        {
            if(!oldFw.TryGetValue(kv.Key, out var ov)) changes.Add($"FW 新增 {kv.Key}={kv.Value}");
            else if(ov != kv.Value) changes.Add($"FW 变更 {kv.Key}: {ov} -> {kv.Value}");
        }
        foreach(var kv in oldFw.Where(k=>!newFw.ContainsKey(k.Key))) changes.Add($"FW 移除 {kv.Key}={kv.Value}");
        var oldDevs = oldRow.Devices.ToDictionary(x=>x.Port, StringComparer.OrdinalIgnoreCase);
        var newDevs = newRow.Devices.ToDictionary(x=>x.Port, StringComparer.OrdinalIgnoreCase);
        foreach(var kv in newDevs.Where(k=>!oldDevs.ContainsKey(k.Key))) changes.Add($"设备新增 {kv.Key} ({kv.Value.Name})");
        foreach(var kv in oldDevs.Where(k=>!newDevs.ContainsKey(k.Key))) changes.Add($"设备移除 {kv.Key} ({kv.Value.Name})");
        if(oldRow.Found != newRow.Found) changes.Add($"Found: {oldRow.Found} -> {newRow.Found}");
        if(oldRow.IniPath != newRow.IniPath) changes.Add($"路径: {oldRow.IniPath} -> {newRow.IniPath}");
        return changes;
    }

    public static bool CheckAndLog(AggDatabase db, DeviceFctRow newRow)
    {
        if(string.IsNullOrWhiteSpace(newRow.Machine)) return false;
        var old = db.GetDeviceFct(newRow.Machine);
        if(old != null && ComputeHash(old) == ComputeHash(newRow)) return false;
        var changes = Diff(old, newRow);
        bool isFirst = old==null;
        if(!isFirst && changes.Count==0) return false;
        if(!isFirst)
        {
            var oh = ComputeHash(old!);
            var nh = ComputeHash(newRow);
            if(oh==nh) return false;
        }
        var detail = string.Join("; ", changes);
        var hash = ComputeHash(newRow);
        db.InsertFctChange(newRow.Machine, detail, hash);
        try{ db.UpsertDeviceFct(newRow); } catch{}
        try
        {
            var wh = AppConfig.Instance.AggWebhookUrl;
            if(!string.IsNullOrWhiteSpace(wh))
                Logger.Info($"[FCT变更] {newRow.Machine}: {detail}");
        } catch {}
        return true;
    }
}
