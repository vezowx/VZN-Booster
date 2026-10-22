using VznBooster.Core.Commands;
using VznBooster.Core.Dns;
using VznBooster.Core.Logging;
using VznBooster.Core.Networking;
using VznBooster.Core.Tweaker;
using Xunit;

namespace VznBooster.Core.Tests;

internal static class NetTestKit
{
    public static NetworkAdapterInfo Adapter(string name, string description, string type = "Ethernet", string status = "Up",
        string[]? gateways = null, string[]? dns = null) =>
        new(Guid.NewGuid().ToString("B"), name, description, type, status, 1000,
            dns ?? Array.Empty<string>(), gateways ?? Array.Empty<string>(), 7, DnsConfigSource.Automatic, Array.Empty<string>());

    public static ProxySettings NoProxy => new(true, false, null, null);

    public sealed class FakeAdapters : IAdapterSource
    {
        public List<NetworkAdapterInfo> Items { get; } = new();
        public IReadOnlyList<NetworkAdapterInfo> ReadAll() => Items;
    }

    public sealed class FakeRegistry : IRegistryAccessor
    {
        public Dictionary<(string, string), (RegKind Kind, string Value)> Values { get; } = new();
        public bool Unsupported;
        public ValueSnapshot Read(string keyPath, string valueName)
        {
            if (Unsupported) throw new NotSupportedException();
            return Values.TryGetValue((keyPath, valueName), out var v) ? new ValueSnapshot(true, v.Kind, v.Value) : ValueSnapshot.Absent;
        }
        public void Write(string keyPath, string valueName, RegKind kind, string value) => Values[(keyPath, valueName)] = (kind, value);
        public void Delete(string keyPath, string valueName) => Values.Remove((keyPath, valueName));
    }

    public sealed class FakePing : IPingProbe
    {
        public int Calls;
        public ProbeResult Result = new(ProbeStatus.Reply, 5);
        public Task<ProbeResult> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct) { Calls++; return Task.FromResult(Result); }
    }

    public sealed class FakeDns : IDnsProbe
    {
        public int Calls;
        public DnsProbeResult Result = new(DnsProbeStatus.Answered, 20, 1, 0);
        public Task<DnsProbeResult> QueryAsync(string server, string name, TimeSpan timeout, CancellationToken ct) { Calls++; return Task.FromResult(Result); }
    }

    public static LatencyStats Stats(params double?[] samples) => LatencyStats.FromSamples(samples);

    public static ConnectionSnapshot Snapshot(bool hasGateway = true, LatencyStats? gateway = null, LatencyStats? dns = null,
        bool dnsFound = true, VpnProxyReport? vpn = null) =>
        new(DateTimeOffset.UtcNow, hasGateway, gateway, dns, dnsFound, NoProxy,
            vpn ?? new VpnProxyReport(new[] { new VpnProxyFinding(VpnProxyCategory.NoEvidence, "ok") }));
}

public class ProxyReaderTests
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [Fact]
    public void Le_proxy_habilitado_e_pac()
    {
        var reg = new NetTestKit.FakeRegistry();
        reg.Values[(Key, "ProxyEnable")] = (RegKind.DWord, "1");
        reg.Values[(Key, "ProxyServer")] = (RegKind.String, "proxy.exemplo:8080");
        reg.Values[(Key, "AutoConfigURL")] = (RegKind.String, "http://exemplo/proxy.pac");
        var p = ProxyReader.Read(reg);
        Assert.True(p.Readable);
        Assert.True(p.Enabled);
        Assert.Equal("proxy.exemplo:8080", p.Server);
        Assert.Equal("http://exemplo/proxy.pac", p.AutoConfigUrl);
    }

    [Fact]
    public void Proxy_desligado_ignora_o_servidor_salvo()
    {
        var reg = new NetTestKit.FakeRegistry();
        reg.Values[(Key, "ProxyEnable")] = (RegKind.DWord, "0");
        reg.Values[(Key, "ProxyServer")] = (RegKind.String, "antigo:3128");
        var p = ProxyReader.Read(reg);
        Assert.False(p.Enabled);
        Assert.Null(p.Server);
    }

    [Fact]
    public void Leitura_impossivel_fica_inconclusiva()
        => Assert.False(ProxyReader.Read(new NetTestKit.FakeRegistry { Unsupported = true }).Readable);
}

public class VpnProxyDetectorTests
{
    private static VpnProxyReport Eval(ProxySettings? proxy = null, params NetworkAdapterInfo[] adapters)
        => VpnProxyDetector.Evaluate(adapters, proxy ?? NetTestKit.NoProxy);

