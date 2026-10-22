using VznBooster.Core.Hardware;
using VznBooster.Core.Monitoring;
using Xunit;

namespace VznBooster.Core.Tests;

public class ProcessMonitorTests
{
    private static ProcessSnapshot P(int pid, string name, int session = 1, bool window = false, double? cpuSeconds = null, long ws = 1000) =>
        new(pid, name, ws, cpuSeconds is null ? null : TimeSpan.FromSeconds(cpuSeconds.Value), session, window);

    [Theory]
    [InlineData(0, "Idle", 0, false, ProcessKind.System)]
    [InlineData(4, "System", 0, false, ProcessKind.System)]
    [InlineData(500, "csrss", 0, false, ProcessKind.System)]
    [InlineData(500, "LSASS.exe", 0, false, ProcessKind.System)]
    [InlineData(900, "svchost", 0, false, ProcessKind.Service)]
    [InlineData(1200, "chrome", 1, true, ProcessKind.Application)]
    [InlineData(1300, "meuServicoDesconhecido", 1, false, ProcessKind.Background)]
    public void Classifica_apenas_por_caracteristicas_observaveis(int pid, string name, int session, bool window, ProcessKind expected)
        => Assert.Equal(expected, ProcessClassifier.Classify(P(pid, name, session, window)));

    [Fact]
    public void Calcula_cpu_por_processo_em_relacao_ao_total_de_nucleos()
    {
        var previous = new Dictionary<int, TimeSpan> { [1] = TimeSpan.FromSeconds(10), [2] = TimeSpan.FromSeconds(5) };
        var current = new[] { P(1, "a", cpuSeconds: 14), P(2, "b", cpuSeconds: 5), P(3, "novo", cpuSeconds: 1), P(4, "protegido", cpuSeconds: null) };

        var cpu = ProcessUsageCalculator.ComputeCpu(previous, current, TimeSpan.FromSeconds(2), 4);

        Assert.Equal(50, cpu[1], 6);        // 4 s de CPU em 2 s com 4 núcleos = 50% do total
        Assert.Equal(0, cpu[2], 6);
        Assert.False(cpu.ContainsKey(3));   // sem leitura anterior: omitido, não inventado
        Assert.False(cpu.ContainsKey(4));   // CPU indisponível: omitido
    }

    [Fact]
    public void Contador_que_regride_ou_intervalo_invalido_nao_gera_valor()
    {
        var previous = new Dictionary<int, TimeSpan> { [1] = TimeSpan.FromSeconds(10) };
        Assert.Empty(ProcessUsageCalculator.ComputeCpu(previous, new[] { P(1, "a", cpuSeconds: 3) }, TimeSpan.FromSeconds(2), 4));
        Assert.Empty(ProcessUsageCalculator.ComputeCpu(previous, new[] { P(1, "a", cpuSeconds: 12) }, TimeSpan.Zero, 4));
        Assert.Empty(ProcessUsageCalculator.ComputeCpu(previous, new[] { P(1, "a", cpuSeconds: 12) }, TimeSpan.FromSeconds(2), 0));
    }

    [Fact]
    public void Limita_a_100_por_cento()
    {
        var previous = new Dictionary<int, TimeSpan> { [1] = TimeSpan.Zero };
        var cpu = ProcessUsageCalculator.ComputeCpu(previous, new[] { P(1, "a", cpuSeconds: 100) }, TimeSpan.FromSeconds(1), 2);
        Assert.Equal(100, cpu[1]);
    }

    [Fact]
    public void Memoria_comprometida_e_calculada_quando_ha_limite_e_disponivel()
    {
        var m = new MemorySnapshot(16_000, 4_000, 24_000, 10_000);
        Assert.Equal(14_000, m.CommitUsedBytes);
        Assert.Null(new MemorySnapshot(16_000, 4_000).CommitUsedBytes);
    }
}

public class HardwareInventoryTests
{
    private sealed class FakeLm : ILocalMachineRegistry
    {
        public Dictionary<(string, string), object> Values { get; } = new();
        public Dictionary<string, string[]> SubKeys { get; } = new();
        public string? GetString(string keyPath, string valueName) => Values.TryGetValue((keyPath, valueName), out var v) ? v as string : null;
        public long? GetNumber(string keyPath, string valueName) => Values.TryGetValue((keyPath, valueName), out var v) && v is long l ? l : null;
        public IReadOnlyList<string> GetSubKeyNames(string keyPath) => SubKeys.TryGetValue(keyPath, out var k) ? k : Array.Empty<string>();
    }

    private const string Cpu0 = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string Bios = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string WinNt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string Gpu = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const long Gb = 1024L * 1024 * 1024;

