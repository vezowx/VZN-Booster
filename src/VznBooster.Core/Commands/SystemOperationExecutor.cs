using System.ComponentModel;
using System.Diagnostics;
using VznBooster.Core.Logging;

namespace VznBooster.Core.Commands;

/// <summary>
/// Único ponto de execução de operações de sistema: só roda itens do catálogo, exige confirmação
/// para qualquer coisa que não seja leitura, verifica elevação e registra TODO resultado (inclusive recusas).
/// Não eleva privilégios sozinho: se o app não estiver elevado, a operação é recusada e registrada.
/// </summary>
public sealed class SystemOperationExecutor
{
    private readonly ICommandRunner _runner;
    private readonly IOperationLog _log;
    private readonly Func<bool> _isElevated;

    public SystemOperationExecutor(ICommandRunner runner, IOperationLog log, Func<bool>? isElevated = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _isElevated = isElevated ?? ProcessElevation.IsProcessElevated;
    }

    public Task<OperationLogEntry> ExecuteAsync(
        string operationId, bool userConfirmed, string? backupId = null, CancellationToken ct = default)
    {
        if (!OperationCatalog.TryGet(operationId, out var op))
            throw new ArgumentException($"Operação não permitida: {operationId}", nameof(operationId));
        return ExecuteCoreAsync(op, userConfirmed, backupId, ct);
    }

    public Task<OperationLogEntry> ExecuteAsync(
        ValidatedOperation operation, bool userConfirmed, string? backupId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteCoreAsync(operation.Operation, userConfirmed, backupId, ct);
    }

    private async Task<OperationLogEntry> ExecuteCoreAsync(
        AllowedOperation op, bool userConfirmed, string? backupId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var start = DateTimeOffset.UtcNow;

        if (op.Risk != RiskLevel.ReadOnly && !userConfirmed)
            return await FinishAsync(Build(op, start, sw.Elapsed, OperationResult.Refused, null, null,
                "Confirmação explícita do usuário ausente.", backupId)).ConfigureAwait(false);

        if (op.RequiresAdmin && !_isElevated())
            return await FinishAsync(Build(op, start, sw.Elapsed, OperationResult.Refused, null, null,
                "Requer privilégios de administrador; o aplicativo não está elevado.", backupId)).ConfigureAwait(false);

        try
        {
            var r = await _runner.RunAsync(op.FileName, op.Arguments, op.Timeout, ct).ConfigureAwait(false);
            var ok = r.ExitCode == 0 && !r.TimedOut;
            var err = r.TimedOut ? (r.StdErr + " [tempo limite excedido]").Trim() : r.StdErr;
            return await FinishAsync(Build(op, start, r.Duration, ok ? OperationResult.Success : OperationResult.Failed,
                r.ExitCode, r.StdOut, err, backupId)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await FinishAsync(Build(op, start, sw.Elapsed, OperationResult.Cancelled, null, null,
                "Operação cancelada.", backupId)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return await FinishAsync(Build(op, start, sw.Elapsed, OperationResult.Failed, null, null,
                $"Falha ao iniciar o processo: {ex.Message}", backupId)).ConfigureAwait(false);
        }
    }

    private static OperationLogEntry Build(AllowedOperation op, DateTimeOffset start, TimeSpan duration,
        OperationResult result, int? exit, string? stdout, string? stderr, string? backupId) =>
        new(Guid.NewGuid(), start, op.Category, op.Id, op.FileName, op.Arguments, exit, stdout, stderr,
            duration, result, op.RequiresRestart && result == OperationResult.Success, backupId, false);

    private async Task<OperationLogEntry> FinishAsync(OperationLogEntry entry)
    {
        var redacted = entry.RedactSensitiveData();
        await _log.WriteAsync(redacted, CancellationToken.None).ConfigureAwait(false);
        return redacted;
    }
}
