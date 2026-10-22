using VznBooster.Core.Commands;
using VznBooster.Core.Logging;

namespace VznBooster.Core.Networking;

public sealed record FixOutcome(OperationLogEntry Entry, ConnectionSnapshot? Before, ConnectionSnapshot? After, string Summary);

/// <summary>
/// Aplica UMA correção de conectividade do catálogo (cache DNS, DHCP, Winsock, IP), medindo a conexão antes e depois quando isso faz sentido.
/// O sucesso do comando nunca é apresentado como melhora: só os números medidos, com o aviso de que variação normal pode explicá-los.
/// Correções que exigem reinicialização não têm comparação (o "depois" só vale depois de reiniciar).
/// </summary>
public sealed class NetworkFixService
{
    public static readonly IReadOnlyList<string> FixIds = new[] { "net.dns-flush", "net.dhcp-renew", "net.winsock-reset", "net.ip-reset" };

    private readonly SystemOperationExecutor _executor;
    private readonly NetworkDiagnosticService _diagnostics;
    private readonly TimeSpan _settle;

    public NetworkFixService(SystemOperationExecutor executor, NetworkDiagnosticService diagnostics, TimeSpan? settle = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _settle = settle ?? TimeSpan.FromSeconds(4);
    }

    public async Task<FixOutcome> ApplyAsync(string fixId, bool userConfirmed, CancellationToken ct = default)
    {
        if (!FixIds.Contains(fixId) || !OperationCatalog.TryGet(fixId, out var op))
            throw new ArgumentException($"Correção desconhecida: {fixId}", nameof(fixId));

        ConnectionSnapshot? before = null;
        if (userConfirmed && !op.RequiresRestart) before = await _diagnostics.CaptureAsync(ct).ConfigureAwait(false);

        var entry = await _executor.ExecuteAsync(fixId, userConfirmed, null, ct).ConfigureAwait(false);
        if (entry.Result != OperationResult.Success)
            return new FixOutcome(entry, before, null, entry.Result switch
            {
                OperationResult.Refused => entry.StdErr ?? "A operação foi recusada.",
                OperationResult.Cancelled => "A operação foi cancelada.",
                _ => $"O comando não foi concluído com sucesso. {entry.StdErr}".Trim()
            });

        if (op.RequiresRestart)
            return new FixOutcome(entry, null, null,
                "O comando foi concluído. Esta correção só tem efeito completo depois de REINICIAR o computador, por isso não há comparação antes/depois agora.");

        await Task.Delay(_settle, ct).ConfigureAwait(false);
        var after = await _diagnostics.CaptureAsync(ct).ConfigureAwait(false);
        return new FixOutcome(entry, before, after, Compare(before, after));
    }

    public static string Compare(ConnectionSnapshot? before, ConnectionSnapshot? after)
    {
        const string caveat = "O comando ter terminado com sucesso não prova melhora. Diferenças pequenas podem ser variação normal entre medições (poucas amostras, horários diferentes); " +
                              "o aplicativo não atribui causa a elas.";
        if (before is null || after is null) return "Não foi possível comparar antes e depois. " + caveat;

        static string Ms(double? v) => v is null ? "indisponível" : $"{v:0.0} ms";
        static string Loss(Networking.LatencyStats? s) => s is null ? "indisponível" : $"{s.LossPercent:0.#}% ({s.Received}/{s.Sent})";

        var lines = new List<string>
        {
            $"Gateway — latência média: {Ms(before.Gateway?.AvgMs)} → {Ms(after.Gateway?.AvgMs)}; jitter: {Ms(before.Gateway?.JitterMs)} → {Ms(after.Gateway?.JitterMs)}; perda: {Loss(before.Gateway)} → {Loss(after.Gateway)}.",
            $"DNS configurado — tempo médio: {Ms(before.Dns?.AvgMs)} → {Ms(after.Dns?.AvgMs)}; respostas: {Loss(before.Dns)} → {Loss(after.Dns)}.",
            $"Medições: antes às {before.CapturedUtc.ToLocalTime():HH:mm:ss}, depois às {after.CapturedUtc.ToLocalTime():HH:mm:ss}.",
        };
        if (before.VpnProxy.VpnLikely != after.VpnProxy.VpnLikely)
            lines.Add("Atenção: o indício de VPN mudou entre as medições, o que compromete a comparação.");
        lines.Add(caveat);
        return string.Join("\n", lines);
    }
}
