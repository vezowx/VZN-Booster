using System.Diagnostics;
using System.Security;
using VznBooster.Core.Backup;
using VznBooster.Core.Logging;
using VznBooster.Core.Tweaker;

namespace VznBooster.Core.Startup;

/// <summary>
/// Ativa/desativa entradas de inicialização do USUÁRIO pelo mesmo mecanismo do Gerenciador de Tarefas (StartupApproved): nada é apagado e a entrada
/// continua existindo. Fluxo: backup do valor anterior → alteração → leitura e verificação → log. Reversão com detecção de conflito.
/// Só atua em entradas que a enumeração atual encontra e que sejam alteráveis (HKCU e pasta Inicializar do usuário).
/// </summary>
public sealed class StartupEngine
{
    public const string BackupOperation = "startup.change";

    private readonly IStartupRegistry _registry;
    private readonly StartupEnumerator _enumerator;
    private readonly BackupStore _backups;
    private readonly IOperationLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StartupEngine(IStartupRegistry registry, StartupEnumerator enumerator, BackupStore backups, IOperationLog log)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IReadOnlyList<StartupEntry> Enumerate() => _enumerator.Enumerate();

    private static string? ApprovedPathFor(StartupSource s) => s switch
    {
        StartupSource.CurrentUserRun => StartupEnumerator.ApprovedRunPath,
        StartupSource.UserStartupFolder => StartupEnumerator.ApprovedFolderPath,
        _ => null
    };

    private static string ValueNameOf(StartupEntry e) =>
        e.Source == StartupSource.UserStartupFolder ? Path.GetFileName(e.Command) : e.Name;

    private static string Encode(byte[]? v) => v is null ? "absent" : Convert.ToHexString(v);
    private static byte[]? Decode(string s) => s == "absent" ? null : Convert.FromHexString(s);

    public async Task<TweakOutcome> SetEnabledAsync(string entryId, bool enable, bool userConfirmed,
        bool acknowledgeSecurity = false, CancellationToken ct = default)
    {
        if (!userConfirmed) return Fail(TweakResultKind.Refused, "Confirmação explícita ausente; nada foi alterado.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var entry = _enumerator.Enumerate().FirstOrDefault(e => e.Id == entryId);
            if (entry is null) return Fail(TweakResultKind.Refused, "A entrada não foi encontrada (pode ter sido removida); atualize a lista.");
            if (!entry.CanChange || ApprovedPathFor(entry.Source) is not { } approvedPath)
                return Fail(TweakResultKind.Refused, entry.Note ?? "Esta entrada não pode ser alterada por este aplicativo.");
            if (!enable && entry.LooksSecurityRelated && !acknowledgeSecurity)
                return Fail(TweakResultKind.Refused, "Esta entrada parece ser de segurança. Desativá-la exige o reconhecimento adicional explícito.");
            if (entry.Enabled == enable)
                return Fail(TweakResultKind.AlreadyApplied, enable ? "A entrada já está ativada." : "A entrada já está desativada.");

            var valueName = ValueNameOf(entry);
            byte[]? before;
            try { before = _registry.GetBinary(StartupHive.CurrentUser, approvedPath, valueName); }
            catch (Exception ex) when (IsRegistryError(ex)) { return Fail(TweakResultKind.Failed, $"Não foi possível ler o estado atual ({ex.GetType().Name}); nada foi alterado."); }

            var desired = enable ? StartupApprovedState.Enabled() : StartupApprovedState.Disabled(DateTimeOffset.UtcNow);
            var backup = await _backups.CreateAsync(BackupOperation, new Dictionary<string, string>
            {
                ["entryId"] = entry.Id,
                ["name"] = entry.Name,
                ["source"] = entry.Source.ToString(),
                ["path"] = approvedPath,
                ["valueName"] = valueName,
                ["before"] = Encode(before),
                ["wasEnabled"] = entry.Enabled.ToString()
            }, "StartupEngine.RestoreAsync(backupId)", ct).ConfigureAwait(false);

            try { _registry.SetBinary(approvedPath, valueName, desired); }
            catch (Exception ex) when (IsRegistryError(ex))
            {
                var restored = TryPut(approvedPath, valueName, before);
                await _backups.CompleteAsync(backup.Id, new Dictionary<string, string>(), restored ? "Falhou; estado anterior restaurado" : "Falhou; estado parcial — verifique", ct).ConfigureAwait(false);
                var msg = restored ? $"A alteração falhou ({ex.GetType().Name}) e o estado anterior foi restaurado."
                                   : $"A alteração falhou ({ex.GetType().Name}) e a restauração automática também falhou: verifique a entrada.";
                await LogAsync(enable, entry, OperationResult.Failed, msg, backup.Id, sw.Elapsed).ConfigureAwait(false);
                return new TweakOutcome(TweakResultKind.Failed, msg, backup.Id, false, Array.Empty<string>());
            }

            byte[]? after = null;
            var verified = false;
            try
            {
                after = _registry.GetBinary(StartupHive.CurrentUser, approvedPath, valueName);
                verified = StartupApprovedState.IsEnabled(after) == enable;
            }
            catch (Exception ex) when (IsRegistryError(ex)) { _ = ex; }

            await _backups.CompleteAsync(backup.Id,
                new Dictionary<string, string> { ["after"] = Encode(after), ["verified"] = verified.ToString() },
                verified ? "Aplicado e verificado" : "Aplicado, verificação falhou", ct).ConfigureAwait(false);

            var message = verified
                ? (enable ? "Entrada ativada e verificada." : "Entrada desativada e verificada. O programa continua instalado e não iniciará com o Windows.")
                : "O valor foi escrito, mas a leitura posterior não confirmou a mudança.";
            await LogAsync(enable, entry, verified ? OperationResult.Success : OperationResult.Failed, message, backup.Id, sw.Elapsed).ConfigureAwait(false);
            return new TweakOutcome(verified ? TweakResultKind.Success : TweakResultKind.Failed, message, backup.Id, verified, Array.Empty<string>());
        }
        finally { _gate.Release(); }
    }

