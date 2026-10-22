using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using VznBooster.Core.Formatting;

namespace VznBooster.Core.Hardware;

/// <summary>Leitura somente-leitura de HKLM. Existe como interface para testar o inventário sem o registro real.</summary>
public interface ILocalMachineRegistry
{
    string? GetString(string keyPath, string valueName);
    long? GetNumber(string keyPath, string valueName);
    IReadOnlyList<string> GetSubKeyNames(string keyPath);
}

public sealed class WindowsLocalMachineRegistry : ILocalMachineRegistry
{
    private static bool Valid(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase) && !path.Contains("..");

    private static object? Value(string path, string name)
    {
        if (!Valid(path)) return null;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return null; }
    }

    public string? GetString(string keyPath, string valueName) => Value(keyPath, valueName) switch
    {
        string s => s.Trim().TrimEnd('\0'),
        string[] many => string.Join(" ", many),
        _ => null
    };

    public long? GetNumber(string keyPath, string valueName) => Value(keyPath, valueName) switch
    {
        int i => (long?)unchecked((uint)i),
        long l => (long?)l,
        byte[] b when b.Length == 8 => (long?)BitConverter.ToInt64(b, 0),
        byte[] b when b.Length == 4 => (long?)BitConverter.ToUInt32(b, 0),
        _ => (long?)null
    };

    public IReadOnlyList<string> GetSubKeyNames(string keyPath)
    {
        if (!Valid(keyPath) || !OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return Array.Empty<string>(); }
    }
}

public sealed record DiskInfo(string Name, string FileSystem, long TotalBytes, long FreeBytes);

public sealed record PlatformInfo(
    string ProcessArchitecture, string OsArchitecture, int LogicalProcessors, int OsBuild,
    long? TotalMemoryBytes, long? CommitLimitBytes, IReadOnlyList<DiskInfo> Disks,
    int? MonitorCount, int? PrimaryWidth, int? PrimaryHeight, TimeSpan Uptime,
    bool? VirtualizationEnabled, bool? Avx2);

public interface IPlatformInfoSource
{
    PlatformInfo Read();
}

public sealed class WindowsPlatformInfoSource : IPlatformInfoSource
{
    private const int PfVirtFirmwareEnabled = 21;
    private const int PfAvx2 = 40;
    private const int SmCxScreen = 0, SmCyScreen = 1, SmCMonitors = 80;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessorFeaturePresent(uint feature);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    public PlatformInfo Read()
    {
        var windows = OperatingSystem.IsWindows();
        var sampler = new Monitoring.WindowsSystemSampler();
        var memory = sampler.ReadMemory();

        var disks = new List<DiskInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                    disks.Add(new DiskInfo(d.Name, d.DriveFormat, d.TotalSize, d.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* indisponível */ }
        }

        return new PlatformInfo(
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount, Environment.OSVersion.Version.Build,
            memory?.TotalBytes, memory?.CommitLimitBytes, disks,
            windows ? GetSystemMetrics(SmCMonitors) : null,
            windows ? GetSystemMetrics(SmCxScreen) : null,
            windows ? GetSystemMetrics(SmCyScreen) : null,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            windows ? IsProcessorFeaturePresent(PfVirtFirmwareEnabled) : null,
            windows ? IsProcessorFeaturePresent(PfAvx2) : null);
    }
}

/// <summary>De onde veio o valor. Valores não lidos aparecem como indisponíveis; nada é estimado.</summary>
public enum ValueSource { SystemReported, Measured, Unavailable }

public sealed record HardwareValue(string Label, string Value, ValueSource Source)
{
    public string SourceText => Source switch
    {
        ValueSource.Measured => "medido",
        ValueSource.SystemReported => "informado pelo sistema",
        _ => "indisponível"
    };
}

public sealed record HardwareSection(string Title, IReadOnlyList<HardwareValue> Items);

public sealed record HardwareInventory(IReadOnlyList<HardwareSection> Sections);

