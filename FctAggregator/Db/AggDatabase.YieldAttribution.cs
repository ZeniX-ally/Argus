using System.Globalization;

namespace FctAggregator;

public partial class AggDatabase
{
    public List<YieldBreakdownItem> DecomposeByModel(string machine, DateTime startDate, DateTime endDate, int maxRows = 20)
    {
        var result = new List<YieldBreakdownItem>();

        try
        {
            using var conn = OpenReader();

            var sql = @"
                SELECT
                    model,
                    category,
                    COUNT(*) as total,
                    SUM(CASE WHEN result='PASS' THEN 1 ELSE 0 END) as pass,
                    SUM(CASE WHEN result='FAIL' THEN 1 ELSE 0 END) as fail,
                    SUM(CASE WHEN result='INTERRUPTED' THEN 1 ELSE 0 END) as interrupted
                FROM agg_records
                WHERE machine = @machine
                  AND test_date >= @startDate
                  AND test_date <= @endDate
                  AND result IN ('PASS', 'FAIL', 'INTERRUPTED')
                GROUP BY model, category
                ORDER BY fail DESC
                LIMIT @maxRows";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@machine", machine);
            cmd.Parameters.AddWithValue("@startDate", startDate.ToString("yyyyMMdd"));
            cmd.Parameters.AddWithValue("@endDate", endDate.ToString("yyyyMMdd"));
            cmd.Parameters.AddWithValue("@maxRows", maxRows);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var model = reader.GetString(0);
                var category = reader.GetString(1);
                var total = reader.GetInt64(2);
                var pass = reader.GetInt64(3);
                var fail = reader.GetInt64(4);
                var interrupted = reader.GetInt64(5);

                if (total == 0) continue;

                var yield = Math.Round((double)pass / total * 100, 2);

                result.Add(new YieldBreakdownItem
                {
                    Model = model,
                    Category = category,
                    Total = total,
                    Pass = pass,
                    Fail = fail,
                    Interrupted = interrupted,
                    YieldPct = yield,
                    Rank = result.Count + 1
                });
            }

            var maxFail = result.Count > 0 ? result.Max(x => x.Fail) : 0;
            if (maxFail > 0)
                foreach (var it in result)
                    it.Contribution = Math.Round((double)it.Fail / maxFail * 100, 1);
        }
        catch (Exception ex)
        {
            Logger.Error($"[YieldAttribution] DecomposeByModel failed: {ex.Message}");
        }

        return result;
    }

    public List<YieldBreakdownItem> DecomposeByFixture(string machine, DateTime startDate, DateTime endDate, int maxRows = 20)
    {
        var result = new List<YieldBreakdownItem>();

        try
        {
            using var conn = OpenReader();

            var sql = @"
                SELECT
                    fkey as model,
                    'fixture' as category,
                    COUNT(*) as total,
                    0 as pass,
                    COUNT(*) as fail,
                    0 as interrupted
                FROM (
                    SELECT COALESCE(NULLIF(TRIM(fixture_id), ''),
                            substr(fail_reason, 1, instr(fail_reason || ' ', ' ') - 1)) as fkey
                    FROM agg_records
                    WHERE machine = @machine
                      AND test_date >= @startDate
                      AND test_date <= @endDate
                      AND fail_reason IS NOT NULL
                      AND fail_reason != ''
                )
                GROUP BY fkey
                ORDER BY fail DESC
                LIMIT @maxRows";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@machine", machine);
            cmd.Parameters.AddWithValue("@startDate", startDate.ToString("yyyyMMdd"));
            cmd.Parameters.AddWithValue("@endDate", endDate.ToString("yyyyMMdd"));
            cmd.Parameters.AddWithValue("@maxRows", maxRows);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var fixtureName = reader.GetString(0);
                var total = reader.GetInt64(2);
                var fail = reader.GetInt64(4);

                if (total == 0) continue;

                result.Add(new YieldBreakdownItem
                {
                    Model = fixtureName,
                    Category = "fixture",
                    Total = total,
                    Pass = 0,
                    Fail = fail,
                    Interrupted = 0,
                    Rank = result.Count + 1
                });
            }

            var totalFail = result.Sum(x => x.Fail);
            if (totalFail > 0)
                foreach (var it in result)
                    it.Contribution = Math.Round((double)it.Fail / totalFail * 100, 1);
        }
        catch (Exception ex)
        {
            Logger.Error($"[YieldAttribution] DecomposeByFixture failed: {ex.Message}");
        }

        return result;
    }

    public long GetRecentFailCount(string machine, int daysBack = 7)
    {
        var count = 0L;
        var startDate = DateTime.Today.AddDays(-daysBack);
        var endDate = DateTime.Today;

        try
        {
            using var conn = OpenReader();

            var sql = @"SELECT COUNT(*) FROM agg_records
                       WHERE machine = @machine
                         AND test_date >= @startDate
                         AND test_date <= @endDate
                         AND result='FAIL'";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@machine", machine);
            cmd.Parameters.AddWithValue("@startDate", startDate.ToString("yyyyMMdd"));
            cmd.Parameters.AddWithValue("@endDate", endDate.ToString("yyyyMMdd"));

            var result = cmd.ExecuteScalar();
            if (result != null && long.TryParse(result.ToString(), out var parsed))
                count = parsed;
        }
        catch (Exception ex)
        {
            Logger.Error($"[YieldAttribution] GetRecentFailCount failed: {ex.Message}");
        }

        return count;
    }

}

public class YieldBreakdownItem
{
    public string Model = "";
    public string Category = "";
    public long Total;
    public long Pass;
    public long Fail;
    public long Interrupted;
    public double YieldPct;
    public double Contribution;
    public int Rank;
}