    public async Task<TweakOutcome> RestoreAsync(string backupId, bool userConfirmed, bool overrideConflict = false, CancellationToken ct = default)
    {
        if (!userConfirmed) return Fail(TweakResultKind.Refused, "Confirmação explícita ausente; nada foi alterado.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var rec = await _backups.GetAsync(backupId, ct).ConfigureAwait(false);
            if (rec is null || rec.Operation != BackupOperation) return Fail(TweakResultKind.Refused, "Backup de inicialização não encontrado.");
            if (!rec.Status.StartsWith("Aplicado", StringComparison.Ordinal))
                return Fail(TweakResultKind.Refused, $"Este backup está no estado \"{rec.Status}\" e não pode ser restaurado.");
            if (!rec.Before.TryGetValue("entryId", out var entryId) || !rec.Before.TryGetValue("path", out var path)
                || !rec.Before.TryGetValue("valueName", out var valueName) || !rec.Before.TryGetValue("before", out var encoded)
                || !rec.Before.TryGetValue("name", out var name))
                return Fail(TweakResultKind.Refused, "Backup inválido ou incompleto.");

            // Somente os dois caminhos de StartupApproved do usuário, e somente para uma entrada que ainda existe.
            if (path != StartupEnumerator.ApprovedRunPath && path != StartupEnumerator.ApprovedFolderPath)
                return Fail(TweakResultKind.Refused, "O backup não corresponde a um caminho permitido; nada foi alterado.");
            var entry = _enumerator.Enumerate().FirstOrDefault(e => e.Id == entryId);
            if (entry is null || ValueNameOf(entry) != valueName)
                return Fail(TweakResultKind.Refused, "A entrada deste backup não existe mais; nada foi alterado.");

            byte[]? before;
            try { before = Decode(encoded); }
            catch (FormatException) { return Fail(TweakResultKind.Refused, "O backup contém um valor ilegível; nada foi alterado."); }

            byte[]? current;
            try { current = _registry.GetBinary(StartupHive.CurrentUser, path, valueName); }
            catch (Exception ex) when (IsRegistryError(ex)) { return Fail(TweakResultKind.Failed, $"Não foi possível ler o estado atual ({ex.GetType().Name}); nada foi alterado."); }

            var conflicts = new List<string>();
            if (rec.After is { } recorded && recorded.TryGetValue("after", out var afterEncoded))
            {
                byte[]? expected;
                try { expected = Decode(afterEncoded); } catch (FormatException) { expected = null; }
                if (StartupApprovedState.IsEnabled(expected) != StartupApprovedState.IsEnabled(current))
                    conflicts.Add($"{name}: o estado atual ({(StartupApprovedState.IsEnabled(current) ? "ativada" : "desativada")}) difere do que o VZN Booster aplicou ({(StartupApprovedState.IsEnabled(expected) ? "ativada" : "desativada")}).");
            }
            if (conflicts.Count > 0 && !overrideConflict)
                return new TweakOutcome(TweakResultKind.Conflict,
                    "O estado da entrada mudou depois da aplicação (por outro programa ou por você). Confirme novamente para sobrescrevê-lo.",
                    rec.Id, false, conflicts);

            var keep = new Dictionary<string, string>(rec.After ?? new Dictionary<string, string>());
            try
            {
                if (before is null) _registry.DeleteValue(path, valueName);
                else _registry.SetBinary(path, valueName, before);
            }
            catch (Exception ex) when (IsRegistryError(ex))
            {
                keep["restore.verified"] = bool.FalseString;
                await _backups.CompleteAsync(rec.Id, keep, "Reversão falhou; estado parcial — verifique", ct).ConfigureAwait(false);
                var msg = $"A reversão falhou ({ex.GetType().Name}); verifique a entrada.";
                await LogRestoreAsync(entry, OperationResult.Failed, msg, rec.Id, sw.Elapsed).ConfigureAwait(false);
                return new TweakOutcome(TweakResultKind.Failed, msg, rec.Id, false, Array.Empty<string>());
            }

            bool verified;
            try
            {
                var now = _registry.GetBinary(StartupHive.CurrentUser, path, valueName);
                verified = StartupApprovedState.IsEnabled(now) == StartupApprovedState.IsEnabled(before);
            }
            catch (Exception ex) when (IsRegistryError(ex)) { verified = false; _ = ex; }

            keep["restore.utc"] = DateTimeOffset.UtcNow.ToString("O");
            keep["restore.verified"] = verified.ToString();
            await _backups.CompleteAsync(rec.Id, keep, verified ? "Revertido e verificado" : "Revertido, verificação falhou", ct).ConfigureAwait(false);

            var message = verified ? "Entrada revertida ao estado anterior e verificada." : "A reversão foi executada, mas a leitura posterior não confirmou o estado anterior.";
            await LogRestoreAsync(entry, verified ? OperationResult.Success : OperationResult.Failed, message, rec.Id, sw.Elapsed).ConfigureAwait(false);
            return new TweakOutcome(verified ? TweakResultKind.Success : TweakResultKind.Failed, message, rec.Id, verified,
                conflicts.Count > 0 ? conflicts : Array.Empty<string>());
        }
        finally { _gate.Release(); }
    }

