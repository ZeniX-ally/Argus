namespace FctAggregator;

/// <summary>
/// 规格 01 良率归因分析器（v3.21.1 P0）：回答"为什么良率跌"。
/// 核心算法：按 model/category 分组 fail_count + yield%，排序 top N。
/// </summary>
public static class YieldAttributor
{
    /// <summary>
    /// 主分析入口：返回最近 X 天的型号维度归因结果。
    /// </summary>
    public static List<YieldBreakdownItem> AnalyzeModel(AggDatabase db, string machine, int daysBack = 7)
    {
        var startDate = DateTime.Today.AddDays(-daysBack);
        var endDate = DateTime.Today;

        // 无失败记录时提前返回，避免空结果查询
        if (db.GetRecentFailCount(machine, daysBack) == 0)
            return new List<YieldBreakdownItem>();

        // 调用数据库查询层
        return db.DecomposeByModel(machine, startDate, endDate, maxRows: 20);
    }

    /// <summary>
    /// fixture 维度归因（扩展功能）。
    /// </summary>
    public static List<YieldBreakdownItem> AnalyzeFixture(AggDatabase db, string machine, int daysBack = 7)
    {
        var startDate = DateTime.Today.AddDays(-daysBack);
        var endDate = DateTime.Today;

        if (db.GetRecentFailCount(machine, daysBack) == 0)
            return new List<YieldBreakdownItem>();

        return db.DecomposeByFixture(machine, startDate, endDate, maxRows: 20);
    }
}