public static class HardwareInventoryReader
{
    private const string Cpu0 = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string Bios = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string WinNt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static HardwareValue Reported(string label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? Missing(label) : new HardwareValue(label, value, ValueSource.SystemReported);

    private static HardwareValue Missing(string label, string reason = "indisponível") => new(label, reason, ValueSource.Unavailable);

    private static HardwareValue FromBool(string label, bool? v) =>
        v is null ? Missing(label) : new HardwareValue(label, v.Value ? "Sim" : "Não", ValueSource.SystemReported);

    public static HardwareInventory Read(ILocalMachineRegistry lm, PlatformInfo p)
    {
        ArgumentNullException.ThrowIfNull(lm);
        ArgumentNullException.ThrowIfNull(p);
        var sections = new List<HardwareSection>();

        // Processador
        var mhz = lm.GetNumber(Cpu0, "~MHz");
        sections.Add(new HardwareSection("Processador", new[]
        {
            Reported("Modelo", lm.GetString(Cpu0, "ProcessorNameString")),
            Reported("Fabricante", lm.GetString(Cpu0, "VendorIdentifier")),
            mhz is { } m ? new HardwareValue("Frequência nominal", $"{m} MHz", ValueSource.SystemReported) : Missing("Frequência nominal"),
            new HardwareValue("Threads lógicos", p.LogicalProcessors.ToString(CultureInfo.CurrentCulture), ValueSource.SystemReported),
            Missing("Núcleos físicos", "não lido nesta versão"),
            new HardwareValue("Arquitetura", $"processo {p.ProcessArchitecture}, sistema {p.OsArchitecture}", ValueSource.SystemReported),
            FromBool("Virtualização habilitada no firmware", p.VirtualizationEnabled),
            FromBool("Instruções AVX2", p.Avx2),
        }));

        // Placa-mãe e firmware
        sections.Add(new HardwareSection("Placa-mãe e firmware", new[]
        {
            Reported("Fabricante do sistema", lm.GetString(Bios, "SystemManufacturer")),
            Reported("Modelo do sistema", lm.GetString(Bios, "SystemProductName")),
            Reported("Fabricante da placa-mãe", lm.GetString(Bios, "BaseBoardManufacturer")),
            Reported("Modelo da placa-mãe", lm.GetString(Bios, "BaseBoardProduct")),
            Reported("Fabricante do BIOS/UEFI", lm.GetString(Bios, "BIOSVendor")),
            Reported("Versão do BIOS/UEFI", lm.GetString(Bios, "BIOSVersion")),
            Reported("Data do BIOS/UEFI", lm.GetString(Bios, "BIOSReleaseDate")),
        }));

        // Memória
        sections.Add(new HardwareSection("Memória RAM", new[]
        {
            p.TotalMemoryBytes is { } total
                ? new HardwareValue("Capacidade visível ao Windows", ByteSize.Format(total), ValueSource.SystemReported)
                : Missing("Capacidade visível ao Windows"),
            p.CommitLimitBytes is { } commit
                ? new HardwareValue("Limite de memória comprometida", ByteSize.Format(commit), ValueSource.SystemReported)
                : Missing("Limite de memória comprometida"),
            Missing("Módulos (quantidade, velocidade, fabricante)", "não lidos nesta versão"),
        }));

        // Placas de vídeo
        var gpus = new List<HardwareSection>();
        foreach (var sub in lm.GetSubKeyNames(DisplayClass).Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)).OrderBy(n => n, StringComparer.Ordinal))
        {
            var path = $@"{DisplayClass}\{sub}";
            var name = lm.GetString(path, "DriverDesc");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var vram = lm.GetNumber(path, "HardwareInformation.qwMemorySize") ?? lm.GetNumber(path, "HardwareInformation.MemorySize");
            gpus.Add(new HardwareSection($"Placa de vídeo {gpus.Count + 1}", new[]
            {
                Reported("Modelo", name),
                Reported("Fabricante do driver", lm.GetString(path, "ProviderName")),
                Reported("Versão do driver", lm.GetString(path, "DriverVersion")),
                vram is { } v and > 0 ? new HardwareValue("Memória de vídeo dedicada", ByteSize.Format(v), ValueSource.SystemReported) : Missing("Memória de vídeo dedicada"),
                Missing("Uso, temperatura e memória em uso", "não lidos nesta versão"),
            }));
        }
        if (gpus.Count == 0)
            sections.Add(new HardwareSection("Placa de vídeo", new[] { Missing("Modelo") }));
        else
            sections.AddRange(gpus);

