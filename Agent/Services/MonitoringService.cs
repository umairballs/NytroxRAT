using System.Diagnostics;
using System.Runtime.InteropServices;
using NytroxRAT.Shared.Models;

namespace NytroxRAT.Agent.Services;

public class MonitoringService : IDisposable
{
    private readonly PerformanceCounter _cpuCounter;
    private bool _disposed;

    public MonitoringService()
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
        _cpuCounter.NextValue(); // first call always returns 0
    }

    public SystemStatsPayload GetSystemStats()
    {
        GetMemoryStatus(out var memStatus);
        var totalMem = (long)memStatus.ullTotalPhys;
        var freeMem  = (long)memStatus.ullAvailPhys;

        return new SystemStatsPayload
        {
            CpuPercent       = Math.Round(_cpuCounter.NextValue(), 1),
            MemoryTotalBytes = totalMem,
            MemoryUsedBytes  = totalMem - freeMem,
            Hostname         = Environment.MachineName,
            OsVersion        = Environment.OSVersion.VersionString,
            Uptime           = TimeSpan.FromMilliseconds(Environment.TickCount64),
            Disks            = GetDiskInfo()
        };
    }

    public List<ProcessEntry> GetProcessList()
    {
        return Process.GetProcesses()
            .OrderByDescending(p =>
            {
                try { return p.WorkingSet64; } catch { return 0; }
            })
            .Take(50)
            .Select(p =>
            {
                try
                {
                    return new ProcessEntry
                    {
                        Pid         = p.Id,
                        Name        = p.ProcessName,
                        MemoryBytes = p.WorkingSet64
                    };
                }
                catch { return null; }
            })
            .Where(e => e != null)
            .Select(e => e!)
            .ToList();
    }

    private static List<DiskInfo> GetDiskInfo() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new DiskInfo
            {
                Name       = d.Name,
                TotalBytes = d.TotalSize,
                FreeBytes  = d.AvailableFreeSpace
            }).ToList();

    // ── Win32 ──────────────────────────────────────────────────────────────
    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static void GetMemoryStatus(out MEMORYSTATUSEX status)
    {
        status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile;
        public ulong ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cpuCounter.Dispose();
        _disposed = true;
    }
}
