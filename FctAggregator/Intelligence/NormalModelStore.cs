using System.Globalization;
using System.Text.Json;

namespace FctAggregator;

public sealed class NormalModelRow
{
    public string Source { get; set; } = "";
    public string Model { get; set; } = "";
    public string SignalKey { get; set; } = "";
    public long N { get; set; }
    public double Mean { get; set; }
    public double Sigma { get; set; }
    public double P01 { get; set; }
    public double P50 { get; set; }
    public double P99 { get; set; }
    public double MinV { get; set; } = double.MaxValue;
    public double MaxV { get; set; } = double.MinValue;
    public string LastTs { get; set; } = "";
    public string Status { get; set; } = "learning";
    public string QState { get; set; } = "";   // Welford+P² 持久化状态（JSON）
}

/// <summary>
/// 正常态模型编排：每 (source, model, signal_key) 一行，增量更新 Welford+P²。
/// Observe 读-改-写全程进程内锁；调用方负责吞异常。
/// </summary>
public static class NormalModelStore
{
    public const string SourceMeasurement = "measurement";
    public const string SourceWaveform = "waveform";
    public const string SourceDevice = "device";

    private static readonly object _lock = new();

    public static void Observe(Database db, int minSamples, string source, string model, string signalKey, double value, string ts)
    {
        if (!double.IsFinite(value)) return;
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(signalKey)) return;
        model ??= "";
        minSamples = Math.Max(1, minSamples);
        lock (_lock)
        {
            var row = db.GetNormalModel(source, model, signalKey);

            var w = new Welford();
            P2Quantile q1, q5, q9;
            double minV = value, maxV = value;
            if (row != null)
            {
                w.Restore(row.N, row.Mean, ReadM2(row.QState));
                q1 = P2Quantile.Deserialize(ExtractQ(row.QState, "q01")) ?? new P2Quantile(0.01);
                q5 = P2Quantile.Deserialize(ExtractQ(row.QState, "q50")) ?? new P2Quantile(0.5);
                q9 = P2Quantile.Deserialize(ExtractQ(row.QState, "q99")) ?? new P2Quantile(0.99);
                if (row.MinV < minV) minV = row.MinV;
                if (row.MaxV > maxV) maxV = row.MaxV;
            }
            else
            {
                q1 = new P2Quantile(0.01); q5 = new P2Quantile(0.5); q9 = new P2Quantile(0.99);
            }

            w.Update(value);
            q1.Update(value); q5.Update(value); q9.Update(value);
            string status = w.N >= minSamples ? "ready" : "learning";

            var next = new NormalModelRow
            {
                Source = source, Model = model, SignalKey = signalKey,
                N = w.N, Mean = w.Mean, Sigma = w.Sigma,
                P01 = q1.Quantile(), P50 = q5.Quantile(), P99 = q9.Quantile(),
                MinV = minV, MaxV = maxV, LastTs = ts, Status = status,
            };
            next.QState = JsonSerializer.Serialize(new QState
            {
                m2 = w.Snapshot().m2,
                q01 = q1.Serialize(), q50 = q5.Serialize(), q99 = q9.Serialize(),
            });
            db.UpsertNormalModel(next);
        }
    }

    /// <summary>stale 惰性判定：ready 但 last_ts 距 now 超 stale_days → stale（不改库，评分时调用）。</summary>
    public static string EffectiveStatus(NormalModelRow row, DateTime now, int staleDays)
    {
        if (row.Status != "ready") return row.Status;
        if (DateTime.TryParseExact(row.LastTs, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
        {
            if ((now - last).TotalDays > Math.Max(1, staleDays)) return "stale";
        }
        return row.Status;
    }

    private sealed class QState
    {
        public double m2 { get; set; }
        public string? q01 { get; set; }
        public string? q50 { get; set; }
        public string? q99 { get; set; }
    }

    private static double ReadM2(string qstate)
    {
        try { return JsonSerializer.Deserialize<QState>(qstate)?.m2 ?? 0; }
        catch { return 0; }
    }

    private static string? ExtractQ(string qstate, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(qstate);
            return doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }
}
