using System.Globalization;
using VznBooster.Core.Formatting;
using VznBooster.Core.Monitoring;
using VznBooster.Core.Recommendations;
using Xunit;

namespace VznBooster.Core.Tests;

public class MonitoringTests
{
    [Fact]
    public void Calcula_uso_de_cpu_entre_duas_leituras()
    {
        var prev = new CpuTimes(100, 200, 100);
        var curr = new CpuTimes(150, 300, 200);       // delta: ocioso 50, kernel 100 (inclui ocioso), usuário 100
        Assert.Equal(75, CpuUsageCalculator.Calculate(prev, curr)!.Value, 6);
    }

    [Fact]
    public void Intervalo_invalido_nao_inventa_valor()
    {
        var a = new CpuTimes(100, 200, 100);
        Assert.Null(CpuUsageCalculator.Calculate(a, a));                          // sem variação
        Assert.Null(CpuUsageCalculator.Calculate(a, new CpuTimes(50, 100, 50)));  // contador regrediu
    }

    [Fact]
    public void Memoria_e_discos_calculam_percentuais()
    {
        var m = new MemorySnapshot(16_000, 4_000);
        Assert.Equal(12_000, m.UsedBytes);
        Assert.Equal(75, m.UsedPercent, 6);
        var d = new DriveSnapshot(@"C:\", 1000, 250);
        Assert.Equal(25, d.FreePercent, 6);
        Assert.Equal(75, d.UsedPercent, 6);
        Assert.Equal(0, new DriveSnapshot("X:", 0, 0).UsedPercent);
    }

    [Fact]
    public void Formata_bytes_de_forma_estavel()
    {
        Assert.Equal("512 B", ByteSize.Format(512, CultureInfo.InvariantCulture));
        Assert.Equal("1.5 KB", ByteSize.Format(1536, CultureInfo.InvariantCulture));
        Assert.Equal("2 GB", ByteSize.Format(2L * 1024 * 1024 * 1024, CultureInfo.InvariantCulture));
    }
}

public class RecommendationEngineTests
{
    private static SystemSnapshot Snap(MemorySnapshot? mem = null, params DriveSnapshot[] drives) => new(null, mem, drives);
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Disco_com_pouco_espaco_gera_recomendacao_com_evidencia()
    {
        var recs = RecommendationEngine.Evaluate(Snap(null, new DriveSnapshot(@"C:\", 500 * Gb, 200 * Gb), new DriveSnapshot(@"D:\", 1000 * Gb, 5 * Gb)), null);
        var r = Assert.Single(recs);
        Assert.Equal(@"disk.low:D:\", r.Id);
        Assert.Contains(@"D:\", r.Title);
        Assert.Equal("Cleaner", r.TargetSection);
        Assert.False(string.IsNullOrWhiteSpace(r.Evidence));
        Assert.False(string.IsNullOrWhiteSpace(r.Risk));
    }

    [Fact]
    public void Sistema_saudavel_nao_gera_recomendacoes()
    {
        var recs = RecommendationEngine.Evaluate(Snap(new MemorySnapshot(16 * Gb, 8 * Gb), new DriveSnapshot(@"C:\", 500 * Gb, 200 * Gb)), 100L * 1024 * 1024);
        Assert.Empty(recs);
    }

    [Fact]
    public void Memoria_alta_gera_recomendacao_informativa()
    {
        var recs = RecommendationEngine.Evaluate(Snap(new MemorySnapshot(16 * Gb, 2 * Gb)), null);
        var r = Assert.Single(recs);
        Assert.Equal("memory.high", r.Id);
        Assert.Null(r.TargetSection);
        Assert.Contains("não encerra", r.Risk);
    }

    [Fact]
    public void Estimativa_do_cleaner_acima_do_limite_gera_recomendacao()
    {
        var recs = RecommendationEngine.Evaluate(Snap(), 1 * Gb);
        Assert.Equal("cleaner.potential", Assert.Single(recs).Id);
    }

    [Fact]
    public void Sem_dados_nao_inventa_recomendacoes()
        => Assert.Empty(RecommendationEngine.Evaluate(Snap(), null));
}