    [Fact]
    public void Adaptador_de_vpn_ativo_e_apenas_indicio_de_conexao()
    {
        var r = Eval(null, NetTestKit.Adapter("WireGuard", "WireGuard Tunnel", "Ethernet"));
        var f = Assert.Single(r.Findings);
        Assert.Equal(VpnProxyCategory.VpnConnectedLikely, f.Category);
        Assert.Contains("WireGuard", f.Detail);
        Assert.Contains("não confirma", f.Detail);
        Assert.True(r.VpnLikely);
    }

    [Fact]
    public void Adaptador_de_vpn_inativo_e_possivel_sem_confirmacao()
    {
        var r = Eval(null, NetTestKit.Adapter("Ethernet 2", "TAP-Windows Adapter V9", "Ethernet", "Down"));
        Assert.Equal(VpnProxyCategory.VpnPossibleUnconfirmed, Assert.Single(r.Findings).Category);
        Assert.False(r.VpnLikely);
    }

    [Fact]
    public void Tuneis_e_adaptadores_virtuais_comuns_do_windows_nao_sao_vpn()
    {
        var r = Eval(null,
            NetTestKit.Adapter("Teredo Tunneling Pseudo-Interface", "Microsoft Teredo Tunneling Adapter", "Tunnel"),
            NetTestKit.Adapter("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter"),
            NetTestKit.Adapter("Ethernet", "Intel(R) Ethernet Connection"));
        Assert.Equal(VpnProxyCategory.NoEvidence, Assert.Single(r.Findings).Category);
    }

    [Fact]
    public void Miniport_so_indica_vpn_quando_esta_ativo()
    {
        Assert.Equal(VpnProxyCategory.NoEvidence,
            Assert.Single(Eval(null, NetTestKit.Adapter("WAN Miniport (IKEv2)", "WAN Miniport (IKEv2)", "Ppp", "Down")).Findings).Category);
        Assert.Equal(VpnProxyCategory.VpnConnectedLikely,
            Assert.Single(Eval(null, NetTestKit.Adapter("MinhaVPN", "WAN Miniport (IKEv2)", "Ppp", "Up")).Findings).Category);
    }

    [Fact]
    public void Tunel_desconhecido_ativo_e_possivel_vpn_nao_confirmada()
        => Assert.Equal(VpnProxyCategory.VpnPossibleUnconfirmed,
            Assert.Single(Eval(null, NetTestKit.Adapter("Foo", "Adaptador de túnel genérico", "Tunnel")).Findings).Category);

    [Fact]
    public void Proxy_e_pac_geram_achados_proprios()
    {
        var r = Eval(new ProxySettings(true, true, "proxy.exemplo:8080", "http://exemplo/p.pac"));
        Assert.Equal(2, r.Findings.Count(f => f.Category == VpnProxyCategory.ProxyConfigured));
        Assert.Contains(r.Findings, f => f.Detail.Contains("proxy.exemplo:8080"));
    }

    [Fact]
    public void Sem_indicios_e_proxy_ilegivel_fica_inconclusivo()
        => Assert.Equal(VpnProxyCategory.Inconclusive,
            Assert.Single(Eval(new ProxySettings(false, false, null, null)).Findings).Category);
}

public class NetworkEvaluationTests
{
    private static NetworkFinding? Find(IReadOnlyList<NetworkFinding> l, string id) => l.FirstOrDefault(f => f.Id == id);

    [Fact]
    public void Sem_gateway_e_problema_sem_correcao_automatica()
    {
        var l = NetworkDiagnosticService.Evaluate(NetTestKit.Snapshot(hasGateway: false, dnsFound: false));
        var f = Find(l, "no-gateway")!;
        Assert.Equal(FindingSeverity.Problem, f.Severity);
        Assert.Empty(f.SuggestedFixIds);
    }

    [Fact]
    public void Perda_alta_no_gateway_e_problema_e_vem_primeiro_sem_oferecer_comando_de_rede()
    {
        var gw = NetTestKit.Stats(5, null, 5, 5, 5, 5, 5, 5, 5, 5);     // 10%
        var l = NetworkDiagnosticService.Evaluate(NetTestKit.Snapshot(gateway: gw, dns: NetTestKit.Stats(20, 20, 20)));
        Assert.Equal("gateway-loss", l[0].Id);
        Assert.Equal(FindingSeverity.Problem, l[0].Severity);
        Assert.Empty(l[0].SuggestedFixIds);                               // causa local: não há comando que resolva
    }

