using VznBooster.Core.Commands;
using VznBooster.Core.Logging;

namespace VznBooster.Core.Restore;

public sealed record RestorePointOutcome(bool Success, string Message);

/// <summary>
/// Cria um ponto de restauração do Windows (Checkpoint-Computer) e VERIFICA contando os pontos "VZN Booster" antes e depois:
/// o Windows pode concluir o comando sem criar nada (limite de um ponto a cada 24 horas) e isso nunca é declarado como sucesso.
/// Exige administrador (o executor recusa e registra quando o aplicativo não está elevado).
/// </summary>
public sealed class RestorePointService
{
    private const string AdminMessage =
        "Criar pontos de restauração exige administrador. Reinicie o VZN Booster como administrador (Configurações → Permissões) e tente de novo.";

    private readonly SystemOperationExecutor _executor;

    public RestorePointService(SystemOperationExecutor executor) => _executor = executor ?? throw new ArgumentNullException(nameof(executor));

    public async Task<RestorePointOutcome> CreateAsync(bool userConfirmed, CancellationToken ct = default)
    {
        if (!userConfirmed) return new(false, "Confirmação explícita ausente; nada foi criado.");

        var (before, beforeError) = await CountAsync(ct).ConfigureAwait(false);
        if (beforeError is not null) return new(false, beforeError);

        var create = await _executor.ExecuteAsync("restorepoint.create", true, null, ct).ConfigureAwait(false);
        if (create.Result == OperationResult.Refused) return new(false, AdminMessage);
        if (create.Result != OperationResult.Success)
            return new(false, $"O Windows não criou o ponto de restauração ({create.Result}). {Short(create.StdErr)} " +
                              "Verifique se a Proteção do Sistema está ativada para a unidade do Windows.".Trim());

        var (after, afterError) = await CountAsync(ct).ConfigureAwait(false);
        if (afterError is not null)
            return new(false, "O comando foi concluído, mas não foi possível verificar se o ponto foi criado. " + afterError);

        return after > before
            ? new(true, "Ponto de restauração criado e verificado.")
            : new(false, "O comando foi concluído, mas nenhum novo ponto apareceu na lista. O Windows costuma limitar a criação a um ponto a cada 24 horas, " +
                         "ou a Proteção do Sistema pode estar desativada.");
    }

    private async Task<(int Count, string? Error)> CountAsync(CancellationToken ct)
    {
        var e = await _executor.ExecuteAsync("restorepoint.count", false, null, ct).ConfigureAwait(false);
        if (e.Result == OperationResult.Refused) return (0, AdminMessage);
        if (e.Result != OperationResult.Success || !int.TryParse((e.StdOut ?? string.Empty).Trim(), out var n))
            return (0, "Não foi possível consultar os pontos de restauração (PowerShell indisponível ou sem acesso); nada foi criado.");
        return (n, null);
    }

    private static string Short(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= 200 ? t : t[..200] + "…";
    }
}
