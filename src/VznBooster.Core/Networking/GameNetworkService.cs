namespace VznBooster.Core.Networking;

public sealed record GameNetworkResult(
    GameProfile Game, GameRegion Region, DateTimeOffset StartedUtc, int Samples, LatencyStats Stats,
    MeasurementKind Kind, string Method, string Confidence, IReadOnlyList<string> Notes);

/// <summary>
/// Estimativa INDIRETA de proximidade para jogos. Mede o tempo de handshake TCP até um endpoint regional público da AWS; isso NÃO é o ping da partida.
/// O resultado nunca é classificado como "Medido": é "Estimado" (com confiança baixa) ou "Indisponível".
/// </summary>
public sealed class GameNetworkService
{
    public const int MaxSamples = 30;
    private readonly ITcpProbe _probe;

    public GameNetworkService(ITcpProbe probe) => _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    public async Task<GameNetworkResult> RunAsync(GameProfile game, GameRegion region, int samples, TimeSpan interval, TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(region);
        if (!game.Regions.Contains(region)) throw new ArgumentException("A região não pertence ao jogo informado.", nameof(region));
        if (samples < 1 || samples > MaxSamples) throw new ArgumentOutOfRangeException(nameof(samples), $"Use de 1 a {MaxSamples} amostras.");
        if (interval < TimeSpan.Zero || interval > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(interval));
        if (timeout < TimeSpan.FromMilliseconds(200) || timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));

        var started = DateTimeOffset.UtcNow;
        var rtts = new List<double?>(samples);
        for (var i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = await _probe.ConnectAsync(region.Host, GameCatalog.Port, timeout, ct).ConfigureAwait(false);
            rtts.Add(r.Status == TcpProbeStatus.Connected ? r.Ms : null);
            if (i < samples - 1 && interval > TimeSpan.Zero) await Task.Delay(interval, ct).ConfigureAwait(false);
        }

        var stats = LatencyStats.FromSamples(rtts);
        var kind = stats.Received > 0 ? MeasurementKind.Estimated : MeasurementKind.Unavailable;
        var notes = new List<string>
        {
            game.Disclaimer,
            $"O destino medido é um endpoint regional público da AWS ({region.Host}), usado só como referência de proximidade: {region.Reference}.",
            "O ping real da partida pode ser diferente: depende do servidor escolhido pelo jogo, do roteamento, do congestionamento, do Wi-Fi e do provedor."
        };
        return new GameNetworkResult(game, region, started, samples, stats, kind,
            $"Handshake TCP (porta {GameCatalog.Port}) até o endpoint regional da AWS — ESTIMATIVA INDIRETA", "Baixa", notes);
    }
}
