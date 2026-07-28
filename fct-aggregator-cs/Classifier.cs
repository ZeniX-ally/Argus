namespace FctAggregator;

/// <summary>
/// 分类逻辑: 产品结果看文件名前缀 P_/F_/O_; debug 看 USER 字段。
/// </summary>
public static class Classifier
{
    /// <summary>USER 属性值 == debug(忽略大小写) 即为 debug 数据。</summary>
    public static bool IsDebug(string? user)
        => (user ?? "").Trim().ToLowerInvariant() == "debug";

    /// <summary>
    /// 按文件名前缀 + fail 项判定结果。
    /// P_ -> PASS; F_ -> FAIL; O_ -> 有真fail项则FAIL, 否则INTERRUPTED; 其它 -> INVALID。
    /// </summary>
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
