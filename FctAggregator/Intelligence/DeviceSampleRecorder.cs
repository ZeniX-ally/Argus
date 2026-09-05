using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FctAggregator;

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

        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(2000);
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
