namespace FctAggregator;

/// <summary>
/// 字节数格式化唯一入口（口径单一来源）。
///
/// **注意：本类只做实现收敛，不统一进制** —— 项目历史上存在两套进制，统一会改变现场显示值：
/// - <see cref="MbGb"/>：十进制（1000 进制），只有 GB/MB 两档；
/// - <see cref="Human"/>：二进制（1024 进制），B/KB/MB/GB 四档。
///
/// 同一个 2,147,483,648 字节的库，前者显示 `2.15 GB`、后者显示 `2.00 GB`（差 7%）。
/// 是否统一进制属产品决策，见 `docs/Argus-精简审计报告-20260914.md` §2.1。
/// </summary>
public static class ByteUtil
{
    /// <summary>十进制 GB/MB（原 FctProgramBackup / Database.Storage / StorageOptimizer ×2 / BackupCodePanel 五处同款实现）。
    /// 注意：小于 1MB 的值也按 MB 显示（如 `0.0 MB`），这是原有行为，不要"顺手修"。</summary>
    public static string MbGb(long b) =>
        b >= 1_000_000_000 ? $"{b / 1_000_000_000.0:F2} GB" : $"{b / 1_000_000.0:F1} MB";

    /// <summary>二进制 B/KB/MB/GB（原 FeishuNotifier / Fetcher.Packager 两处实现）。
    /// <paramref name="kbDecimals"/> 保留两处历史差异：飞书卡 KB 用 1 位小数，工具箱用 0 位。</summary>
    public static string Human(long b, int kbDecimals = 1)
    {
        if (b < 1024) return $"{b} B";
        // 小数位数是变量，不能写在插值格式说明符里（嵌套大括号非法），单独拼格式串
        if (b < 1024L * 1024) return (b / 1024.0).ToString("F" + Math.Max(0, kbDecimals)) + " KB";
        if (b < 1024L * 1024 * 1024) return $"{b / 1024.0 / 1024:F1} MB";
        return $"{b / 1024.0 / 1024 / 1024:F2} GB";
    }
}
