using System.Net.NetworkInformation;

namespace VznBooster.Core.Networking;

public sealed record NetworkAdapterInfo(
    string Id, string Name, string Description, string Type, string Status,
    double? LinkSpeedMbps, IReadOnlyList<string> DnsServers, IReadOnlyList<string> Gateways,
    int? InterfaceIndex, DnsConfigSource DnsSource, IReadOnlyList<string> StaticDnsServers);

public interface IAdapterSource
{
    IReadOnlyList<NetworkAdapterInfo> ReadAll();
}

public sealed class SystemAdapterSource : IAdapterSource
{
    public IReadOnlyList<NetworkAdapterInfo> ReadAll() => NetworkAdapterReader.ReadAll();
}

/// <summary>Leitura somente-leitura dos adaptadores via API gerenciada. Lista vazia pode significar "nenhum" ou "não disponível".</summary>
public static class NetworkAdapterReader
{
    public static IReadOnlyList<NetworkAdapterInfo> ReadAll()
    {
        var list = new List<NetworkAdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var dns = new List<string>();
            var gateways = new List<string>();
            int? index = null;
            try
            {
                var props = ni.GetIPProperties();
                dns.AddRange(props.DnsAddresses.Select(a => a.ToString()));
                gateways.AddRange(props.GatewayAddresses.Select(g => g.Address.ToString()));
                index = props.GetIPv4Properties().Index;
            }
            catch (NetworkInformationException) { /* IPv4 ou propriedades indisponíveis para este adaptador */ }

            var (source, statics) = DnsConfigReader.Read(ni.Id);
            double? speed = ni.Speed > 0 ? ni.Speed / 1_000_000.0 : null;
            list.Add(new NetworkAdapterInfo(ni.Id, ni.Name, ni.Description, ni.NetworkInterfaceType.ToString(),
                ni.OperationalStatus.ToString(), speed, dns, gateways, index, source, statics));
        }
        return list;
    }
}
