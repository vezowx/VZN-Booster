using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace VznBooster.Core.Networking;

public enum TcpProbeStatus { Connected, TimedOut, Refused, NameResolutionFailed, Error }

public sealed record TcpProbeResult(TcpProbeStatus Status, double? Ms);

public interface ITcpProbe
{
    Task<TcpProbeResult> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Tempo do handshake TCP (SYN → SYN/ACK) até host:porta. A resolução de nome NÃO entra na medida. Não envia dados da aplicação:
/// a conexão é encerrada logo após estabelecida.
/// </summary>
public sealed class SocketTcpProbe : ITcpProbe
{
    public async Task<TcpProbeResult> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        IPAddress[] addresses;
        try { addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false); }
        catch (SocketException) { return new TcpProbeResult(TcpProbeStatus.NameResolutionFailed, null); }

        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (ip is null) return new TcpProbeResult(TcpProbeStatus.NameResolutionFailed, null);

        using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return new TcpProbeResult(TcpProbeStatus.Connected, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TcpProbeResult(TcpProbeStatus.TimedOut, null);
        }
        catch (SocketException ex)
        {
            return new TcpProbeResult(ex.SocketErrorCode == SocketError.ConnectionRefused ? TcpProbeStatus.Refused : TcpProbeStatus.Error, null);
        }
    }
}
