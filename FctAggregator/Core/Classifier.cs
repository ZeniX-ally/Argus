namespace FctAggregator;

public static class Classifier
{
    public static bool IsDebug(string? user)
        => (user ?? "").Trim().ToLowerInvariant() == "debug";

    public static string ClassifyByPrefix(string filename, bool hasFailItems)
    {
        var prefix = filename.Length >= 2 ? filename[..2].ToUpperInvariant() : "";
        switch (prefix)
        {
            case "P_":
                return "PASS";
            case "F_":
                return "FAIL";
            case "O_":
                return hasFailItems ? "FAIL" : "INTERRUPTED";
            default:
                Logger.Warning($"未知文件名前缀 '{prefix}': {filename}");
                return "INVALID";
        }
    }
}