    private static PlatformInfo Platform(int build = 22631) => new("X64", "X64", 16, build, 16 * Gb, 24 * Gb,
        new[] { new DiskInfo(@"C:\", "NTFS", 500 * Gb, 200 * Gb) }, 2, 1920, 1080, TimeSpan.FromHours(30), true, true);

    private static HardwareValue Item(HardwareInventory inv, string section, string label) =>
        inv.Sections.First(s => s.Title == section).Items.First(i => i.Label == label);

    [Fact]
    public void Le_processador_firmware_e_sistema_do_registro_marcando_a_fonte()
    {
        var lm = new FakeLm();
        lm.Values[(Cpu0, "ProcessorNameString")] = "Processador Teste 3000";
        lm.Values[(Cpu0, "~MHz")] = 3600L;
        lm.Values[(Bios, "BIOSVersion")] = "F10";
        lm.Values[(WinNt, "EditionID")] = "Professional";
        lm.Values[(WinNt, "UBR")] = 4602L;

        var inv = HardwareInventoryReader.Read(lm, Platform());

        Assert.Equal("Processador Teste 3000", Item(inv, "Processador", "Modelo").Value);
        Assert.Equal(ValueSource.SystemReported, Item(inv, "Processador", "Modelo").Source);
        Assert.Equal("3600 MHz", Item(inv, "Processador", "Frequência nominal").Value);
        Assert.Equal("Sim", Item(inv, "Processador", "Virtualização habilitada no firmware").Value);
        Assert.Equal("F10", Item(inv, "Placa-mãe e firmware", "Versão do BIOS/UEFI").Value);
        Assert.Equal("Windows 11 (Professional)", Item(inv, "Sistema operacional", "Edição").Value);
        Assert.Equal("22631.4602", Item(inv, "Sistema operacional", "Build").Value);
    }

    [Fact]
    public void O_que_nao_foi_lido_fica_indisponivel_e_nunca_inventado()
    {
        var inv = HardwareInventoryReader.Read(new FakeLm(), Platform());

        var model = Item(inv, "Processador", "Modelo");
        Assert.Equal(ValueSource.Unavailable, model.Source);
        Assert.Equal("indisponível", model.SourceText);
        Assert.Equal(ValueSource.Unavailable, Item(inv, "Placa de vídeo", "Modelo").Source);
        Assert.Equal(ValueSource.Unavailable, Item(inv, "Sensores", "Temperaturas de CPU e GPU").Source);
        Assert.Equal(ValueSource.Unavailable, Item(inv, "Memória RAM", "Módulos (quantidade, velocidade, fabricante)").Source);
        Assert.Equal(ValueSource.Unavailable, Item(inv, "Armazenamento", "Tipo (SSD/HDD) e saúde (SMART)").Source);
        Assert.Equal(ValueSource.Unavailable, Item(inv, "Processador", "Núcleos físicos").Source);
    }

    [Fact]
    public void Windows_10_e_identificado_pela_build()
    {
        var inv = HardwareInventoryReader.Read(new FakeLm(), Platform(build: 19045));
        Assert.StartsWith("Windows 10", Item(inv, "Sistema operacional", "Edição").Value);
    }

    [Fact]
    public void Cada_placa_de_video_vira_uma_secao_com_memoria_quando_informada()
    {
        var lm = new FakeLm();
        lm.SubKeys[Gpu] = new[] { "0000", "0001", "Configuration", "Properties" };
        lm.Values[(Gpu + @"\0000", "DriverDesc")] = "GPU Integrada Teste";
        lm.Values[(Gpu + @"\0001", "DriverDesc")] = "GPU Dedicada Teste";
        lm.Values[(Gpu + @"\0001", "HardwareInformation.qwMemorySize")] = 8 * Gb;
        lm.Values[(Gpu + @"\0001", "DriverVersion")] = "31.0.1";

        var inv = HardwareInventoryReader.Read(lm, Platform());

        Assert.Equal("GPU Integrada Teste", Item(inv, "Placa de vídeo 1", "Modelo").Value);
        Assert.Equal(ValueSource.Unavailable, Item(inv, "Placa de vídeo 1", "Memória de vídeo dedicada").Source);
        Assert.Equal("8 GB", Item(inv, "Placa de vídeo 2", "Memória de vídeo dedicada").Value);
        Assert.Equal("31.0.1", Item(inv, "Placa de vídeo 2", "Versão do driver").Value);
        Assert.DoesNotContain(inv.Sections, s => s.Title.Contains("Configuration"));
    }

    [Fact]
    public void Armazenamento_e_medido()
    {
        var inv = HardwareInventoryReader.Read(new FakeLm(), Platform());
        var c = Item(inv, "Armazenamento", @"C:\");
        Assert.Equal(ValueSource.Measured, c.Source);
        Assert.Contains("NTFS", c.Value);
    }

    [Fact]
    public void Relatorio_oculta_dados_pessoais_por_padrao()
    {
        var inv = HardwareInventoryReader.Read(new FakeLm(), Platform());
        var padrao = HardwareReport.Build(inv, DateTimeOffset.UtcNow, includePersonal: false, computerName: "MEU-PC", userName: "meuusuario");
        Assert.DoesNotContain("MEU-PC", padrao);
        Assert.DoesNotContain("meuusuario", padrao);
        Assert.Contains("ocultados", padrao);
        Assert.Contains("[indisponível]", padrao);

        var completo = HardwareReport.Build(inv, DateTimeOffset.UtcNow, includePersonal: true, computerName: "MEU-PC", userName: "meuusuario");
        Assert.Contains("MEU-PC", completo);
        Assert.Contains("INCLUÍDOS", completo);
    }

    [Fact]
    public void Formata_tempo_de_atividade()
    {
        Assert.Equal("2 h 5 min", HardwareInventoryReader.FormatUptime(TimeSpan.FromMinutes(125)));
        Assert.Equal("1 d 6 h 0 min", HardwareInventoryReader.FormatUptime(TimeSpan.FromHours(30)));
    }
}
