namespace VznBooster.Core.Dns;

/// <param name="VerifiedAgainstOfficialSource">
/// Marcado manualmente somente após conferir os endereços na documentação oficial do provedor.
/// </param>
public sealed record DnsProfile(
    string Id, string Name, string Variant,
    IReadOnlyList<string> IPv4, IReadOnlyList<string> IPv6,
    string DocumentationUrl, bool VerifiedAgainstOfficialSource);

/// <summary>
/// Perfis conhecidos. Control D foi deliberadamente omitido: seus endereços variam por modalidade
/// e ainda precisam ser conferidos na documentação oficial (https://controld.com/free-dns).
/// </summary>
public static class DnsProfiles
{
    public static IReadOnlyList<DnsProfile> All { get; } = new[]
    {
        new DnsProfile("cloudflare", "Cloudflare", "Padrão (sem filtro)",
            new[] { "1.1.1.1", "1.0.0.1" }, new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" },
            "https://developers.cloudflare.com/1.1.1.1/ip-addresses/", false),
        new DnsProfile("google", "Google Public DNS", "Padrão",
            new[] { "8.8.8.8", "8.8.4.4" }, new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" },
            "https://developers.google.com/speed/public-dns/docs/using", false),
        new DnsProfile("quad9", "Quad9", "Recomendado (bloqueio de malware, DNSSEC)",
            new[] { "9.9.9.9", "149.112.112.112" }, new[] { "2620:fe::fe", "2620:fe::9" },
            "https://docs.quad9.net/services/", false),
        new DnsProfile("adguard", "AdGuard DNS", "Padrão (bloqueia anúncios e rastreadores)",
            new[] { "94.140.14.14", "94.140.15.15" }, new[] { "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff" },
            "https://adguard-dns.io/en/public-dns.html", false),
    };
}
