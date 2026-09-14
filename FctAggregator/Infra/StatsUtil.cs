namespace FctAggregator;

/// <summary>
/// 统计口径唯一入口（口径单一来源）。
///
/// **良率有两套历史实现，浮点结果不同，本类只收敛实现、不统一运算顺序。**
/// 两者在 IEEE754 下结果可能不同，而且**差异是可见的**：
/// `Pass=23 / Fail=57`（Total=80）时，先乘后除得 `28.75`、先除后乘得 `28.749999999999996`，
/// 按一位小数显示分别是 `28.8%` 与 `28.7%`。
/// 统一运算顺序会改变现场良率显示值，属产品决策，见 `docs/Argus-精简审计报告-20260914.md` §2.3。
/// </summary>
public static class StatsUtil
{
    /// <summary>良率%：**先乘后除** `Pass * 100.0 / Total`。
    /// 原 AppState.YieldRate / AppState.TodayYield / DebugPanel 今日与当月 四处实现。</summary>
    public static double YieldPct(int pass, int fail)
    {
        var total = pass + fail;
        return total > 0 ? pass * 100.0 / total : 0.0;
    }

    /// <summary>良率%：**先除后乘** `(double)Pass / Total * 100.0`。
    /// 原 HourlyStatItem.YieldRate / SelfBaseline ×2 / FailRanker.CsvExporter 四处实现。</summary>
    public static double YieldPctDivFirst(int pass, int fail)
    {
        var total = pass + fail;
        return total > 0 ? (double)pass / total * 100.0 : 0.0;
    }
}
