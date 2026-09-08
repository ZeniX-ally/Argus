namespace FctAggregator;

public static class PriorityScorer
{
    public sealed class Scored
    {
        public string Level = "";
        public string Zh = "";
        public int Score;
        public string Reason = "";
    }

    public static Scored Score(int failCount, int machineCount, int durationDays, double weightFactor = 1.0)
    {
        int s = 0;
        if (failCount >= 20) s += 40;
        else if (failCount >= 10) s += 25;
        else if (failCount >= 5) s += 15;
        else s += 5;
        if (machineCount >= 5) s += 30;
        else if (machineCount >= 3) s += 20;
        else if (machineCount >= 2) s += 10;
        if (durationDays >= 7) s += 30;
        else if (durationDays >= 3) s += 15;
        else if (durationDays >= 1) s += 5;

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
