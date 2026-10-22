using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VznBooster.Core.Monitoring;

public sealed record ProcessSnapshot(int Pid, string Name, long WorkingSetBytes, TimeSpan? CpuTime, int SessionId, bool HasWindow);

public enum ProcessKind { System, Service, Application, Background }

/// <summary>Classificação apenas por características observáveis; nunca presume que um processo desconhecido seja malicioso.</summary>
public static class ProcessClassifier
{
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "System Idle Process", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "fontdrvhost", "dwm", "ntoskrnl"
    };

    public static ProcessKind Classify(ProcessSnapshot p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var name = p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name[..^4] : p.Name;
        if (p.Pid is 0 or 4 || SystemNames.Contains(name)) return ProcessKind.System;
        if (p.SessionId == 0) return ProcessKind.Service;
        return p.HasWindow ? ProcessKind.Application : ProcessKind.Background;
    }
}

public static class ProcessUsageCalculator
{
    /// <summary>% de CPU total (todos os núcleos = 100%) por PID entre duas amostras. Sem leitura anterior ou com contador regredindo, o PID é omitido.</summary>
    public static IReadOnlyDictionary<int, double> ComputeCpu(
        IReadOnlyDictionary<int, TimeSpan> previous, IReadOnlyList<ProcessSnapshot> current, TimeSpan elapsed, int logicalProcessors)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var result = new Dictionary<int, double>();
        if (elapsed <= TimeSpan.Zero || logicalProcessors <= 0) return result;
        foreach (var p in current)
        {
            if (p.CpuTime is not { } now || !previous.TryGetValue(p.Pid, out var before)) continue;
            var delta = now - before;
            if (delta < TimeSpan.Zero) continue;
            result[p.Pid] = Math.Clamp(delta.TotalSeconds / (elapsed.TotalSeconds * logicalProcessors) * 100, 0, 100);
        }
        return result;
    }
}

public interface IProcessSampler
{
    IReadOnlyList<ProcessSnapshot> Sample();
}

/// <summary>Amostra somente-leitura dos processos. Processos protegidos ficam com CPU indisponível (null); nenhum é aberto para escrita ou encerrado.</summary>
public sealed class SystemProcessSampler : IProcessSampler
{
    public IReadOnlyList<ProcessSnapshot> Sample()
    {
        var list = new List<ProcessSnapshot>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                TimeSpan? cpu = null;
                try { cpu = p.TotalProcessorTime; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { /* protegido */ }

                var hasWindow = false;
                try { hasWindow = p.MainWindowHandle != IntPtr.Zero; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }

                list.Add(new ProcessSnapshot(p.Id, p.ProcessName, p.WorkingSet64, cpu, p.SessionId, hasWindow));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* o processo terminou durante a leitura */ }
            finally { p.Dispose(); }
        }
        return list;
    }
}

/// <summary>Caminho do executável via QueryFullProcessImageName (acesso limitado, sem administrador na maioria dos casos). Null se indisponível.</summary>
public static class ProcessPathResolver
{
    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static string? TryGetPath(int pid)
    {
        if (!OperatingSystem.IsWindows() || pid <= 4) return null;
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
}
