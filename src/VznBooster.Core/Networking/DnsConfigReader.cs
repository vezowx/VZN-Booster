using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VznBooster.Core.Networking;

public enum DnsConfigSource { Automatic, Static, Unknown }

/// <summary>
/// Descobre se o DNS IPv4 de um adaptador é automático (DHCP) ou manual, lendo (somente leitura) o valor
/// "NameServer" da chave Tcpip\Parameters\Interfaces\{GUID}. É uma heurística baseada no registro, não uma API
/// documentada: se algo falhar, o resultado é Unknown e o aplicativo se recusa a alterar o DNS.
/// </summary>
public static class DnsConfigReader
{
    private static readonly Regex GuidPattern = new(@"^\{[0-9A-Fa-f\-]{36}\}$", RegexOptions.Compiled);

    public static (DnsConfigSource Source, IReadOnlyList<string> Servers) Read(string adapterId)
    {
        var none = Array.Empty<string>();
        if (!OperatingSystem.IsWindows()) return (DnsConfigSource.Unknown, none);
        if (adapterId is null || !GuidPattern.IsMatch(adapterId)) return (DnsConfigSource.Unknown, none);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + adapterId);
            if (key is null) return (DnsConfigSource.Unknown, none);
            var raw = key.GetValue("NameServer") as string;
            if (string.IsNullOrWhiteSpace(raw)) return (DnsConfigSource.Automatic, none);

            var servers = new List<string>();
            foreach (var part in raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (IPAddress.TryParse(part, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    servers.Add(ip.ToString());
            return servers.Count > 0 ? (DnsConfigSource.Static, servers) : (DnsConfigSource.Unknown, none);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return (DnsConfigSource.Unknown, none);
        }
    }
}
