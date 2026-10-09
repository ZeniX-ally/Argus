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

    public static int Pass, Fail, Interrupted, Invalid, ParseError, ProductCount;
    public static int TodayPass, TodayFail, TodayInterrupted, TodayProductCount;
    public static string TodayModelLine = "";
    public static string TodayTaktLine = "今日节拍  —    没有总时长";
    public static List<HourlyStatItem> Hourly = new();
    public static List<TopFailItem> TopFails = new();
    public static List<LiveFailAlert> Alerts = new();

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
        => Refresh(db, stationId);

    /// <summary>轻量刷新：只重算数据库统计，供 UI 周期调用保证看板实时（跨日归零、无入库时也对齐库内最新值）。</summary>
    public static void RefreshDbStats(Database db, string stationId)
        => Refresh(db, stationId);

    private static void Refresh(Database db, string stationId)
    {
        // 审计：下面三条是 KPI 主查询，原先没有 try——库被占用/损坏时异常会穿透 Engine.Start()，
        // 启动流程直接中断。与后面三条一样做降级兜底（保留上一轮的值）。
        StatsData g;
        StatsData d;
        List<ModelDayStat> byModel;
        try { g = db.FetchGlobalStats(stationId); }
        catch (Exception ex) { Logger.Warning($"[统计] 全局统计查询失败: {ex.Message}"); return; }
        var today = DateTime.Now.ToString("yyyyMMdd");
        try { d = db.FetchDailyStats(stationId, today); }
        catch (Exception ex) { Logger.Warning($"[统计] 当日统计查询失败: {ex.Message}"); return; }
        try { byModel = db.FetchDailyStatsByModel(stationId, today); }
        catch (Exception ex) { Logger.Warning($"[统计] 分型号统计查询失败: {ex.Message}"); byModel = new(); }
        var modelLine = FormatModelLine(byModel);
        List<HourlyStatItem> hourly;
        List<TopFailItem> topFails;
        List<LiveFailAlert> alerts;
        try { hourly = db.FetchDailyHourlyStats(stationId, today); }
        catch (Exception ex) { Logger.Warning($"[统计] 小时图查询失败: {ex.Message}"); hourly = new(); }
        try { topFails = db.FetchDailyTopFails(stationId, today, 5); }
        catch (Exception ex) { Logger.Warning($"[统计] Top5 查询失败: {ex.Message}"); topFails = new(); }
        try { alerts = db.FetchRecentFailAlerts(stationId, 10, today); }
        catch (Exception ex) { Logger.Warning($"[统计] 告警流查询失败: {ex.Message}"); alerts = new(); }
        string taktLine = TodayTaktLine;
        try
        {
            taktLine = CycleTakt.FormatLine(CycleTakt.Compute(db.ListTodayCycles(stationId, today)));
        }
        catch (Exception ex) { Logger.Warning($"[统计] 节拍查询失败: {ex.Message}"); }
        if (hourly.Sum(x => x.Total) == 0 && d.TodayProductCount > 0)
            Logger.Warning($"[统计] KPI 有 {d.TodayProductCount} 台但看板小时图为 0（Top5={topFails.Count} 告警={alerts.Count}）");
        lock (_lock)
        {
            Pass = g.Pass; Fail = g.Fail; Interrupted = g.Interrupted;
            Invalid = g.Invalid; ProductCount = g.ProductCount;
            TodayPass = d.Pass; TodayFail = d.Fail; TodayInterrupted = d.Interrupted;
            TodayProductCount = d.TodayProductCount;
            TodayModelLine = modelLine;
            TodayTaktLine = taktLine;
            Hourly = hourly;
            TopFails = topFails;
            Alerts = alerts;
        }
    }

    private static string FormatModelLine(List<ModelDayStat> rows)
    {
        if (rows == null || rows.Count == 0) return "";
        return string.Join(" · ", rows.Select(r =>
        {
            var name = r.Model;
            if (name.Length == 8 && name.StartsWith("E300", StringComparison.OrdinalIgnoreCase))
                name = name[4..];
            return $"{name} {r.Pass}P/{r.Fail}F/{r.Total}";
        }));
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
                ParseError = ParseError, ProductCount = ProductCount,
                TodayPass = TodayPass, TodayFail = TodayFail, TodayInterrupted = TodayInterrupted,
                TodayProductCount = TodayProductCount,
                TodayModelLine = TodayModelLine,
                TodayTaktLine = TodayTaktLine,
                Hourly = new List<HourlyStatItem>(Hourly),
                TopFails = new List<TopFailItem>(TopFails),
                Alerts = new List<LiveFailAlert>(Alerts),
            };
        }
    }
}

public class StatsSnapshot
{
    public string StationId = "", Status = "";
    public int ModelsCount;
    public bool WebhookConfigured, HistoricalScanComplete;
    public int Pass, Fail, Interrupted, Invalid, ParseError, ProductCount;
    public int TodayPass, TodayFail, TodayInterrupted, TodayProductCount;
    public string TodayModelLine = "";
    public string TodayTaktLine = "";
    public List<HourlyStatItem> Hourly = new();
    public List<TopFailItem> TopFails = new();
    public List<LiveFailAlert> Alerts = new();
    public double YieldRate => StatsUtil.YieldPct(Pass, Fail);
    public double TodayYield => StatsUtil.YieldPct(TodayPass, TodayFail);
}
