using VznBooster.Core.Dns;
using VznBooster.Core.Networking;
using Xunit;

namespace VznBooster.Core.Tests;

public class PingTestServiceTests
{
    private sealed class ScriptedProbe : IPingProbe
    {
        private readonly Queue<ProbeResult> _q;
        public int Calls;
        public ScriptedProbe(params ProbeResult[] r) => _q = new Queue<ProbeResult>(r);
        public Task<ProbeResult> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct)
        { Calls++; return Task.FromResult(_q.Dequeue()); }
    }

    private static ProbeResult Ok(double ms) => new(ProbeStatus.Reply, ms);
    private static readonly ProbeResult Lost = new(ProbeStatus.TimedOut, null);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Calcula_a_partir_das_respostas_reais()
    {
        var svc = new PingTestService(new ScriptedProbe(Ok(10), Ok(20), Lost, Ok(30)));
        var r = await svc.RunAsync("example.com", 4, TimeSpan.Zero, Timeout);
        Assert.Equal(MeasurementKind.Measured, r.Kind);
        Assert.Equal(25, r.Stats.LossPercent);
        Assert.Equal(20, r.Stats.AvgMs);
        Assert.Equal(10, r.Stats.JitterMs);
        Assert.Equal(new[] { ProbeStatus.Reply, ProbeStatus.Reply, ProbeStatus.TimedOut, ProbeStatus.Reply }, r.Statuses.ToArray());
        Assert.Contains("example.com", r.Method);
    }

    [Fact]
    public async Task Sem_nenhuma_resposta_fica_indisponivel()
    {
        var svc = new PingTestService(new ScriptedProbe(Lost, Lost));
        var r = await svc.RunAsync("8.8.8.8", 2, TimeSpan.Zero, Timeout);
        Assert.Equal(MeasurementKind.Unavailable, r.Kind);
        Assert.Null(r.Stats.AvgMs);
    }

    [Theory]
    [InlineData("", 4)]
    [InlineData("não é host", 4)]
    [InlineData("example.com", 0)]
    [InlineData("example.com", 101)]
    public async Task Valida_entradas(string target, int samples)
    {
        var probe = new ScriptedProbe();
        var svc = new PingTestService(probe);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => svc.RunAsync(target, samples, TimeSpan.Zero, Timeout));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Valida_timeout()
    {
        var svc = new PingTestService(new ScriptedProbe());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => svc.RunAsync("example.com", 1, TimeSpan.Zero, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Cancelamento_interrompe_sem_resultado_parcial()
    {
        var probe = new ScriptedProbe(Ok(1), Ok(1), Ok(1));
        var svc = new PingTestService(probe);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.RunAsync("example.com", 3, TimeSpan.Zero, Timeout, cts.Token));
        Assert.Equal(0, probe.Calls);
    }
}

public class DnsProfileMatcherTests
{
    [Fact]
    public void Identifica_perfil_conhecido_ipv4_e_ipv6()
    {
        Assert.Equal("cloudflare", DnsProfileMatcher.Identify(new[] { "1.1.1.1", "1.0.0.1" })!.Id);
        Assert.Equal("google", DnsProfileMatcher.Identify(new[] { "2001:4860:4860:0:0:0:0:8888" })!.Id);
    }

    [Fact]
    public void Mistura_de_perfis_ou_desconhecido_retorna_null()
    {
        Assert.Null(DnsProfileMatcher.Identify(new[] { "1.1.1.1", "8.8.8.8" }));
        Assert.Null(DnsProfileMatcher.Identify(new[] { "192.168.0.1" }));
        Assert.Null(DnsProfileMatcher.Identify(Array.Empty<string>()));
        Assert.Null(DnsProfileMatcher.Identify(new[] { "lixo" }));
    }
}
