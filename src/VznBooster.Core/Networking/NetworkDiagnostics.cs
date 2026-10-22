using VznBooster.Core.Dns;
using VznBooster.Core.Tweaker;

namespace VznBooster.Core.Networking;

public enum FindingSeverity { Info, Warning, Problem }

public sealed record NetworkFinding(string Id, FindingSeverity Severity, string Title, string Evidence, IReadOnlyList<string> SuggestedFixIds);

/// <summary>Estado medido da conexão em um instante. Estatísticas nulas = não foi possível medir (nunca valores inventados).</summary>
public sealed record ConnectionSnapshot(
    DateTimeOffset CapturedUtc, bool HasGateway, LatencyStats? Gateway, LatencyStats? Dns,
    bool DnsServerFound, ProxySettings Proxy, VpnProxyReport VpnProxy);

/// <summary>
/// Diagnóstico de rede somente-leitura: latência/perda até o gateway (ICMP), tempo de resposta do servidor DNS configurado (consulta UDP real),
/// proxy e indícios de VPN. As recomendações vêm de limiares fixos e documentados abaixo; nada é corrigido sem confirmação.
/// </summary>
public sealed class NetworkDiagnosticService
{
    public const double GatewayLossProblemPercent = 5;
    public const double GatewayLatencyWarningMs = 30;
    public const double GatewayJitterWarningMs = 15;
    public const double DnsSlowWarningMs = 100;
    public const string DnsTestDomain = "example.com";

    private readonly IAdapterSource _adapters;
    private readonly PingTestService _ping;
    private readonly IDnsProbe _dns;
    private readonly IRegistryAccessor _registry;

    public NetworkDiagnosticService(IAdapterSource adapters, PingTestService ping, IDnsProbe dns, IRegistryAccessor registry)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _ping = ping ?? throw new ArgumentNullException(nameof(ping));
        _dns = dns ?? throw new ArgumentNullException(nameof(dns));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<ConnectionSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        var list = await Task.Run(() => _adapters.ReadAll(), ct).ConfigureAwait(false);
        var active = list.FirstOrDefault(a => a.Status == "Up" && a.Gateways.Any(IsUsableIPv4));

        LatencyStats? gateway = null;
        LatencyStats? dns = null;
        var dnsServerFound = false;
        if (active is not null)
        {
            var gw = active.Gateways.First(IsUsableIPv4);
            gateway = (await _ping.RunAsync(gw, 5, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2), ct).ConfigureAwait(false)).Stats;

            var server = active.DnsServers.FirstOrDefault(IsUsableIPv4);
            if (server is not null)
            {
                dnsServerFound = true;
                var samples = new List<double?>();
                for (var i = 0; i < 3; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = await _dns.QueryAsync(server, DnsTestDomain, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    samples.Add(r.Status == DnsProbeStatus.Answered ? r.Ms : null);
                }
                dns = LatencyStats.FromSamples(samples);
            }
        }