        // Armazenamento
        var disks = p.Disks.Select(d => new HardwareValue(d.Name,
            $"{d.FileSystem} — {ByteSize.Format(d.TotalBytes)} no total, {ByteSize.Format(d.FreeBytes)} livres", ValueSource.Measured)).ToList();
        if (disks.Count == 0) disks.Add(Missing("Unidades fixas"));
        disks.Add(Missing("Tipo (SSD/HDD) e saúde (SMART)", "sem fonte confiável nesta versão"));
        sections.Add(new HardwareSection("Armazenamento", disks));

        // Sistema operacional
        var family = p.OsBuild >= 22000 ? "11" : "10";
        var edition = lm.GetString(WinNt, "EditionID");
        var ubr = lm.GetNumber(WinNt, "UBR");
        var install = lm.GetNumber(WinNt, "InstallDate");
        sections.Add(new HardwareSection("Sistema operacional", new[]
        {
            new HardwareValue("Edição", string.IsNullOrWhiteSpace(edition) ? $"Windows {family}" : $"Windows {family} ({edition})", ValueSource.SystemReported),
            Reported("Versão", lm.GetString(WinNt, "DisplayVersion")),
            new HardwareValue("Build", ubr is { } u ? $"{p.OsBuild}.{u}" : p.OsBuild.ToString(CultureInfo.InvariantCulture), ValueSource.SystemReported),
            install is { } secs and > 0
                ? new HardwareValue("Data de instalação", DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime().ToString("dd/MM/yyyy"), ValueSource.SystemReported)
                : Missing("Data de instalação"),
            new HardwareValue("Tempo de atividade", FormatUptime(p.Uptime), ValueSource.Measured),
            Missing("Estado de ativação", "não verificado (sem consulta confiável nesta versão)"),
        }));

        // Monitores
        sections.Add(new HardwareSection("Monitores", new[]
        {
            p.MonitorCount is { } mc and > 0 ? new HardwareValue("Monitores conectados", mc.ToString(CultureInfo.CurrentCulture), ValueSource.SystemReported) : Missing("Monitores conectados"),
            p.PrimaryWidth is { } w and > 0 && p.PrimaryHeight is { } h and > 0
                ? new HardwareValue("Resolução do monitor principal", $"{w} × {h}", ValueSource.SystemReported)
                : Missing("Resolução do monitor principal"),
            Missing("Frequência de atualização e modelo", "não lidos nesta versão"),
        }));

        // Sensores
        sections.Add(new HardwareSection("Sensores", new[]
        {
            Missing("Temperaturas de CPU e GPU", "a leitura de sensores não está implementada; nenhuma estimativa é usada"),
        }));

        return new HardwareInventory(sections);
    }

    public static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h {t.Minutes} min" : $"{t.Hours} h {t.Minutes} min";
}

public static class HardwareReport
{
    /// <summary>
    /// Relatório em texto. Por padrão NÃO inclui o nome do computador nem do usuário (dados pessoais); a inclusão é explícita.
    /// </summary>
    public static string Build(HardwareInventory inventory, DateTimeOffset generatedAt, bool includePersonal,
        string? computerName = null, string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var sb = new StringBuilder();
        sb.AppendLine("VZN Booster — Relatório de hardware");
        sb.AppendLine($"Gerado em: {generatedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine(includePersonal
            ? "Dados pessoais: INCLUÍDOS por escolha do usuário."
            : "Dados pessoais: ocultados (nome do computador e do usuário não foram incluídos).");
        sb.AppendLine("Fontes: \"informado pelo sistema\" = lido do Windows; \"medido\" = medido pelo aplicativo; \"indisponível\" = não obtido (nada é estimado).");
        if (includePersonal)
        {
            sb.AppendLine();
            sb.AppendLine($"Nome do computador: {computerName ?? "indisponível"}");
            sb.AppendLine($"Nome da conta: {userName ?? "indisponível"}");
        }
        foreach (var section in inventory.Sections)
        {
            sb.AppendLine();
            sb.AppendLine(section.Title);
            foreach (var item in section.Items)
                sb.AppendLine($"  {item.Label}: {item.Value} [{item.SourceText}]");
        }
        return sb.ToString();
    }
}