    [Fact]
    public void Latencia_e_jitter_altos_viram_avisos()
    {
        var gw = NetTestKit.Stats(10, 80, 12, 90, 11);
        var l = NetworkDiagnosticService.Evaluate(NetTestKit.Snapshot(gateway: gw, dns: NetTestKit.Stats(20, 20, 20)));
        Assert.NotNull(Find(l, "gateway-latency"));
        Assert.NotNull(Find(l, "gateway-jitter"));
        Assert.All(l, f => Assert.True(f.Severity <= FindingSeverity.Warning));
    }

    [Fact]
    public void Dns_sem_resposta_sugere_flush_e_depois_dhcp()
    {
        var l = NetworkDiagnosticService.Evaluate(NetTestKit.Snapshot(gateway: NetTestKit.Stats(3, 3, 3, 3, 3),
            dns: NetTestKit.Stats(null, null, null)));
        var f = Find(l, "dns-fail")!;
        Assert.Equal(FindingSeverity.Problem, f.Severity);
        Assert.Equal(new[] { "net.dns-flush", "net.dhcp-renew" }, f.SuggestedFixIds.ToArray());
    }

    [Fact]
    public void Dns_lento_aponta_para_o_comparador_sem_oferecer_correcao()
    {
        var l = NetworkDiagnosticService.Evaluate(NetTestKit.Snapshot(gateway: NetTestKit.Stats(3, 3, 3), dns: NetTestKit.Stats(150, 160, 170)));
        var f = Find(l, "dns-slow")!;
        Assert.Contains("Comparar DNS", f.Evidence);
        Assert.Empty(f.SuggestedFixIds);
    }

    [Fact]
    public void Conexao_saudavel_nao_gera_achados()
        => Assert.Empty(NetworkDiagnosticService.Evaluate(NetTestKit.Snapshot(
            gateway: NetTestKit.Stats(3, 3, 3, 3, 3), dns: NetTestKit.Stats(20, 21, 20))));

    [Fact]
    public void Vpn_e_proxy_aparecem_como_informativos()
    {
        var vpn = new VpnProxyReport(new[]
        {
            new VpnProxyFinding(VpnProxyCategory.VpnConnectedLikely, "Adaptador de VPN ativo: X."),
            new VpnProxyFinding(VpnProxyCategory.ProxyConfigured, "Proxy configurado.")
        });
        var l = NetworkDiagnosticService.Evaluate(NetTestKit.Snapshot(gateway: NetTestKit.Stats(3, 3, 3), dns: NetTestKit.Stats(20, 20, 20), vpn: vpn));
        Assert.Equal(FindingSeverity.Info, Find(l, "vpn")!.Severity);
        Assert.Contains("caminho pela VPN", Find(l, "vpn")!.Evidence);
        Assert.Equal(FindingSeverity.Info, Find(l, "proxy")!.Severity);
    }
}

