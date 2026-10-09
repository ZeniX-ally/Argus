using System.Text.Json;

namespace FctAggregator;

public sealed class LearningSnapshot
{
    public bool Enabled { get; set; }
    public string Heartbeat { get; set; } = "";
    public int Total { get; set; }
    public int Ready { get; set; }
    public int Learning { get; set; }
    public int Stale { get; set; }
    public List<NormalModelRow> Models { get; set; } = new();
    public List<DeviationEvent> Events { get; set; } = new();

    public static LearningSnapshot Capture(Database db, AppConfig cfg, DateTime? now = null)
    {
        var snap = new LearningSnapshot { Enabled = cfg?.LearnNormalEnabled == true };
        if (db == null) return snap;
        try
        {
            snap.Heartbeat = db.GetMeta(NormalLearners.MetaHeartbeat) ?? "";
            var cov = db.CountNormalModels(now ?? DateTime.Now, cfg?.LearnNormalStaleDays ?? 14);
            snap.Total = cov.Total; snap.Ready = cov.Ready; snap.Learning = cov.Learning; snap.Stale = cov.Stale;
            snap.Models = db.ListNormalModels(limit: 500);
            snap.Events = db.ListDeviationEvents(100);
        }
        catch (Exception ex) { Logger.Warning($"[正常态] 快照读取失败: {ex.Message}"); }
        return snap;
    }

    public string HeartbeatSummary()
    {
        if (string.IsNullOrWhiteSpace(Heartbeat)) return "尚无心跳（开关关或尚未摄取）";
        try
        {
            using var doc = JsonDocument.Parse(Heartbeat);
            var r = doc.RootElement;
            string One(string name)
            {
                if (!r.TryGetProperty(name, out var n)) return $"{name}:-";
                var u = n.TryGetProperty("updates", out var uv) ? uv.GetInt64() : 0;
                var e = n.TryGetProperty("errors", out var ev) ? ev.GetInt64() : 0;
                return $"{name} 更新 {u} / 错 {e}";
            }
            var flushed = r.TryGetProperty("flushed_at", out var f) ? f.GetString() : "";
            return string.Join("  ·  ", new[] { One("measurement"), One("waveform"), One("device") }) +
                   (string.IsNullOrEmpty(flushed) ? "" : $"  （{flushed}）");
        }
        catch { return Heartbeat; }
    }
}
