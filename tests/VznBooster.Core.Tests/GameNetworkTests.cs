using System.Net;
using System.Text.RegularExpressions;
using VznBooster.Core.Networking;
using Xunit;

namespace VznBooster.Core.Tests;

public class GameCatalogTests
{
    [Fact]
    public void Tem_os_tres_jogos_pedidos_com_regioes_e_aviso()
    {
        var names = GameCatalog.All.Select(g => g.Name).ToList();
        Assert.Contains("Valorant", names);
        Assert.Contains("Rocket League", names);
        Assert.Contains("Fortnite", names);
        Assert.All(GameCatalog.All, g =>
        {
            Assert.NotEmpty(g.Regions);
            Assert.Contains("NÃO é medido", g.Disclaimer);
            Assert.Equal(g.Regions.Count, g.Regions.Select(r => r.Id).Distinct().Count());
        });
    }

    [Fact]
    public void Destinos_sao_nomes_de_endpoints_publicos_da_aws_e_nunca_enderecos_ip_de_servidores_de_jogo()
    {
        var pattern = new Regex(@"^ec2\.[a-z]{2}-[a-z]+-\d\.amazonaws\.com$");
        foreach (var region in GameCatalog.All.SelectMany(g => g.Regions))
        {
            Assert.Matches(pattern, region.Host);
            Assert.False(IPAddress.TryParse(region.Host, out _));
            Assert.False(string.IsNullOrWhiteSpace(region.Reference));
        }
    }

    [Fact]
    public void Busca_por_id()
    {
        Assert.Equal("Fortnite", GameCatalog.Find("fortnite")!.Name);
        Assert.Null(GameCatalog.Find("inexistente"));
    }
}

public class GameNetworkServiceTests
{
    private sealed class FakeTcp : ITcpProbe
    {
        private readonly Queue<TcpProbeResult> _q;
        public int Calls;
        public string? LastHost;
        public int LastPort;
        public FakeTcp(params TcpProbeResult[] r) => _q = new Queue<TcpProbeResult>(r);
        public Task<TcpProbeResult> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
        { Calls++; LastHost = host; LastPort = port; return Task.FromResult(_q.Dequeue()); }
    }

    private static TcpProbeResult Ok(double ms) => new(TcpProbeStatus.Connected, ms);
    private static readonly TcpProbeResult Fail = new(TcpProbeStatus.TimedOut, null);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static GameProfile Game => GameCatalog.Find("valorant")!;
    private static GameRegion Region => Game.Regions.First(r => r.Id == "br");

    [Fact]
    public async Task Resultado_e_estimado_nunca_medido_e_usa_o_destino_auxiliar_da_regiao()
    {
        var tcp = new FakeTcp(Ok(10), Ok(20), Fail, Ok(30));
        var r = await new GameNetworkService(tcp).RunAsync(Game, Region, 4, TimeSpan.Zero, Timeout);

        Assert.Equal(MeasurementKind.Estimated, r.Kind);
        Assert.NotEqual(MeasurementKind.Measured, r.Kind);
        Assert.Equal("Baixa", r.Confidence);
        Assert.Equal(Region.Host, tcp.LastHost);
        Assert.Equal(443, tcp.LastPort);
        Assert.Equal(25, r.Stats.LossPercent);
        Assert.Equal(20, r.Stats.AvgMs);
        Assert.Contains("ESTIMATIVA INDIRETA", r.Method);
        Assert.Contains(r.Notes, n => n.Contains("AWS"));
        Assert.Contains(r.Notes, n => n.Contains("ping real da partida"));
        Assert.Contains(r.Notes, n => n.Contains("NÃO é medido"));
    }

    [Fact]
    public async Task Sem_nenhuma_resposta_fica_indisponivel_sem_numeros()
    {
        var r = await new GameNetworkService(new FakeTcp(Fail, Fail)).RunAsync(Game, Region, 2, TimeSpan.Zero, Timeout);
        Assert.Equal(MeasurementKind.Unavailable, r.Kind);
        Assert.Null(r.Stats.AvgMs);
        Assert.Equal(100, r.Stats.LossPercent);
    }

    [Fact]
    public async Task Valida_entradas_antes_de_conectar()
    {
        var tcp = new FakeTcp();
        var svc = new GameNetworkService(tcp);
        var outraRegiao = GameCatalog.Find("fortnite")!.Regions.First(r => r.Id == "asia");     // não pertence ao Valorant
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RunAsync(Game, outraRegiao, 3, TimeSpan.Zero, Timeout));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.RunAsync(Game, Region, 0, TimeSpan.Zero, Timeout));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.RunAsync(Game, Region, GameNetworkService.MaxSamples + 1, TimeSpan.Zero, Timeout));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.RunAsync(Game, Region, 3, TimeSpan.Zero, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, tcp.Calls);
    }

    [Fact]
    public async Task Cancelamento_interrompe_sem_resultado_parcial()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tcp = new FakeTcp(Ok(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GameNetworkService(tcp).RunAsync(Game, Region, 3, TimeSpan.Zero, Timeout, cts.Token));
        Assert.Equal(0, tcp.Calls);
    }
}
