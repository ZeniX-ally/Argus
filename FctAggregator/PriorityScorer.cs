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

    public static Scored Score(int failCount, int machineCount, int durationDays)
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

        string level = s >= 60 ? "high" : s >= 35 ? "medium" : "low";
        string zh = level=="high" ? "高" : level=="medium" ? "中" : "低";
        string reason = $"频次{failCount} 机台{machineCount} 持续{durationDays}天 => {s}分";
        return new Scored{ Level=level, Zh=zh, Score=s, Reason=reason };
    }

    public static string PriorityZhOf(int failCount, int machineCount=1, int durationDays=1)
        => Score(failCount, machineCount, durationDays).Zh;
}
