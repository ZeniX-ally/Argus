using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FctAggregator;

/// <summary>
/// 机台端设备资源自采样记录器 (规格 05 S2: 轻量后台采样，零额外 NuGet 依赖)。
/// 采样指标: 系统 CPU 使用率 (GetSystemTimes P/Invoke), 系统物理内存使用率 (GlobalMemoryStatusEx P/Invoke), 磁盘剩余空间 (DriveInfo)。
/// 采样周期 5min (默认), 写入本地 SQLite 表 device_samples_local, 每日维护保留 14 天。
/// </summary>
public sealed class DeviceSampleRecorder : IDisposable
{
    private static DeviceSampleRecorder? _instance;
    private static readonly object _initLock = new();

    public static DeviceSampleRecorder Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_initLock)
                {
                    _instance ??= new DeviceSampleRecorder();
                }
            }
            return _instance;
        }
    }

    private readonly AppConfig _cfg;
    private Thread? _worker;
    private volatile bool _stopping;
    private readonly object _cpuLock = new();
    private long _lastIdleTime;
    private long _lastKernelTime;
    private long _lastUserTime;

    public DeviceSampleRecorder(AppConfig? cfg = null)
    {
        _cfg = cfg ?? AppConfig.Instance;
    }

    public void Start()
    {
        if (!_cfg.LearnResourceSamplingEnabled)
        {
            Logger.Info("[自采样] learn_resource_sampling_enabled 为 false，机台自采样不启动");
            return;
        }

        if (_worker != null && _worker.IsAlive) return;

        _stopping = false;
        _worker = new Thread(Loop) { IsBackground = true, Name = "device-sample-recorder" };
        _worker.Start();
        Logger.Info("[自采样] 本机资源定时自采样已启动 (周期 300s, 表 device_samples_local)");

        // 异步采集第一针，避免启动后 5 分钟库内无新记录
        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(2000); // 稍微停顿待系统平稳
                RecordOnce();
            }
            catch (Exception ex)
            {
                Logger.Warning($"[自采样] 初始采样异常: {ex.Message}");
            }
        });
    }

    public void Stop()
    {
        _stopping = true;
        try { _worker?.Join(2000); } catch { }
        _worker = null;
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        while (!_stopping)
        {
            // 睡眠 300 秒 (5 分钟)，允许优雅快速退出
            for (int i = 0; i < 300 && !_stopping; i++)
            {
                Thread.Sleep(1000);
            }
            if (_stopping) break;

            try
            {
                RecordOnce();
            }
            catch (Exception ex)
            {
                Logger.Warning($"[自采样] 周期采样异常: {ex.Message}");
            }
        }
    }

    /// <summary>执行单次采样并写入本地库。</summary>
    public (double Cpu, double MemPct, double DiskFreeGb)? RecordOnce()
    {
        try
        {
            var cpu = SampleSystemCpu();
            var memPct = SampleSystemMemoryPercent();
            var diskFree = SampleDiskFreeGb();

            var db = Database.Current;
            if (db != null)
            {
                db.InsertLocalDeviceSample(cpu, memPct, diskFree);
            }

            return (cpu, memPct, diskFree);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[自采样] 写入本地库失败: {ex.Message}");
            return null;
        }
    }

    // ==================== P/Invoke 系统级采样 (零 NuGet 第三方依赖) ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
        public long ToLong() => ((long)dwHighDateTime << 32) | dwLowDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>采集系统整体 CPU 使用率 (0.0~100.0%)。</summary>
    public double SampleSystemCpu()
    {
        if (!OperatingSystem.IsWindows()) return 0.0;
        lock (_cpuLock)
        {
            try
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0.0;
                long idleTime = idle.ToLong();
                long kernelTime = kernel.ToLong();
                long userTime = user.ToLong();

                if (_lastKernelTime == 0 && _lastUserTime == 0)
                {
                    _lastIdleTime = idleTime;
                    _lastKernelTime = kernelTime;
                    _lastUserTime = userTime;
                    return 0.0;
                }

                long usrDiff = userTime - _lastUserTime;
                long kerDiff = kernelTime - _lastKernelTime;
                long idlDiff = idleTime - _lastIdleTime;

                long sysTotal = usrDiff + kerDiff;
                if (sysTotal <= 0) return 0.0;

                // kernelTime 已包含 idleTime: total = (sysTotal - idlDiff) / sysTotal
                double cpu = (double)(sysTotal - idlDiff) * 100.0 / sysTotal;

                _lastIdleTime = idleTime;
                _lastKernelTime = kernelTime;
                _lastUserTime = userTime;

                if (double.IsNaN(cpu) || double.IsInfinity(cpu)) return 0.0;
                return Math.Clamp(Math.Round(cpu, 1), 0.0, 100.0);
            }
            catch
            {
                return 0.0;
            }
        }
    }

    /// <summary>采集系统物理内存使用百分比 (0.0~100.0%)。</summary>
    public static double SampleSystemMemoryPercent()
    {
        if (!OperatingSystem.IsWindows()) return 0.0;
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref mem))
            {
                return Math.Clamp((double)mem.dwMemoryLoad, 0.0, 100.0);
            }
        }
        catch { }

        // 回落到 GC/Process
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            if (gcInfo.TotalAvailableMemoryBytes > 0)
            {
                double used = GC.GetTotalMemory(false);
                return Math.Clamp(Math.Round(used * 100.0 / gcInfo.TotalAvailableMemoryBytes, 1), 0.0, 100.0);
            }
        }
        catch { }

        return 0.0;
    }

    /// <summary>采集安装目录所在磁盘的剩余空间 (GB)。</summary>
    public static double SampleDiskFreeGb()
    {
        try
        {
            var root = Path.GetPathRoot(AppConfig.BaseDir) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed) ?? drive;
            }
            if (!drive.IsReady) return 0.0;
            return Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0, 2);
        }
        catch
        {
            return 0.0;
        }
    }
}
