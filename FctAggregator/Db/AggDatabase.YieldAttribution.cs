using System.Globalization;

namespace FctAggregator;

/// <summary>
/// 规格 01 良率归因查询层（v3.21.1 P0）：按型号/治具/批次分解 fail_count + yield。
/// </summary>
public partial class AggDatabase
{
    /// <summary>
    /// 良率归因分解：返回各 model/category 维度的详细统计。
    /// 数据源：agg_records（FAIL 记录）。
    /// </summary>
    public List<YieldBreakdownItem> DecomposeByModel(string machine, DateTime startDate, DateTime endDate, int maxRows = 20)
    {
        var result = new List<YieldBreakdownItem>();
        
        try
        {
            using var conn = OpenReader();
            
            // SQL: 按 model/category 分组统计 fail_count + total + pass
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
            
            // 修复：先收集完再算贡献度（原版引用尚未赋值的 maxFailCount，恒为 0 且有除零风险）
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
    
    /// <summary>
    /// 良率归因分解：按 fixture_id 维度统计。
    /// 需要从 XML 解析中提取 FIXTURE_ID 字段（暂用 fail_reason 前缀替代）。
    /// </summary>
    public List<YieldBreakdownItem> DecomposeByFixture(string machine, DateTime startDate, DateTime endDate, int maxRows = 20)
    {
        var result = new List<YieldBreakdownItem>();
        
        try
        {
            using var conn = OpenReader();
            
            // SQL: 按 fail_reason 前缀分组（模拟 fixture_id）
            var sql = @"
                SELECT 
                    fail_reason_prefix as model,
                    'fixture' as category,
                    COUNT(*) as total,
                    0 as pass,
                    COUNT(*) as fail,
                    0 as interrupted
                FROM (
                    SELECT substr(fail_reason, 1, instr(fail_reason || ' ', ' ') - 1) as fail_reason_prefix
                    FROM agg_records
                    WHERE machine = @machine
                      AND test_date >= @startDate
                      AND test_date <= @endDate
                      AND fail_reason IS NOT NULL
                      AND fail_reason != ''
                )
                GROUP BY fail_reason_prefix
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
            
            // fixture 分解全为 FAIL 记录：贡献度 = 单项 fail / 总 fail
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
    
    /// <summary>
    /// 获取最近 X 天的总 fail_count（用于贡献度计算分母）。
    /// </summary>
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
    public string Model = "";           // 型号名称或 fixture_id
    public string Category = "";        // model 或 fixture
    public long Total;
    public long Pass;
    public long Fail;
    public long Interrupted;
    public double YieldPct;             // 良率百分比
    public double Contribution;         // 贡献度百分比（相对于最大失败项）
    public int Rank;                    // 排名 1-N
}
