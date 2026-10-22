using VznBooster.Core.Dns;
using VznBooster.Core.Networking;
using Xunit;

namespace VznBooster.Core.Tests;

public class DnsWireTests
{
    [Fact]
    public void Monta_consulta_A_com_cabecalho_e_rotulos_corretos()
    {
        var q = DnsWire.BuildQuery("example.com", 0x1234);
        var expected = new byte[]
        {
            0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            3, (byte)'c', (byte)'o', (byte)'m', 0,
            0x00, 0x01, 0x00, 0x01
        };
        Assert.Equal(expected, q);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b.com")]
    [InlineData("bad_name.com")]
    [InlineData("a..com")]
    public void Rejeita_nomes_invalidos(string name) => Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(name, 1));

    [Fact]
    public void Rejeita_rotulo_maior_que_63()
        => Assert.Throws<ArgumentException>(() => DnsWire.BuildQuery(new string('a', 64) + ".com", 1));

    [Fact]
    public void Interpreta_resposta_valida()
    {
        var data = new byte[] { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 2, 0, 0, 0, 0 };
        var r = DnsWire.Parse(data, 0x1234);
        Assert.True(r.Valid);
        Assert.Equal(0, r.RCode);
        Assert.Equal(2, r.AnswerCount);
    }

    [Fact]
    public void Le_nxdomain_e_servfail_pelo_codigo()
    {
        Assert.Equal(3, DnsWire.Parse(new byte[] { 0, 7, 0x81, 0x83, 0, 1, 0, 0, 0, 0, 0, 0 }, 7).RCode);
        Assert.Equal(2, DnsWire.Parse(new byte[] { 0, 7, 0x81, 0x82, 0, 1, 0, 0, 0, 0, 0, 0 }, 7).RCode);
    }

    [Fact]
    public void Rejeita_respostas_invalidas()
    {
        Assert.False(DnsWire.Parse(new byte[5], 1).Valid);                                                       // curta
        Assert.False(DnsWire.Parse(new byte[] { 0, 9, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0 }, 1).Valid);        // id diferente
        Assert.False(DnsWire.Parse(new byte[] { 0, 1, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, 1).Valid);        // não é resposta (QR=0)
    }
}

public class DnsComparerTests
{
    private sealed class FakeProbe : IDnsProbe
    {
        private readonly Func<string, DnsProbeResult> _byServer;
        public int Calls;
        public FakeProbe(Func<string, DnsProbeResult> byServer) => _byServer = byServer;
        public Task<DnsProbeResult> QueryAsync(string server, string name, TimeSpan timeout, CancellationToken ct)
        { Calls++; return Task.FromResult(_byServer(server)); }
    }

    private static DnsProbeResult Ok(double ms) => new(DnsProbeStatus.Answered, ms, 1, 0);
    private static readonly DnsProbeResult Lost = new(DnsProbeStatus.TimedOut, null, 0, -1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private static DnsCandidate C(string id, string server) => new(id, id, server);

    private static DnsProviderResult R(string id, params double?[] samples)
        => new(C(id, "1.1.1.1"), LatencyStats.FromSamples(samples), samples.Count(s => s is null), samples.Length);

    [Fact]
    public async Task Compara_e_aponta_o_mais_rapido_quando_a_diferenca_e_significativa()
    {
        var probe = new FakeProbe(s => s == "1.1.1.1" ? Ok(10) : Ok(40));
        var cmp = await new DnsComparer(probe).RunAsync(new[] { C("a", "1.1.1.1"), C("b", "8.8.8.8") }, 2, Timeout);
        Assert.Equal("a", cmp.BestId);
        Assert.Equal(MeasurementKind.Measured, cmp.Kind);
        Assert.Equal(2 * DnsComparer.Domains.Count * 2, probe.Calls);
        Assert.All(cmp.Results, r => Assert.Equal(2 * DnsComparer.Domains.Count, r.Queries));
        Assert.Contains("jogo", cmp.Verdict);
    }

    [Fact]
    public async Task Sem_diferenca_significativa_nao_inventa_vencedor()
    {
        var probe = new FakeProbe(s => s == "1.1.1.1" ? Ok(20) : Ok(21));
        var cmp = await new DnsComparer(probe).RunAsync(new[] { C("a", "1.1.1.1"), C("b", "8.8.8.8") }, 1, Timeout);
        Assert.Null(cmp.BestId);
        Assert.Contains("Sem diferença significativa", cmp.Verdict);
    }

    [Fact]
    public void Criterio_exige_diferenca_absoluta_e_relativa()
    {
        Assert.Equal("a", DnsComparer.Judge(new[] { R("a", 10, 10), R("b", 30, 30) }).BestId);        // 20 ms e 66%
        Assert.Null(DnsComparer.Judge(new[] { R("a", 1.0, 1.0), R("b", 2.0, 2.0) }).BestId);          // 50%, mas só 1 ms
        Assert.Null(DnsComparer.Judge(new[] { R("a", 100, 100), R("b", 110, 110) }).BestId);          // 10 ms, mas só ~9%
    }

    [Fact]
    public void Servidor_que_perde_a_maioria_das_consultas_nao_e_elegivel()
    {
        var (best, verdict) = DnsComparer.Judge(new[] { R("a", 10, null, null, null), R("b", 50, 50, 50, 50) });
        Assert.Null(best);
        Assert.Contains("Dados insuficientes", verdict);
    }

    [Fact]
    public async Task Sem_respostas_fica_indisponivel()
    {
        var cmp = await new DnsComparer(new FakeProbe(_ => Lost)).RunAsync(new[] { C("a", "1.1.1.1"), C("b", "8.8.8.8") }, 1, Timeout);
        Assert.Equal(MeasurementKind.Unavailable, cmp.Kind);
        Assert.Null(cmp.BestId);
        Assert.All(cmp.Results, r => Assert.Equal(100, r.Stats.LossPercent));
    }

    [Fact]
    public async Task Valida_entradas_antes_de_consultar()
    {
        var probe = new FakeProbe(_ => Ok(1));
        var cmp = new DnsComparer(probe);
        await Assert.ThrowsAsync<ArgumentException>(() => cmp.RunAsync(Array.Empty<DnsCandidate>(), 1, Timeout));
        await Assert.ThrowsAsync<ArgumentException>(() => cmp.RunAsync(new[] { C("a", "nao-e-ip") }, 1, Timeout));
        await Assert.ThrowsAsync<ArgumentException>(() => cmp.RunAsync(new[] { C("a", "2606:4700:4700::1111") }, 1, Timeout));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cmp.RunAsync(new[] { C("a", "1.1.1.1") }, 0, Timeout));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cmp.RunAsync(new[] { C("a", "1.1.1.1") }, 1, TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<ArgumentException>(() => cmp.RunAsync(Enumerable.Range(0, 9).Select(i => C($"c{i}", "1.1.1.1")).ToList(), 1, Timeout));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Cancelamento_interrompe_sem_resultado_parcial()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var probe = new FakeProbe(_ => Ok(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DnsComparer(probe).RunAsync(new[] { C("a", "1.1.1.1") }, 1, Timeout, null, cts.Token));
        Assert.Equal(0, probe.Calls);
    }
}
