namespace FctAggregator;

public sealed class PassDeviationPoint
{
    public long RecordId { get; set; }
    public string Ts { get; set; } = "";
    public string TestName { get; set; } = "";
    public string Model { get; set; } = "";
    public double Value { get; set; }
    public double Z { get; set; }
}

public sealed class DeviationSeries
{
    public string TestName { get; set; } = "";
    public string Model { get; set; } = "";
    public List<(int PassIndex, PassDeviationPoint Point)> Points { get; set; } = new();
}

/// <summary>按 PASS 时间序构建多测项 |z| 序列（纵轴 z，横轴每条 PASS）。</summary>
public static class DeviationSeriesBuilder
{
    static readonly string[] Palette =
    {
        "#00B4FF", "#27AE60", "#E74C3C", "#F39C12", "#9B59B6", "#1ABC9C", "#E67E22", "#3498DB",
    };

    public static (List<string> PassLabels, List<DeviationSeries> Series) Build(
        Database db, AppConfig cfg, int windowDays = 7, int maxSignals = 6, int maxPasses = 400, DateTime? anchor = null)
    {
        if (db == null || cfg == null) return (new List<string>(), new List<DeviationSeries>());
        windowDays = Math.Clamp(windowDays, 1, 90);
        maxSignals = Math.Clamp(maxSignals, 1, 12);
        maxPasses = Math.Clamp(maxPasses, 50, 2000);

        var to = (anchor ?? DateTime.Now).Date;
        var from = to.AddDays(-(windowDays - 1));
        var fromYmd = from.ToString("yyyy-MM-dd");
        var toYmd = to.ToString("yyyy-MM-dd");

        var now = anchor ?? DateTime.Now;
        var signals = PickSignals(db, cfg, fromYmd, toYmd, maxSignals, now);
        if (signals.Count == 0) return (new List<string>(), new List<DeviationSeries>());

        var raw = db.ListPassMeasurementPoints(fromYmd, toYmd, signals, maxRows: maxPasses * maxSignals);
        if (raw.Count == 0) return (new List<string>(), new List<DeviationSeries>());

        var passOrder = raw
            .Select(r => (r.RecordId, r.Ts))
            .Distinct()
            .OrderBy(p => p.Ts, StringComparer.Ordinal)
            .ThenBy(p => p.RecordId)
            .Take(maxPasses)
            .ToList();
        var passIndex = new Dictionary<(long, string), int>();
        var passLabels = new List<string>(passOrder.Count);
        for (int i = 0; i < passOrder.Count; i++)
        {
            passIndex[passOrder[i]] = i;
            var ts = passOrder[i].Ts;
            passLabels.Add(ts.Length >= 16 ? ts[5..16] : ts);
        }

        var bySignal = new Dictionary<string, DeviationSeries>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in raw)
        {
            if (!passIndex.TryGetValue((row.RecordId, row.Ts), out var xi)) continue;
            var key = row.TestName;
            if (!bySignal.TryGetValue(key, out var ser))
            {
                ser = new DeviationSeries { TestName = row.TestName, Model = row.Model };
                bySignal[key] = ser;
            }
            var z = ComputeAbsZ(db, cfg, row.Model, row.TestName, row.Value, now);
            if (z == null) continue;
            ser.Points.Add((xi, new PassDeviationPoint
            {
                RecordId = row.RecordId, Ts = row.Ts, TestName = row.TestName,
                Model = row.Model, Value = row.Value, Z = z.Value,
            }));
        }

        var series = new List<DeviationSeries>();
        int ci = 0;
        foreach (var name in signals)
        {
            if (!bySignal.TryGetValue(name, out var ser) || ser.Points.Count == 0) continue;
            ser.Points.Sort((a, b) => a.PassIndex.CompareTo(b.PassIndex));
            series.Add(ser);
            ci++;
        }
        return (passLabels, series);
    }

    private static double? ComputeAbsZ(Database db, AppConfig cfg, string model, string testName, double value, DateTime now)
    {
        var row = db.GetNormalModel(NormalModelStore.SourceMeasurement, model ?? "", testName);
        if (row == null) return null;
        if (!CpkAnalyzer.ShouldTreatAsProcess(testName, new MeasureStatsRow
        {
            TestName = testName, Mean = row.Mean, Sigma = row.Sigma, N = (int)Math.Min(int.MaxValue, row.N),
        }))
            return null;
        var score = DeviationScorer.ScoreOne(row, value, now, cfg.LearnNormalStaleDays);
        if (score == null) return null;
        var sigma = Math.Max(row.Sigma, DeviationScorer.SigmaFloor);
        if (!double.IsFinite(sigma) || sigma <= 0) return null;
        var z = Math.Abs(value - row.Mean) / sigma;
        return double.IsFinite(z) ? Math.Round(z, 3) : null;
    }

    private static List<string> PickSignals(Database db, AppConfig cfg, string fromYmd, string toYmd, int max, DateTime now)
    {
        var picked = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in db.ListDeviationEvents(30))
        {
            if (!string.IsNullOrWhiteSpace(ev.TopSignal) && seen.Add(ev.TopSignal))
                picked.Add(ev.TopSignal);
            if (picked.Count >= max) return picked;
        }
        var rows = db.ListPassMeasurementPoints(fromYmd, toYmd, null, maxRows: 8000);
        var counts = rows.GroupBy(r => r.TestName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, N: g.Count()))
            .OrderByDescending(x => x.N)
            .ToList();
        foreach (var c in counts)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            var model = rows.Where(r => r.TestName == c.Name && !string.IsNullOrEmpty(r.Model))
                .GroupBy(r => r.Model).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            var row = db.GetNormalModel(NormalModelStore.SourceMeasurement, model, c.Name);
            if (row == null || NormalModelStore.EffectiveStatus(row, now, cfg.LearnNormalStaleDays) != "ready")
                continue;
            if (seen.Add(c.Name)) picked.Add(c.Name);
            if (picked.Count >= max) break;
        }
        return picked;
    }

    public static string SeriesColorHex(int index) => Palette[Math.Abs(index) % Palette.Length];
}
