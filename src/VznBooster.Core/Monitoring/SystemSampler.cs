using System.Runtime.InteropServices;

namespace VznBooster.Core.Monitoring;

public sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);

public sealed record MemorySnapshot(long TotalBytes, long AvailableBytes, long? CommitLimitBytes = null, long? CommitAvailableBytes = null)
{
    public long? CommitUsedBytes => CommitLimitBytes is { } l && CommitAvailableBytes is { } a ? l - a : null;
    public long UsedBytes => TotalBytes - AvailableBytes;
    public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;
}

public sealed record DriveSnapshot(string Name, long TotalBytes, long FreeBytes)
{
    public double FreePercent => TotalBytes > 0 ? FreeBytes * 100.0 / TotalBytes : 0;
    public double UsedPercent => TotalBytes > 0 ? (TotalBytes - FreeBytes) * 100.0 / TotalBytes : 0;
}

public interface ISystemSampler
{
    CpuTimes? ReadCpuTimes();
    MemorySnapshot? ReadMemory();
    IReadOnlyList<DriveSnapshot> ReadDrives();

    /// <summary>Tempos acumulados por núcleo lógico (lista vazia se indisponível).</summary>
    IReadOnlyList<CpuTimes> ReadPerCoreCpuTimes();

    /// <summary>Frequências reportadas pelo Windows por núcleo (null se indisponível).</summary>
    IReadOnlyList<ProcessorFrequency>? ReadProcessorFrequencies();
}

public sealed record ProcessorFrequency(int Core, int MaxMhz, int CurrentMhz);

public static class CpuUsageCalculator
{
    /// <summary>Uso entre duas leituras de GetSystemTimes (o tempo de kernel inclui o ocioso). Null se não houver intervalo válido.</summary>
    public static double? Calculate(CpuTimes previous, CpuTimes current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User) return null;
        var idle = current.Idle - previous.Idle;
        var total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        if (total == 0) return null;
        return Math.Clamp((total - idle) * 100.0 / total, 0, 100);
    }
}

#pragma warning disable CS0649, CS0169 // campos preenchidos (ou apenas usados no layout) pelo código nativo
/// <summary>Leituras reais via kernel32 (somente leitura). "Disponível" inclui o cache em espera do Windows.</summary>
public sealed class WindowsSystemSampler : ISystemSampler
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile;
        public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime; public long KernelTime; public long UserTime;
        public long DpcTime; public long InterruptTime; public uint InterruptCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number; public uint MaxMhz; public uint CurrentMhz;
        public uint MhzLimit; public uint MaxIdleState; public uint CurrentIdleState;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, IntPtr output, uint outputLength);

    private const int SystemProcessorPerformanceInformation = 8;
    private const int ProcessorInformationLevel = 11;

    private static ulong ToUlong(FILETIME f) => ((ulong)f.High << 32) | f.Low;

    public IReadOnlyList<CpuTimes> ReadPerCoreCpuTimes()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<CpuTimes>();
        var count = Environment.ProcessorCount;
        var size = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(count * size);
        try
        {
            if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, count * size, out var returned) != 0)
                return Array.Empty<CpuTimes>();
            var cores = Math.Min(count, returned / size);
            var list = new List<CpuTimes>(cores);
            for (var i = 0; i < cores; i++)
            {
                var info = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buffer + i * size);
                list.Add(new CpuTimes((ulong)info.IdleTime, (ulong)info.KernelTime, (ulong)info.UserTime));   // o tempo de kernel inclui o ocioso
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public IReadOnlyList<ProcessorFrequency>? ReadProcessorFrequencies()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var count = Environment.ProcessorCount;
        var size = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(count * size);
        try
        {
            if (CallNtPowerInformation(ProcessorInformationLevel, IntPtr.Zero, 0, buffer, (uint)(count * size)) != 0) return null;
            var list = new List<ProcessorFrequency>(count);
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(buffer + i * size);
                list.Add(new ProcessorFrequency((int)info.Number, (int)info.MaxMhz, (int)info.CurrentMhz));
            }
            return list;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public CpuTimes? ReadCpuTimes()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetSystemTimes(out var idle, out var kernel, out var user)
            ? new CpuTimes(ToUlong(idle), ToUlong(kernel), ToUlong(user))
            : null;
    }

    public MemorySnapshot? ReadMemory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref s) && s.ullTotalPhys > 0
            ? new MemorySnapshot((long)s.ullTotalPhys, (long)s.ullAvailPhys, (long)s.ullTotalPageFile, (long)s.ullAvailPageFile)
            : null;
    }

    public IReadOnlyList<DriveSnapshot> ReadDrives()
    {
        var list = new List<DriveSnapshot>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                    list.Add(new DriveSnapshot(d.Name, d.TotalSize, d.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unidade indisponível */ }
        }
        return list;
    }
}
#pragma warning restore CS0649, CS0169
