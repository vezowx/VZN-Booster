using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace VznBooster.Core.Dns;

public enum DnsProbeStatus { Answered, TimedOut, ServerFailure, Error }

public sealed record DnsProbeResult(DnsProbeStatus Status, double? Ms, int AnswerCount, int RCode);

public interface IDnsProbe
{
    Task<DnsProbeResult> QueryAsync(string server, string name, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Consulta DNS real (UDP/53, IPv4) a um servidor específico, medindo o tempo até a resposta. NXDOMAIN conta como resposta válida;
/// SERVFAIL/REFUSED contam como falha do servidor. O tempo inclui o cache do resolvedor (consultas repetidas tendem a ser mais rápidas).
/// </summary>
public sealed class UdpDnsProbe : IDnsProbe
{
    public async Task<DnsProbeResult> QueryAsync(string server, string name, TimeSpan timeout, CancellationToken ct)
    {
        if (!IPAddress.TryParse(server, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("O servidor DNS deve ser um endereço IPv4.", nameof(server));

        var id = (ushort)RandomNumberGenerator.GetInt32(65536);
        var query = DnsWire.BuildQuery(name, id);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            udp.Connect(ip, 53);
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(query, cts.Token).ConfigureAwait(false);
            while (true)
            {
                var received = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var response = DnsWire.Parse(received.Buffer, id);
                if (!response.Valid) continue;      // pacote estranho ou atrasado: continua esperando até o tempo limite
                sw.Stop();
                return response.RCode is 0 or 3
                    ? new DnsProbeResult(DnsProbeStatus.Answered, sw.Elapsed.TotalMilliseconds, response.AnswerCount, response.RCode)
                    : new DnsProbeResult(DnsProbeStatus.ServerFailure, sw.Elapsed.TotalMilliseconds, response.AnswerCount, response.RCode);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DnsProbeResult(DnsProbeStatus.TimedOut, null, 0, -1);
        }
        catch (SocketException)
        {
            return new DnsProbeResult(DnsProbeStatus.Error, null, 0, -1);
        }
    }
}
