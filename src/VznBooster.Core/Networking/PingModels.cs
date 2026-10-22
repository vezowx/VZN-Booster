namespace VznBooster.Core.Networking;

public enum ProbeStatus { Reply, TimedOut, Unreachable, NameResolutionFailed, Error }

public sealed record ProbeResult(ProbeStatus Status, double? RttMs);

public interface IPingProbe
{
    Task<ProbeResult> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct);
}
