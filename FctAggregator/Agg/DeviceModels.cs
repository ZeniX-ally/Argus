namespace FctAggregator;

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
    public double CpuUsage;
    public int MemTotalMb;
    public int MemUsedMb;
    public double DiskTotalGb;
    public double DiskFreeGb;
    public long UptimeSec;
    public string ArgusVersion = "";
    public string LastSeen = "";
    public string UpdatedAt = "";
    public bool Online;
}

public sealed class DeviceSampleRow
{
    public long Id;
    public string Machine = "";
    public string Ts = "";
    public double CpuUsage;
    public int MemUsedMb;
    public double DiskFreeGb;
}

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

public sealed class FctDeviceInfo
{
    public string Name = "";
    public string Port = "";
    public string Type = "com";
    public bool Online;
}
