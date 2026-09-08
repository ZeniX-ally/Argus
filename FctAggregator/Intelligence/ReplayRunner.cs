namespace FctAggregator;

public sealed class ReplayReport
{
    public double DetectionRate { get; set; }
    public double FalsePosPerDay { get; set; }
    public double ReadyCoverage { get; set; }
    public int EventsTotal { get; set; }
    public int AnomalyDays { get; set; }
    public int ScoreDays { get; set; }
    public bool Pass => DetectionRate >= 0.80 && FalsePosPerDay <= 0.20 && ReadyCoverage >= 0.90;
}

/// <summary>
/// 历史回放：前 3 周喂学习器，后 1 周逐条评分，对照已知异常日判卷。
/// selftest 走 RunSynthetic，不依赖现场库。
/// </summary>
public static class ReplayRunner
{
    public static ReplayReport RunSynthetic(string dbPath)
    {
        var db = new Database(dbPath);
        var cfg = new AppConfig
        {
            LearnNormalEnabled = true,
            LearnNormalMinSamples = 30,
            LearnNormalStaleDays = 14,
            LearnNormalVoteSignals = 3,
            LearnNormalEventScore = 75,
            LearnNormalMaxEventsPerDay = 10,
        };
        var trainStart = new DateTime(2026, 8, 1);
        var trainEnd = trainStart.AddDays(21); // exclusive
        var scoreEnd = trainEnd.AddDays(7);
        var anomaly = new DateTime(2026, 8, 25); // within score week (Aug 22–28)

        string[] keys = { "S1", "S2", "S3", "S4", "S5" };
        double[] means = { 10, 20, 30, 40, 50 };

        for (var d = trainStart; d < trainEnd; d = d.AddDays(1))
        {
            for (int k = 0; k < 8; k++)
            {
                var rec = new TestRecord
                {
                    Model = "G49", Result = "PASS",
                    BatchTimestamp = d.AddHours(8 + k).ToString("yyyy-MM-dd HH:mm:ss"),
                };
                for (int i = 0; i < keys.Length; i++)
                    rec.Measurements.Add(new MeasurementRow { TestName = keys[i], Value = means[i] });
                NormalLearners.ObserveMeasurement(db, cfg, rec);
            }
        }

        for (var d = trainEnd; d < scoreEnd; d = d.AddDays(1))
        {
            bool bad = d.Date == anomaly.Date;
            var rec = new TestRecord
            {
                Model = "G49", Result = "PASS",
                BatchTimestamp = d.AddHours(10).ToString("yyyy-MM-dd HH:mm:ss"),
            };
            var samples = new List<(string, double)>();
            for (int i = 0; i < keys.Length; i++)
            {
                var v = bad ? means[i] + 6.0 : means[i];
                rec.Measurements.Add(new MeasurementRow { TestName = keys[i], Value = v });
                samples.Add((keys[i], v));
            }
            DeviationScorer.EvaluateWindow(db, cfg, NormalModelStore.SourceMeasurement, "G49", rec.BatchTimestamp, samples, d.AddHours(10));
        }

        var anomalyDates = new HashSet<string> { anomaly.ToString("yyyy-MM-dd") };
        int hitDays = 0, fp = 0, scoreDays = 0;
        for (var d = trainEnd; d < scoreEnd; d = d.AddDays(1))
        {
            scoreDays++;
            var n = db.CountDeviationEventsOnDay(d.ToString("yyyy-MM-dd"));
            if (anomalyDates.Contains(d.ToString("yyyy-MM-dd")))
            {
                if (n > 0) hitDays++;
            }
            else fp += n;
        }
        var cov = db.CountNormalModels(scoreEnd, cfg.LearnNormalStaleDays);
        double readyPct = cov.Total == 0 ? 0 : (double)cov.Ready / cov.Total;
        int events = 0;
        for (var d = trainEnd; d < scoreEnd; d = d.AddDays(1))
            events += db.CountDeviationEventsOnDay(d.ToString("yyyy-MM-dd"));

        return new ReplayReport
        {
            DetectionRate = anomalyDates.Count == 0 ? 0 : (double)hitDays / anomalyDates.Count,
            FalsePosPerDay = scoreDays <= 1 ? fp : (double)fp / (scoreDays - anomalyDates.Count),
            ReadyCoverage = readyPct,
            EventsTotal = events,
            AnomalyDays = anomalyDates.Count,
            ScoreDays = scoreDays,
        };
    }

    public static ReplayReport Run(Database db, AppConfig cfg, DateTime trainEnd, DateTime scoreFrom, DateTime scoreTo, IReadOnlyCollection<string> anomalyDates)
    {
        var anomalies = new HashSet<string>(anomalyDates ?? Array.Empty<string>(), StringComparer.Ordinal);
        int hitDays = 0, fp = 0, scoreDays = 0;
        for (var d = scoreFrom.Date; d <= scoreTo.Date; d = d.AddDays(1))
        {
            scoreDays++;
            var n = db.CountDeviationEventsOnDay(d.ToString("yyyy-MM-dd"));
            var key = d.ToString("yyyy-MM-dd");
            if (anomalies.Contains(key)) { if (n > 0) hitDays++; }
            else fp += n;
        }
        var cov = db.CountNormalModels(scoreTo, cfg.LearnNormalStaleDays);
        int normalDays = Math.Max(1, scoreDays - anomalies.Count);
        return new ReplayReport
        {
            DetectionRate = anomalies.Count == 0 ? 0 : (double)hitDays / anomalies.Count,
            FalsePosPerDay = (double)fp / normalDays,
            ReadyCoverage = cov.Total == 0 ? 0 : (double)cov.Ready / cov.Total,
            EventsTotal = db.ListDeviationEvents(1000).Count,
            AnomalyDays = anomalies.Count,
            ScoreDays = scoreDays,
        };
    }
}
