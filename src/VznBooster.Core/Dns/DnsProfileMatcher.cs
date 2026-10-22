using System.Net;

namespace VznBooster.Core.Dns;

public static class DnsProfileMatcher
{
    /// <summary>Retorna o perfil conhecido se TODOS os endereços informados pertencem a ele; senão null.</summary>
    public static DnsProfile? Identify(IEnumerable<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var parsed = new List<string>();
        foreach (var a in addresses)
        {
            if (!IPAddress.TryParse(a, out var ip)) return null;
            parsed.Add(ip.ToString());
        }
        if (parsed.Count == 0) return null;

        foreach (var p in DnsProfiles.All)
        {
            var set = p.IPv4.Concat(p.IPv6).Select(x => IPAddress.Parse(x).ToString()).ToHashSet();
            if (parsed.All(set.Contains)) return p;
        }
        return null;
    }
}
