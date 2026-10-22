using System.Net;
using System.Net.Sockets;
using VznBooster.Core.Networking;

namespace VznBooster.Core.Dns;

public sealed record DnsCandidate(string Id, string Name, string Server);

public sealed record DnsProviderResult(DnsCandidate Candidate, LatencyStats Stats, int Failures, int Queries);

public sealed record DnsComparison(
    IReadOnlyList<DnsProviderResult> Results, string? BestId, string Verdict, MeasurementKind Kind, DateTimeOffset StartedUtc);

/// <summary>
/// Compara o tempo de resolução DNS medido NESTA conexão e NESTE momento. Só aponta um vencedor quando a diferença é
/// significativa (≥ 3 ms e ≥ 20% sobre o segundo colocado); caso contrário informa que não há diferença significativa.
/// </summary>
public sealed class DnsComparer
{
    public static readonly IReadOnlyList<string> Domains = new[] { "example.com", "wikipedia.org", "iana.org", "mozilla.org" };
    public const int MaxCandidates = 8;
    public const double MinAbsoluteDiffMs = 3;
    public const double MinRelativeDiff = 0.20;
    private const string Caveat = " Menor tempo de resolução de nomes não significa menor ping dentro de um jogo.";

    private readonly IDnsProbe _probe;

    public DnsComparer(IDnsProbe probe) => _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public async Task<DnsComparison> RunAsync(IReadOnlyList<DnsCandidate> candidates, int rounds, TimeSpan timeout,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 || candidates.Count > MaxCandidates)
            throw new ArgumentException($"Informe de 1 a {MaxCandidates} servidores.", nameof(candidates));
        if (rounds < 1 || rounds > 5) throw new ArgumentOutOfRangeException(nameof(rounds), "Use de 1 a 5 rodadas.");
        if (timeout < TimeSpan.FromMilliseconds(200) || timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout deve estar entre 200 ms e 10 s.");
        foreach (var c in candidates)
            if (!IPAddress.TryParse(c.Server, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException($"Servidor inválido para {c.Name}: use um endereço IPv4.", nameof(candidates));

        var started = DateTimeOffset.UtcNow;
        var results = new List<DnsProviderResult>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(candidate.Name);
            var samples = new List<double?>();
            var failures = 0;
            for (var r = 0; r < rounds; r++)
                foreach (var domain in Domains)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = await _probe.QueryAsync(candidate.Server, domain, timeout, ct).ConfigureAwait(false);
                    if (p.Status == DnsProbeStatus.Answered && p.Ms is { } ms) samples.Add(ms);
                    else { samples.Add(null); failures++; }
                }
            results.Add(new DnsProviderResult(candidate, LatencyStats.FromSamples(samples), failures, samples.Count));
        }

        var (best, verdict) = Judge(results);
        var kind = results.Any(r => r.Stats.Received > 0) ? MeasurementKind.Measured : MeasurementKind.Unavailable;
        return new DnsComparison(results, best, verdict, kind, started);
    }

    public static (string? BestId, string Verdict) Judge(IReadOnlyList<DnsProviderResult> results)
    {
        var eligible = results
            .Where(r => r.Stats.AvgMs is not null && r.Stats.Received * 2 >= r.Queries)
            .OrderBy(r => r.Stats.AvgMs!.Value)
            .ToList();
        if (eligible.Count < 2)
            return (null, "Dados insuficientes para comparar: menos de dois servidores responderam à maioria das consultas." + Caveat);

        var best = eligible[0];
        var second = eligible[1];
        var diff = second.Stats.AvgMs!.Value - best.Stats.AvgMs!.Value;
        var relative = second.Stats.AvgMs.Value > 0 ? diff / second.Stats.AvgMs.Value : 0;
        if (diff >= MinAbsoluteDiffMs && relative >= MinRelativeDiff)
            return (best.Candidate.Id,
                $"{best.Candidate.Name} respondeu mais rápido neste momento ({best.Stats.AvgMs.Value:0.0} ms de média contra {second.Stats.AvgMs.Value:0.0} ms do segundo colocado)." + Caveat);
        return (null, "Sem diferença significativa entre os servidores testados (critério: pelo menos 3 ms e 20% sobre o segundo colocado)." + Caveat);
    }
}
