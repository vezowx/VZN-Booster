namespace VznBooster.Core.Networking;

public sealed record PingTestResult(
    string Target, DateTimeOffset StartedUtc, int Samples, LatencyStats Stats,
    IReadOnlyList<ProbeStatus> Statuses, MeasurementKind Kind, string Method);

/// <summary>
/// Mede a latência até UM destino. O resultado é a latência até aquele destino, nunca o "ping de um jogo".
/// Cancelamento lança OperationCanceledException (sem resultado parcial).
/// </summary>
public sealed class PingTestService
{
    public const int MaxSamples = 100;
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(10);
    private readonly IPingProbe _probe;

    public PingTestService(IPingProbe probe) => _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public async Task<PingTestResult> RunAsync(string target, int samples, TimeSpan interval, TimeSpan timeout,
        CancellationToken ct = default)
    {
        target = (target ?? string.Empty).Trim();
        if (Uri.CheckHostName(target) == UriHostNameType.Unknown)
            throw new ArgumentException("Destino inválido: informe um nome de host ou endereço IP.", nameof(target));
        if (samples < 1 || samples > MaxSamples)
            throw new ArgumentOutOfRangeException(nameof(samples), $"Use de 1 a {MaxSamples} amostras.");
        if (interval < TimeSpan.Zero || interval > MaxInterval)
            throw new ArgumentOutOfRangeException(nameof(interval), "Intervalo deve estar entre 0 e 5 segundos.");
        if (timeout < MinTimeout || timeout > MaxTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout deve estar entre 100 ms e 10 s.");

        var started = DateTimeOffset.UtcNow;
        var rtts = new List<double?>(samples);
        var statuses = new List<ProbeStatus>(samples);
        for (var i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = await _probe.ProbeAsync(target, timeout, ct).ConfigureAwait(false);
            statuses.Add(r.Status);
            rtts.Add(r.Status == ProbeStatus.Reply ? r.RttMs : null);
            if (i < samples - 1 && interval > TimeSpan.Zero)
                await Task.Delay(interval, ct).ConfigureAwait(false);
        }

        var stats = LatencyStats.FromSamples(rtts);
        var kind = stats.Received > 0 ? MeasurementKind.Measured : MeasurementKind.Unavailable;
        return new PingTestResult(target, started, samples, stats, statuses, kind, $"ICMP echo até {target}");
    }
}
