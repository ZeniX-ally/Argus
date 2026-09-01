namespace FctAggregator;

/// <summary>
/// 设备快照行（device_info，machine 主键，聚合端最新快照）。
/// 与《设备监控面板功能规划.md》§3 L1 维度一一对应，零第三方依赖采集。
/// 额外：last_seen/updated_at 在服务端落库时写入，便于离线判定与趋势展示。
/// </summary>
public sealed class DeviceInfoRow
{
    public string Machine = "";
    public string Hostname = "";
    public string Os = "";
    public string OsVersion = "";
    public string Ip = "";
    public string Mac = "";
    public string CpuModel = "";
    public int CpuCores;
    public double CpuUsage;          // 0-100 百分比，采集瞬间
    public int MemTotalMb;
    public int MemUsedMb;
    public double DiskTotalGb;
    public double DiskFreeGb;
    public long UptimeSec;
    public string ArgusVersion = "";
    public string LastSeen = "";     // yyyy-MM-dd HH:mm:ss（服务端接收时间）
    public string UpdatedAt = "";
    /// <summary>在线状态由服务端按心跳超时判定（非采集字段，前端展示用，返回时动态计算）。</summary>
    public bool Online;
}

/// <summary>设备采样点（device_samples，5 分钟一条，滚动保留 7 天）。</summary>
public sealed class DeviceSampleRow
{
    public long Id;
    public string Machine = "";
    public string Ts = "";           // yyyy-MM-dd HH:mm:ss
    public double CpuUsage;
    public int MemUsedMb;
    public double DiskFreeGb;
}

/// <summary>FCT.ini 解析快照（device_fct，machine 主键，5 分钟上报）。</summary>
public sealed class DeviceFctRow
{
    public string Machine = "";
    public string IniPath = "";
    public bool Found;
    public string? Error;
    public List<string> Models = new();
    public List<(string Label, string Version)> FwVersions = new();
    public List<FctDeviceInfo> Devices = new();
    public List<(string Label, string File)> A2lFiles = new();
    public string LastSeen = "";
    public string UpdatedAt = "";
}

/// <summary>FCT.ini 单设备（与 FctIni.DeviceInfo 同构，便于序列化）。</summary>
public sealed class FctDeviceInfo
{
    public string Name = "";
    public string Port = "";
    public string Type = "com";
    public bool Online;
}
