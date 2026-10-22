using System.Net;
using Xunit;
using VznBooster.Core.Cleaning;
using VznBooster.Core.Dns;
using VznBooster.Core.Networking;
using VznBooster.Core.Privacy;

namespace VznBooster.Core.Tests;

public class LatencyStatsTests
{
    [Fact]
    public void Calcula_estatisticas_e_perda()
    {
        var s = LatencyStats.FromSamples(new double?[] { 10, 20, null, 30 });
        Assert.Equal(4, s.Sent); Assert.Equal(3, s.Received);
        Assert.Equal(10, s.MinMs); Assert.Equal(30, s.MaxMs); Assert.Equal(20, s.AvgMs);
        Assert.Equal(25, s.LossPercent);
        Assert.Equal(10, s.JitterMs);                       // (|20-10| + |30-20|) / 2
        Assert.Equal(Math.Sqrt(200.0 / 3), s.StdDevMs!.Value, 6);
    }

    [Fact]
    public void Todas_perdidas_nao_inventa_valores()
    {
        var s = LatencyStats.FromSamples(new double?[] { null, null });
        Assert.Equal(100, s.LossPercent);
        Assert.Null(s.AvgMs); Assert.Null(s.JitterMs);
    }

    [Fact]
    public void Uma_amostra_nao_tem_jitter()
        => Assert.Null(LatencyStats.FromSamples(new double?[] { 12 }).JitterMs);

    [Fact]
    public void Rejeita_entradas_invalidas()
    {
        Assert.Throws<ArgumentException>(() => LatencyStats.FromSamples(Array.Empty<double?>()));
        Assert.Throws<ArgumentException>(() => LatencyStats.FromSamples(new double?[] { -1 }));
        Assert.Throws<ArgumentException>(() => LatencyStats.FromSamples(new double?[] { double.NaN }));
    }
}

public class DnsProfilesTests
{
    [Fact]
    public void Enderecos_sao_validos_sem_duplicidade_entre_perfis()
    {
        var all = DnsProfiles.All.SelectMany(p => p.IPv4.Concat(p.IPv6)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        foreach (var p in DnsProfiles.All)
        {
            Assert.All(p.IPv4, a => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, IPAddress.Parse(a).AddressFamily));
            Assert.All(p.IPv6, a => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetworkV6, IPAddress.Parse(a).AddressFamily));
        }
    }
}

public class DataMaskerTests
{
    [Theory]
    [InlineData("192.168.0.25", "192.168.***.***")]
    [InlineData("8.8.4.4", "***.***.***.***")]
    [InlineData("lixo", "[valor oculto]")]
    public void Mascara_ipv4(string input, string expected) => Assert.Equal(expected, DataMasker.MaskIPv4(input));

    [Fact] public void Mascara_mac() => Assert.Equal("**:**:**:**:**:**", DataMasker.MaskMac("aa-bb-cc-dd-ee-ff"));
}

public class CleanPathValidatorTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "vzn-root");

    [Fact]
    public void Aceita_somente_filhos_da_raiz()
    {
        var v = new CleanPathValidator(new[] { Root });
        Assert.True(v.IsAllowed(Path.Combine(Root, "a", "b.tmp"), out _));
        Assert.False(v.IsAllowed(Root, out _));
        Assert.False(v.IsAllowed(Root + "-outra", out _));
        Assert.False(v.IsAllowed(Path.Combine(Root, "..", "x"), out _));
        Assert.False(v.IsAllowed("", out _));
    }

    [Fact]
    public void Rejeita_raiz_de_unidade()
        => Assert.Throws<ArgumentException>(() => new CleanPathValidator(new[] { Path.GetPathRoot(Path.GetTempPath())! }));
}