public class NetworkCaptureAndFixTests
{
    private sealed class ScriptRunner : ICommandRunner
    {
        public int Calls;
        public int ExitCode;
        public Task<CommandResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken ct)
        { Calls++; return Task.FromResult(new CommandResult(ExitCode, "", ExitCode == 0 ? "" : "falha simulada", TimeSpan.FromMilliseconds(5), false)); }
    }

    private sealed class ListLog : IOperationLog
    {
        public List<OperationLogEntry> Entries { get; } = new();
        public Task WriteAsync(OperationLogEntry e, CancellationToken ct = default) { Entries.Add(e); return Task.CompletedTask; }
        public Task<IReadOnlyList<OperationLogEntry>> ReadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OperationLogEntry>>(Entries);
    }

    private sealed class Rig
    {
        public NetTestKit.FakeAdapters Adapters { get; } = new();
        public NetTestKit.FakePing Ping { get; } = new();
        public NetTestKit.FakeDns Dns { get; } = new();
        public NetTestKit.FakeRegistry Registry { get; } = new();
        public ScriptRunner Runner { get; } = new();
        public ListLog Log { get; } = new();
        public NetworkDiagnosticService Diagnostics { get; }
        public NetworkFixService Fixes { get; }

        public Rig(bool elevated = true, bool withAdapter = true)
        {
            if (withAdapter)
                Adapters.Items.Add(NetTestKit.Adapter("Ethernet", "Intel(R) Ethernet", gateways: new[] { "192.168.0.1" }, dns: new[] { "192.168.0.1" }));
            Diagnostics = new NetworkDiagnosticService(Adapters, new PingTestService(Ping), Dns, Registry);
            Fixes = new NetworkFixService(new SystemOperationExecutor(Runner, Log, () => elevated), Diagnostics, TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task Captura_mede_gateway_dns_proxy_e_vpn()
    {
        var rig = new Rig();
        var s = await rig.Diagnostics.CaptureAsync();
        Assert.True(s.HasGateway);
        Assert.Equal(5, s.Gateway!.Sent);
        Assert.Equal(3, s.Dns!.Sent);
        Assert.Equal(20, s.Dns.AvgMs);
        Assert.True(s.Proxy.Readable);
        Assert.Equal(VpnProxyCategory.NoEvidence, Assert.Single(s.VpnProxy.Findings).Category);
    }

    [Fact]
    public async Task Sem_gateway_nao_dispara_nenhuma_sonda()
    {
        var rig = new Rig(withAdapter: false);
        var s = await rig.Diagnostics.CaptureAsync();
        Assert.False(s.HasGateway);
        Assert.Null(s.Gateway);
        Assert.Null(s.Dns);
        Assert.Equal(0, rig.Ping.Calls);
        Assert.Equal(0, rig.Dns.Calls);
    }

    [Fact]
    public async Task Correcao_com_sucesso_compara_antes_e_depois_sem_afirmar_melhora()
    {
        var rig = new Rig();
        var o = await rig.Fixes.ApplyAsync("net.dns-flush", true);
        Assert.Equal(OperationResult.Success, o.Entry.Result);
        Assert.NotNull(o.Before);
        Assert.NotNull(o.After);
        Assert.Contains("→", o.Summary);
        Assert.Contains("não prova melhora", o.Summary);
        Assert.Equal(1, rig.Runner.Calls);
    }

    [Fact]
    public async Task Correcao_que_exige_reinicio_nao_compara()
    {
        var rig = new Rig();
        var o = await rig.Fixes.ApplyAsync("net.winsock-reset", true);
        Assert.Equal(OperationResult.Success, o.Entry.Result);
        Assert.True(o.Entry.RestartRequired);
        Assert.Null(o.Before);
        Assert.Null(o.After);
        Assert.Contains("REINICIAR", o.Summary);
        Assert.Equal(0, rig.Ping.Calls);
    }

    [Fact]
    public async Task Sem_administrador_a_correcao_e_recusada()
    {
        var rig = new Rig(elevated: false);
        var o = await rig.Fixes.ApplyAsync("net.dhcp-renew", true);
        Assert.Equal(OperationResult.Refused, o.Entry.Result);
        Assert.Contains("administrador", o.Summary);
        Assert.Null(o.After);
        Assert.Equal(0, rig.Runner.Calls);
    }

    [Fact]
    public async Task Sem_confirmacao_nada_roda_e_nao_mede_antes()
    {
        var rig = new Rig();
        var o = await rig.Fixes.ApplyAsync("net.dns-flush", false);
        Assert.Equal(OperationResult.Refused, o.Entry.Result);
        Assert.Null(o.Before);
        Assert.Equal(0, rig.Runner.Calls);
        Assert.Equal(0, rig.Ping.Calls);
    }

    [Fact]
    public async Task Falha_do_comando_nao_vira_sucesso()
    {
        var rig = new Rig();
        rig.Runner.ExitCode = 1;
        var o = await rig.Fixes.ApplyAsync("net.dns-flush", true);
        Assert.Equal(OperationResult.Failed, o.Entry.Result);
        Assert.Null(o.After);
        Assert.Contains("não foi concluído", o.Summary);
    }

    [Fact]
    public async Task Correcao_fora_da_lista_lanca()
        => await Assert.ThrowsAsync<ArgumentException>(() => new Rig().Fixes.ApplyAsync("format.c", true));

    [Fact]
    public void Comparacao_avisa_quando_o_indicio_de_vpn_muda()
    {
        var vpnOn = new VpnProxyReport(new[] { new VpnProxyFinding(VpnProxyCategory.VpnConnectedLikely, "x") });
        var text = NetworkFixService.Compare(NetTestKit.Snapshot(gateway: NetTestKit.Stats(3, 3)), NetTestKit.Snapshot(gateway: NetTestKit.Stats(3, 3), vpn: vpnOn));
        Assert.Contains("VPN mudou", text);
    }
}