        var proxy = ProxyReader.Read(_registry);
        return new ConnectionSnapshot(DateTimeOffset.UtcNow, active is not null, gateway, dns, dnsServerFound, proxy,
            VpnProxyDetector.Evaluate(list, proxy));
    }

    /// <summary>Lista priorizada (problemas primeiro). Correções sugeridas só quando há evidência de problema que elas tratam.</summary>
    public static IReadOnlyList<NetworkFinding> Evaluate(ConnectionSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var list = new List<NetworkFinding>();
        var none = Array.Empty<string>();

        if (!s.HasGateway)
            list.Add(new NetworkFinding("no-gateway", FindingSeverity.Problem, "Sem conexão com um gateway IPv4",
                "Nenhum adaptador ativo com gateway IPv4 foi encontrado. Verifique cabo, Wi-Fi e o roteador; nenhuma correção automática é indicada.", none));

        if (s.Gateway is { } g)
        {
            if (g.Received == 0)
                list.Add(new NetworkFinding("gateway-silent", FindingSeverity.Warning, "O gateway não respondeu ao ping",
                    $"{g.Sent} tentativas sem resposta. Alguns roteadores bloqueiam ICMP; isso sozinho não prova falta de conectividade.", none));
            else
            {
                if (g.LossPercent >= GatewayLossProblemPercent)
                    list.Add(new NetworkFinding("gateway-loss", FindingSeverity.Problem, "Perda de pacotes até o gateway",
                        $"{g.LossPercent:0.#}% de perda ({g.Received}/{g.Sent} respostas). Causa provável local: Wi-Fi, cabo ou roteador.", none));
                else if (g.LossPercent > 0)
                    list.Add(new NetworkFinding("gateway-loss-minor", FindingSeverity.Warning, "Perda ocasional de pacotes até o gateway",
                        $"{g.LossPercent:0.#}% de perda ({g.Received}/{g.Sent} respostas). Uma medição curta não é conclusiva.", none));
                if (g.AvgMs is { } avg && avg > GatewayLatencyWarningMs)
                    list.Add(new NetworkFinding("gateway-latency", FindingSeverity.Warning, "Latência alta até o gateway",
                        $"Média de {avg:0.0} ms até o roteador (em rede local costuma ser bem menor). Em Wi-Fi, sinal fraco ou congestionamento podem explicar.", none));
                if (g.JitterMs is { } jit && jit > GatewayJitterWarningMs)
                    list.Add(new NetworkFinding("gateway-jitter", FindingSeverity.Warning, "Variação alta de latência (jitter) até o gateway",
                        $"Jitter de {jit:0.0} ms. Pode indicar interferência no Wi-Fi ou rede local ocupada.", none));
            }
        }

        if (s.Dns is { } d)
        {
            if (d.Received == 0)
                list.Add(new NetworkFinding("dns-fail", FindingSeverity.Problem, "O servidor DNS configurado não respondeu",
                    $"{d.Sent} consultas sem resposta. Se a conexão com o gateway está boa, limpar o cache DNS pode ajudar; renovar o DHCP é o passo seguinte.",
                    new[] { "net.dns-flush", "net.dhcp-renew" }));
            else if (d.Received < d.Sent)
                list.Add(new NetworkFinding("dns-partial", FindingSeverity.Warning, "O servidor DNS perdeu consultas",
                    $"{d.Received}/{d.Sent} respostas.", new[] { "net.dns-flush" }));
            else if (d.AvgMs is { } dns && dns > DnsSlowWarningMs)
                list.Add(new NetworkFinding("dns-slow", FindingSeverity.Warning, "Servidor DNS lento",
                    $"Média de {dns:0.0} ms por consulta. Use \"Comparar DNS\" para ver se outro servidor responde melhor; isso afeta a resolução de nomes, não o ping da partida.", none));
        }
        else if (s.HasGateway && !s.DnsServerFound)
            list.Add(new NetworkFinding("dns-none", FindingSeverity.Info, "Nenhum servidor DNS IPv4 encontrado no adaptador ativo",
                "O teste de DNS não foi executado.", none));

        foreach (var f in s.VpnProxy.Findings)
            switch (f.Category)
            {
                case VpnProxyCategory.VpnConnectedLikely or VpnProxyCategory.VpnPossibleUnconfirmed:
                    list.Add(new NetworkFinding("vpn", FindingSeverity.Info, "Indício de VPN", f.Detail + " Resultados de latência refletem o caminho pela VPN.", none));
                    break;
                case VpnProxyCategory.ProxyConfigured:
                    list.Add(new NetworkFinding("proxy", FindingSeverity.Info, "Proxy configurado", f.Detail + " O VZN Booster não altera proxies.", none));
                    break;
            }

        return list.OrderByDescending(x => x.Severity).ToList();
    }

    private static bool IsUsableIPv4(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip)
        && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && !System.Net.IPAddress.Any.Equals(ip) && !System.Net.IPAddress.IsLoopback(ip);
}
