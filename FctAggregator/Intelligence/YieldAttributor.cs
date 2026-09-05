namespace FctAggregator;

public static class YieldAttributor
{
    public static List<YieldBreakdownItem> AnalyzeModel(AggDatabase db, string machine, int daysBack = 7)
    {
        var startDate = DateTime.Today.AddDays(-daysBack);
        var endDate = DateTime.Today;

        if (db.GetRecentFailCount(machine, daysBack) == 0)
            return new List<YieldBreakdownItem>();

        return db.DecomposeByModel(machine, startDate, endDate, maxRows: 20);
    }

    public static List<YieldBreakdownItem> AnalyzeFixture(AggDatabase db, string machine, int daysBack = 7)
    {
        var startDate = DateTime.Today.AddDays(-daysBack);
        var endDate = DateTime.Today;

        if (db.GetRecentFailCount(machine, daysBack) == 0)
            return new List<YieldBreakdownItem>();

        return db.DecomposeByFixture(machine, startDate, endDate, maxRows: 20);
    }
}
