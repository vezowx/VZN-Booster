using System.Security;
using VznBooster.Core.Tweaker;

namespace VznBooster.Core.Networking;

public sealed record ProxySettings(bool Readable, bool Enabled, string? Server, string? AutoConfigUrl);

/// <summary>Leitura somente-leitura das configurações de proxy do Windows (HKCU, Internet Settings).</summary>
public static class ProxyReader
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static ProxySettings Read(IRegistryAccessor registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        try
        {
            var enable = registry.Read(Key, "ProxyEnable");
            var server = registry.Read(Key, "ProxyServer");
            var pac = registry.Read(Key, "AutoConfigURL");
            var enabled = enable.Exists && enable.Kind == RegKind.DWord && enable.Value == "1";
            return new ProxySettings(true, enabled, enabled && server.Exists ? server.Value : null, pac.Exists ? pac.Value : null);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException)
        {
            return new ProxySettings(false, false, null, null);
        }
    }
}

public enum VpnProxyCategory { VpnConnectedLikely, VpnPossibleUnconfirmed, ProxyConfigured, NoEvidence, Inconclusive }

public sealed record VpnProxyFinding(VpnProxyCategory Category, string Detail);

public sealed record VpnProxyReport(IReadOnlyList<VpnProxyFinding> Findings)
{
    public bool VpnLikely => Findings.Any(f => f.Category == VpnProxyCategory.VpnConnectedLikely);
}

/// <summary>
/// Procura INDÍCIOS de VPN e proxy; nunca afirma que uma VPN está definitivamente ativa. Um adaptador "Up" com nome de VPN é indício de
/// conexão, não prova de que o tráfego passa por ela. Não tenta contornar VPNs, redes corporativas ou regras de segurança.
/// </summary>
public static class VpnProxyDetector
{
    private static readonly string[] VpnKeywords =
    {
        "wireguard", "wintun", "nordlynx", "openvpn", "tap-windows", "tap-win", "ovpn", "vpn", "anyconnect", "forticlient", "fortinet",
        "globalprotect", "pangp", "tailscale", "zerotier", "protonvpn", "mullvad", "expressvpn", "surfshark", "cloudflare warp", "warp",
        "hamachi", "softether"
    };

    // Adaptadores "WAN Miniport" existem sempre no Windows; só indicam VPN se estiverem ATIVOS.
    private static readonly string[] MiniportKeywords = { "miniport (pptp)", "miniport (l2tp)", "miniport (sstp)", "miniport (ikev2)" };

    // Túneis normais do Windows (IPv6 sobre IPv4) que não são VPN.
    private static readonly string[] NotVpn = { "teredo", "isatap", "6to4", "ip-https" };

    public static VpnProxyReport Evaluate(IReadOnlyList<NetworkAdapterInfo> adapters, ProxySettings proxy)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(proxy);
        var findings = new List<VpnProxyFinding>();

        foreach (var a in adapters)
        {
            var text = $"{a.Name} {a.Description}".ToLowerInvariant();
            if (NotVpn.Any(text.Contains)) continue;
            var up = string.Equals(a.Status, "Up", StringComparison.OrdinalIgnoreCase);

            if (MiniportKeywords.Any(text.Contains))
            {
                if (up) findings.Add(new VpnProxyFinding(VpnProxyCategory.VpnConnectedLikely,
                    $"Adaptador de VPN do Windows ativo: {a.Name}. Indício de conexão; não confirma que o tráfego passa pela VPN."));
                continue;
            }

            if (VpnKeywords.Any(text.Contains))
            {
                findings.Add(up
                    ? new VpnProxyFinding(VpnProxyCategory.VpnConnectedLikely,
                        $"Adaptador de VPN ativo: {a.Name}. Indício de conexão; não confirma que o tráfego passa pela VPN.")
                    : new VpnProxyFinding(VpnProxyCategory.VpnPossibleUnconfirmed,
                        $"Adaptador associado a VPN encontrado, mas não ativo: {a.Name}. A conexão não está confirmada."));
                continue;
            }

            if (up && a.Type is "Tunnel" or "Ppp")
                findings.Add(new VpnProxyFinding(VpnProxyCategory.VpnPossibleUnconfirmed,
                    $"Adaptador de túnel ativo ({a.Type}): {a.Name}. Pode ser uma VPN, mas isso não está confirmado."));
        }

        if (proxy.Enabled)
            findings.Add(new VpnProxyFinding(VpnProxyCategory.ProxyConfigured,
                $"Proxy configurado no Windows (servidor: {proxy.Server ?? "não informado"})."));
        if (!string.IsNullOrWhiteSpace(proxy.AutoConfigUrl))
            findings.Add(new VpnProxyFinding(VpnProxyCategory.ProxyConfigured,
                $"Script de configuração automática de proxy (PAC) definido: {proxy.AutoConfigUrl}"));

        if (findings.Count == 0)
            findings.Add(proxy.Readable
                ? new VpnProxyFinding(VpnProxyCategory.NoEvidence, "Nenhum indício de VPN ou proxy encontrado nos adaptadores e nas configurações lidas.")
                : new VpnProxyFinding(VpnProxyCategory.Inconclusive,
                    "Estado inconclusivo: não foi possível ler as configurações de proxy do Windows, e nenhum adaptador de VPN foi identificado."));
        return new VpnProxyReport(findings);
    }
}