    private bool TryPut(string path, string name, byte[]? snapshot)
    {
        try
        {
            if (snapshot is null) _registry.DeleteValue(path, name);
            else _registry.SetBinary(path, name, snapshot);
            return true;
        }
        catch (Exception ex) when (IsRegistryError(ex)) { return false; }
    }

    private static bool IsRegistryError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or PlatformNotSupportedException;

    private static TweakOutcome Fail(TweakResultKind kind, string message) => new(kind, message, null, false, Array.Empty<string>());

    private Task LogAsync(bool enable, StartupEntry e, OperationResult result, string summary, string backupId, TimeSpan d) =>
        _log.WriteAsync(new OperationLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "Inicialização",
            $"startup.{(enable ? "enable" : "disable")}:{e.Name}", null, new[] { $"{e.SourceText}: {e.Name}" }, null, summary, null, d, result, false, backupId, false),
            CancellationToken.None);

    private Task LogRestoreAsync(StartupEntry e, OperationResult result, string summary, string backupId, TimeSpan d) =>
        _log.WriteAsync(new OperationLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "Inicialização",
            $"startup.restore:{e.Name}", null, new[] { $"{e.SourceText}: {e.Name}" }, null, summary, null, d, result, false, backupId, false),
            CancellationToken.None);
}
