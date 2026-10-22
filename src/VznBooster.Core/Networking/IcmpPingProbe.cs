using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VznBooster.Core.Networking;

/// <summary>
/// Ping ICMP real. Limitações: a API do .NET reporta o RTT em milissegundos inteiros (0 significa &lt; 1 ms)
/// e um timeout pode significar perda OU filtragem de ICMP pelo destino — o ICMP sozinho não distingue os dois casos.
/// </summary>
public sealed class IcmpPingProbe : IPingProbe
{
    public async Task<ProbeResult> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ct.ThrowIfCancellationRequested();
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host, (int)timeout.TotalMilliseconds).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success) return new ProbeResult(ProbeStatus.Reply, reply.RoundtripTime);
            if (reply.Status == IPStatus.TimedOut) return new ProbeResult(ProbeStatus.TimedOut, null);
            return reply.Status.ToString().StartsWith("Destination", StringComparison.Ordinal)
                ? new ProbeResult(ProbeStatus.Unreachable, null)
                : new ProbeResult(ProbeStatus.Error, null);
        }
        catch (PingException ex) when (ex.InnerException is SocketException)
        {
            return new ProbeResult(ProbeStatus.NameResolutionFailed, null);
        }
        catch (PingException)
        {
            return new ProbeResult(ProbeStatus.Error, null);
        }
    }
}
