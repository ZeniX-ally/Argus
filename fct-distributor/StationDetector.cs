using System.Text.RegularExpressions;

namespace FctDistributor;

/// <summary>
/// 机台号（IP 映射）+ 型号识别 —— 分发器自己的一份。
///
/// ⚠ <b>这是副本，不是共享引用。</b>
/// v2.9.3 曾把分发器这份删掉、统一调 <c>FctAggregator.StationDetector</c>；
/// v3.0.0 分发器彻底独立后不能再依赖主程序，于是又拆回两份 —— 这是「彻底分家」的既定代价。
///
/// 只保留分发器真正用到的东西：IP→机台号 与 型号格式校验。
/// 主程序那份还有 <c>ExtractStationFromTester</c> 与整个 <c>AutoStart</c>（开机自启），
/// 分发器不需要，故不抄。
///
/// ⚠ <b>IP 映射表是两份，改一处必须改另一处</b>：
///   本文件  fct-distributor/StationDetector.cs
///   主程序  fct-aggregator-cs/StationDetector.cs
/// 分发器自检里有一条**跨工程一致性断言**：会去读主程序那份源码，
/// 逐条比对 IP 表；对不上就报红（源码不在时跳过，不影响独立部署）。
/// </summary>
public static class StationDetector
{
    /// <summary>IP → 机台号。与主程序那份必须逐条一致（自检会比对）。</summary>
    internal static readonly Dictionary<string, string> IpToStation = new()
    {
        ["172.28.55.11"] = "FCT1",
        ["172.28.55.12"] = "FCT2",
        ["172.28.55.13"] = "FCT3",
        ["172.28.55.14"] = "FCT4",
        ["172.28.55.15"] = "FCT5",
        ["172.28.55.16"] = "FCT6",
        ["172.28.55.18"] = "FCT7",
    };

    private static readonly Regex ModelRe = new(@"^E\d{7}$", RegexOptions.Compiled);

    /// <summary>型号是否合法（E + 7 位数字）。</summary>
    public static bool IsValidModel(string name) => ModelRe.IsMatch(name);

    /// <summary>按本机 IP 猜机台号；猜不到返回 null。</summary>
    public static string? DetectStation()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address.ToString();
                    if (IpToStation.TryGetValue(ip, out var st))
                        return st;
                }
            }
        }
        catch { }
        return null;
    }
}
