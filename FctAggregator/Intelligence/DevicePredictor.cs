using System.Text.Json;

namespace FctAggregator;

public static class DevicePredictor
{
    public sealed class PredictItem
    {
        public string Machine = "";
        public string Metric = "";
        public string Level = "";
        public double Current;
        public double Predicted;
        public int? DaysToExhaust;
        public string Detail = "";
    }

    public static List<PredictItem> Predict(AggDatabase db, int windowDays = 7)
    {
        var list = new List<PredictItem>();
        List<DeviceInfoRow> infos;
        try { infos = db.ListDeviceInfos(); } catch { return list; }
        var from = DateTime.Now.AddDays(-windowDays).ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var info in infos)
        {
            try
            {
                var samples = db.QueryDeviceSamples(info.Machine, 2000, from, null);
                if (samples.Count < 3) continue;
                var cpuTrend = PredictCpu(samples, info.CpuUsage);
                if (cpuTrend != null) { cpuTrend.Machine = info.Machine; list.Add(cpuTrend); }
                var diskPred = PredictDisk(samples, info.DiskFreeGb);
                if (diskPred != null) { diskPred.Machine = info.Machine; list.Add(diskPred); }
                var offlinePred = PredictOffline(samples, info.LastSeen);
                if (offlinePred != null) { offlinePred.Machine = info.Machine; list.Add(offlinePred); }
            } catch { }
        }
        return list;
    }

    private static PredictItem? PredictCpu(List<DeviceSampleRow> samples, double currentCpu)
    {
        var n = samples.Count;
        var xs = new double[n];
        var ys = new double[n];
        var t0 = TryParseTime(samples[0].Ts);
        for (int i=0;i<n;i++)
        {
            var t = TryParseTime(samples[i].Ts);
            xs[i] = (t - t0).TotalDays;
            ys[i] = samples[i].CpuUsage;
        }
        var slope = LinearSlope(xs, ys);
        if (double.IsNaN(slope) || slope <= 0.1) return null;
        var predicted3d = currentCpu + slope * 3;
        if (predicted3d < 90) return null;
        var level = predicted3d >= 95 ? "critical" : "warn";
        return new PredictItem{ Metric="cpu", Level=level, Current=currentCpu, Predicted=Math.Round(predicted3d,1), Detail=$"CPU 趋势 +{slope:0.0}%/天，3 天后约 {predicted3d:0.0}%"};
    }

    private static PredictItem? PredictDisk(List<DeviceSampleRow> samples, double currentFree)
    {
        if (currentFree <= 0 || currentFree > 5000) return null;
        var n = samples.Count;
        var first = samples.First().DiskFreeGb;
        var last = samples.Last().DiskFreeGb;
        if (first <= 0 || last <=0) return null;
        var t0 = TryParseTime(samples.First().Ts);
        var t1 = TryParseTime(samples.Last().Ts);
        var days = (t1 - t0).TotalDays;
        if (days < 1) days = 1;
        var consumed = first - last;
        if (consumed <= 0.05) return null;
        var perDay = consumed / days;
        if (perDay <= 0) return null;
        var daysLeft = currentFree / perDay;
        if (daysLeft > 14) return null;
        var level = daysLeft <= 3 ? "critical" : "warn";
        return new PredictItem{ Metric="disk", Level=level, Current=currentFree, Predicted=Math.Round(currentFree - perDay*3,1), DaysToExhaust=(int)Math.Ceiling(daysLeft), Detail=$"磁盘日耗 {perDay:0.0}GB，约 {(int)Math.Ceiling(daysLeft)} 天耗尽"};
    }

    private static PredictItem? PredictOffline(List<DeviceSampleRow> samples, string lastSeen)
    {
        if (string.IsNullOrEmpty(lastSeen) || !DateTime.TryParse(lastSeen, out var lastDt)) return null;
        var gapSec = (DateTime.Now - lastDt).TotalSeconds;
        if (gapSec > 300) return new PredictItem{ Metric="offline", Level="critical", Current=gapSec, Predicted=gapSec, Detail=$"离线 {gapSec:0}s 未上报"};
        if (gapSec > 90) return new PredictItem{ Metric="offline", Level="warn", Current=gapSec, Predicted=gapSec, Detail=$"心跳延迟 {gapSec:0}s"};
        if (samples.Count >= 5)
        {
            var intervals = new List<double>();
            for(int i=1;i<samples.Count;i++)
            {
                var a=TryParseTime(samples[i-1].Ts); var b=TryParseTime(samples[i].Ts);
                intervals.Add((b-a).TotalSeconds);
            }
            var avg = intervals.Average();
            var std = Math.Sqrt(intervals.Average(v=> (v-avg)*(v-avg)));
            if (std > 60 && avg > 0) return new PredictItem{ Metric="offline", Level="warn", Current=std, Predicted=std, Detail=$"心跳间隔抖动 std={std:0}s"};
        }
        return null;
    }

    public static double TrendPerDay(List<DeviceSampleRow> samples, string metric)
    {
        var n = samples.Count;
        if (n < 2) return double.NaN;
        var xs = new double[n];
        var ys = new double[n];
        var t0 = TryParseTime(samples[0].Ts);
        for (int i = 0; i < n; i++)
        {
            xs[i] = (TryParseTime(samples[i].Ts) - t0).TotalDays;
            ys[i] = metric switch
            {
                "cpu" => samples[i].CpuUsage,
                "disk" => samples[i].DiskFreeGb,
                _ => double.NaN,
            };
        }
        return LinearSlope(xs, ys);
    }

    private static double LinearSlope(double[] xs, double[] ys)
    {
        int n=xs.Length; if(n<2) return double.NaN;
        double sumX=0,sumY=0,sumXY=0,sumXX=0;
        for(int i=0;i<n;i++){ sumX+=xs[i]; sumY+=ys[i]; sumXY+=xs[i]*ys[i]; sumXX+=xs[i]*xs[i];}
        double denom = n*sumXX - sumX*sumX;
        if(Math.Abs(denom)<1e-9) return double.NaN;
        return (n*sumXY - sumX*sumY)/denom;
    }
    private static DateTime TryParseTime(string s)
    {
        if(DateTime.TryParse(s, out var dt)) return dt;
        return DateTime.Now;
    }

    public static void LogPredictions(AggDatabase db, List<PredictItem> items)
    {
        foreach(var it in items)
        {
            try { db.InsertDevicePredictLog(it.Machine, it.Metric, it.Level, it.Predicted, it.DaysToExhaust, it.Detail); } catch {}
        }
    }
}
