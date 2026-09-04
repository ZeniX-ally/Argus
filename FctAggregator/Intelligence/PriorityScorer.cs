namespace FctAggregator;

/// <summary>域7 维修/待办智能 — 优先级评估（v3.19.0 B）：频次+机台数+持续天数加权。</summary>
public static class PriorityScorer
{
    public sealed class Scored
    {
        public string Level = ""; // high/medium/low
        public string Zh = "";
        public int Score;
        public string Reason = "";
    }

    /// <summary>
    /// 优先级评估（规格 05 S4 增加 weightFactor 校准因子）：
    /// 因子来自 LearningEngine 权重自学习（clamp [0.5, 2.0]），乘到基础分后再定级——
    /// 仅影响排序与优先级档位，不改维修状态机、不改任何数据。默认 1.0 行为与 v3.23.3 完全一致。
    /// </summary>
    public static Scored Score(int failCount, int machineCount, int durationDays, double weightFactor = 1.0)
    {
        // 基础分：fail_count
        int s = 0;
        if (failCount >= 20) s += 40;
        else if (failCount >= 10) s += 25;
        else if (failCount >= 5) s += 15;
        else s += 5;
        // 机台维度
        if (machineCount >= 5) s += 30;
        else if (machineCount >= 3) s += 20;
        else if (machineCount >= 2) s += 10;
        // 持续
        if (durationDays >= 7) s += 30;
        else if (durationDays >= 3) s += 15;
        else if (durationDays >= 1) s += 5;

        // 规格 05 S4：校准因子（越界钳制，LearningEngine.CalibrateFactor 产出时已 clamp，此处双保险）
        double wf = Math.Clamp(weightFactor, LearningEngine.FactorMin, LearningEngine.FactorMax);
        int cs = (int)Math.Round(s * wf);

        string level = cs >= 60 ? "high" : cs >= 35 ? "medium" : "low";
        string zh = level=="high" ? "高" : level=="medium" ? "中" : "低";
        string reason = wf == 1.0
            ? $"频次{failCount} 机台{machineCount} 持续{durationDays}天 => {s}分"
            : $"频次{failCount} 机台{machineCount} 持续{durationDays}天 => {s}分×{wf:F2}={cs}分(自学习校准)";
        return new Scored{ Level=level, Zh=zh, Score=cs, Reason=reason };
    }

    public static string PriorityZhOf(int failCount, int machineCount=1, int durationDays=1)
        => Score(failCount, machineCount, durationDays).Zh;
}
