namespace FctAggregator;

public static class AppState
{
    private static readonly object _lock = new();

    public static string StationId = "";
    public static string Status = "idle";
    public static int ModelsCount = 0;
    public static bool WebhookConfigured = false;
    public static bool HistoricalScanComplete = false;

    public static string ScanPhase = "idle";
    public static int ScanTotal = 0;
    public static int ScanParsed = 0;

    public static int Pass, Fail, Interrupted, Invalid, ParseError, ProductCount, XmlCount;
    public static int TodayPass, TodayFail, TodayInterrupted, TodayProductCount;
    public static int MonthPass, MonthFail, MonthInterrupted, MonthProductCount;

    public static void SetStatus(string s) { lock (_lock) Status = s; }

    public static void SetScanProgress(string? phase = null, int? total = null, int? parsed = null)
    {
        lock (_lock)
        {
            if (phase != null) ScanPhase = phase;
            if (total != null) ScanTotal = total.Value;
            if (parsed != null) ScanParsed = parsed.Value;
        }
    }

    public static (string phase, int total, int parsed) GetScanProgress()
    {
        lock (_lock) return (ScanPhase, ScanTotal, ScanParsed);
    }

    public static void IncParseError() { lock (_lock) ParseError++; }

    public static void RefreshStats(Database db, string stationId, string resultsRoot)
        => Refresh(db, stationId, resultsRoot, true);

    /// <summary>轻量刷新：只重算数据库统计（不含结果目录 XML 全盘扫描），供 UI 周期调用保证看板实时（跨日归零、无入库时也对齐库内最新值）。</summary>
    public static void RefreshDbStats(Database db, string stationId)
        => Refresh(db, stationId, null, false);

    private static void Refresh(Database db, string stationId, string? resultsRoot, bool includeDiskScan)
    {
        var g = db.FetchGlobalStats(stationId);
        var today = DateTime.Now.ToString("yyyyMMdd");
        var d = db.FetchDailyStats(stationId, today);
        var month = db.FetchMonthlyStats(stationId, DateTime.Now.ToString("yyyyMM"));
        var xmlOnDisk = includeDiskScan && resultsRoot != null ? CountDiskXml(resultsRoot) : XmlCount;
        lock (_lock)
        {
            Pass = g.Pass; Fail = g.Fail; Interrupted = g.Interrupted;
            Invalid = g.Invalid; ProductCount = g.ProductCount;
            XmlCount = xmlOnDisk;
            TodayPass = d.Pass; TodayFail = d.Fail; TodayInterrupted = d.Interrupted;
            TodayProductCount = d.TodayProductCount;
            MonthPass = month.Pass; MonthFail = month.Fail; MonthInterrupted = month.Interrupted;
            MonthProductCount = month.TodayProductCount;
        }
    }

    public static int CountDiskXml(string resultsRoot)
    {
        try
        {
            if (!Directory.Exists(resultsRoot)) return 0;
            return Directory.EnumerateFiles(resultsRoot, "*.xml", SearchOption.AllDirectories).Count();
        }
        catch { return 0; }
    }

    public static StatsSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new StatsSnapshot
            {
                StationId = StationId, Status = Status, ModelsCount = ModelsCount,
                WebhookConfigured = WebhookConfigured, HistoricalScanComplete = HistoricalScanComplete,
                Pass = Pass, Fail = Fail, Interrupted = Interrupted, Invalid = Invalid,
                ParseError = ParseError, ProductCount = ProductCount, XmlCount = XmlCount,
                TodayPass = TodayPass, TodayFail = TodayFail, TodayInterrupted = TodayInterrupted,
                TodayProductCount = TodayProductCount,
                MonthPass = MonthPass, MonthFail = MonthFail, MonthInterrupted = MonthInterrupted,
                MonthProductCount = MonthProductCount,
            };
        }
    }
}

public class StatsSnapshot
{
    public string StationId = "", Status = "";
    public int ModelsCount;
    public bool WebhookConfigured, HistoricalScanComplete;
    public int Pass, Fail, Interrupted, Invalid, ParseError, ProductCount, XmlCount;
    public int TodayPass, TodayFail, TodayInterrupted, TodayProductCount;
    public int MonthPass, MonthFail, MonthInterrupted, MonthProductCount;
    public double YieldRate => Pass + Fail > 0 ? Pass * 100.0 / (Pass + Fail) : 0.0;
    public double TodayYield => TodayPass + TodayFail > 0 ? TodayPass * 100.0 / (TodayPass + TodayFail) : 0.0;
    public double MonthYield => MonthPass + MonthFail > 0 ? MonthPass * 100.0 / (MonthPass + MonthFail) : 0.0;
}
