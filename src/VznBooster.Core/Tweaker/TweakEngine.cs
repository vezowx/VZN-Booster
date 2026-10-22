using System.Diagnostics;
using System.Security;
using VznBooster.Core.Backup;
using VznBooster.Core.Logging;

namespace VznBooster.Core.Tweaker;

/// <summary>
/// Fluxo de cada ajuste: ler o estado atual → backup do estado anterior → aplicar SOMENTE aquele ajuste → ler de novo e verificar →
/// registrar. A reversão restaura o estado anterior salvo (inclusive "valor inexistente"), verifica e registra, e avisa se outro
/// programa ou o usuário mudou o valor depois (conflito) antes de sobrescrever. Uma operação por vez.
/// </summary>
public sealed class TweakEngine
{
    public const string BackupOperation = "tweak.apply";

    private readonly IRegistryAccessor _registry;
    private readonly BackupStore _backups;
    private readonly IOperationLog _log;
    private readonly Func<int> _windowsBuild;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TweakEngine(IRegistryAccessor registry, BackupStore backups, IOperationLog log, Func<int>? windowsBuild = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _windowsBuild = windowsBuild ?? (() => Environment.OSVersion.Version.Build);
    }

    public bool IsApplicable(TweakDefinition def, out string? reason)
    {
        var build = _windowsBuild();
        if (def.MinBuild is { } min && build < min) { reason = $"Requer Windows build {min} ou superior."; return false; }
        if (def.MaxBuild is { } max && build > max) { reason = def.CompatibilityNote ?? $"Não disponível neste Windows (build {build})."; return false; }
        reason = null;
        return true;
    }

    public TweakState GetState(TweakDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        var applicable = IsApplicable(def, out var reason);
        try
        {
            var snaps = ReadAll(def);
            var matches = snaps.Select((s, i) => Matches(def.Changes[i], s)).ToList();
            var status = matches.All(m => m) ? TweakStatus.Applied : matches.Any(m => m) ? TweakStatus.Partial : TweakStatus.NotApplied;
            var values = def.Changes.Select((c, i) => $"{c.ValueName}: {snaps[i].Describe()}").ToList();
            return new TweakState(def, applicable, reason, status, values);
        }
        catch (Exception ex) when (IsRegistryError(ex))
        {
            return new TweakState(def, applicable, reason ?? $"Estado atual indisponível ({ex.GetType().Name}).",
                TweakStatus.Unknown, new[] { "indisponível" });
        }
    }

    /// <summary>Backup mais recente em estado "Aplicado…" deste ajuste (o que a restauração usa).</summary>
    public async Task<BackupRecord?> FindActiveBackupAsync(string tweakId, CancellationToken ct = default)
    {
        var all = await _backups.ListAsync(ct).ConfigureAwait(false);
        return all.Where(r => r.Operation == BackupOperation
                              && r.Before.TryGetValue("tweakId", out var id) && id == tweakId
                              && r.Status.StartsWith("Aplicado", StringComparison.Ordinal))
                  .OrderByDescending(r => r.CreatedUtc)
                  .FirstOrDefault();
    }

    // ------------------------------------------------------------------ aplicar

    public async Task<TweakOutcome> ApplyAsync(string tweakId, bool userConfirmed, CancellationToken ct = default)
    {
        var def = TweakCatalog.Find(tweakId) ?? throw new ArgumentException($"Ajuste desconhecido: {tweakId}", nameof(tweakId));
        if (!userConfirmed) return Result(TweakResultKind.Refused, "Confirmação explícita ausente; nada foi alterado.");
        if (!IsApplicable(def, out var why)) return Result(TweakResultKind.Refused, why ?? "Ajuste indisponível neste Windows.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();

            ValueSnapshot[] before;
            try { before = ReadAll(def); }
            catch (Exception ex) when (IsRegistryError(ex))
            {
                var msg = $"Não foi possível ler o estado atual com segurança ({ex.GetType().Name}); nada foi alterado.";
                await LogAsync("apply", def, Args(def, null), OperationResult.Failed, msg, null, false, sw.Elapsed).ConfigureAwait(false);
                return Result(TweakResultKind.Failed, msg);
            }

            if (before.Select((s, i) => Matches(def.Changes[i], s)).All(m => m))
                return Result(TweakResultKind.AlreadyApplied, "Este ajuste já está aplicado; nada foi alterado.");

            var backupData = new Dictionary<string, string>
            {
                ["tweakId"] = def.Id,
                ["tweakName"] = def.Name,
                ["risk"] = def.Risk.ToString(),
                ["restart"] = def.Restart.ToString()
            };
            for (var i = 0; i < def.Changes.Count; i++)
            {
                backupData[$"reg.{i}.path"] = def.Changes[i].KeyPath;
                backupData[$"reg.{i}.name"] = def.Changes[i].ValueName;
                backupData[$"reg.{i}.before"] = before[i].Encode();
            }
            var backup = await _backups.CreateAsync(BackupOperation, backupData,
                "TweakEngine.RestoreAsync(backupId)", ct).ConfigureAwait(false);

            var written = new List<int>();
            try
            {
                for (var i = 0; i < def.Changes.Count; i++)
                {
                    var c = def.Changes[i];
                    _registry.Write(c.KeyPath, c.ValueName, c.Kind, c.Desired);
                    written.Add(i);
                }
            }
            catch (Exception ex) when (IsRegistryError(ex))
            {
                var rolledBack = TryRollback(def, before, written);
                var status = rolledBack ? "Falhou; estado anterior restaurado" : "Falhou; estado parcial — verifique";
                await _backups.CompleteAsync(backup.Id, new Dictionary<string, string>(), status, ct).ConfigureAwait(false);
                var msg = rolledBack
                    ? $"A aplicação falhou ({ex.GetType().Name}) e o estado anterior foi restaurado."
                    : $"A aplicação falhou ({ex.GetType().Name}) e a restauração automática também falhou: verifique o ajuste manualmente.";
                await LogAsync("apply", def, Args(def, null), OperationResult.Failed, msg, backup.Id, false, sw.Elapsed).ConfigureAwait(false);
                return new TweakOutcome(TweakResultKind.Failed, msg, backup.Id, false, Array.Empty<string>());
            }

            ValueSnapshot[] after;
            try { after = ReadAll(def); }
            catch (Exception ex) when (IsRegistryError(ex)) { after = Array.Empty<ValueSnapshot>(); _ = ex; }
            var verified = after.Length == def.Changes.Count && after.Select((s, i) => Matches(def.Changes[i], s)).All(m => m);

            var afterData = new Dictionary<string, string>
            {
                ["verified"] = verified.ToString(),
                ["appliedUtc"] = DateTimeOffset.UtcNow.ToString("O")
            };
            for (var i = 0; i < after.Length; i++) afterData[$"reg.{i}.after"] = after[i].Encode();
            await _backups.CompleteAsync(backup.Id, afterData, verified ? "Aplicado e verificado" : "Aplicado, verificação falhou", ct)
                .ConfigureAwait(false);

            var note = def.Restart switch
            {
                RestartRequirement.SignOut => " Encerre a sessão do Windows e entre de novo para que a mudança tenha efeito.",
                RestartRequirement.Restart => " Reinicie o computador para que a mudança tenha efeito.",
                _ => string.Empty
            };
            var summary = $"antes: [{string.Join("; ", before.Select(b => b.Describe()))}] depois: [{string.Join("; ", after.Select(a => a.Describe()))}]";
            var message = verified
                ? "Ajuste aplicado e verificado." + note
                : "O registro foi escrito, mas a leitura posterior não confirmou o novo valor.";
            await LogAsync("apply", def, Args(def, null), verified ? OperationResult.Success : OperationResult.Failed,
                $"{message} {summary}", backup.Id, def.Restart == RestartRequirement.Restart && verified, sw.Elapsed).ConfigureAwait(false);

            return new TweakOutcome(verified ? TweakResultKind.Success : TweakResultKind.Failed, message, backup.Id, verified, Array.Empty<string>());
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ restaurar

    public async Task<TweakOutcome> RestoreAsync(string backupId, bool userConfirmed, bool overrideConflict = false, CancellationToken ct = default)
    {
        if (!userConfirmed) return Result(TweakResultKind.Refused, "Confirmação explícita ausente; nada foi alterado.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            var rec = await _backups.GetAsync(backupId, ct).ConfigureAwait(false);
            if (rec is null || rec.Operation != BackupOperation) return Result(TweakResultKind.Refused, "Backup de ajuste não encontrado.");
            if (!rec.Before.TryGetValue("tweakId", out var tweakId)) return Result(TweakResultKind.Refused, "Backup inválido: não identifica o ajuste.");
            var def = TweakCatalog.Find(tweakId);
            if (def is null) return Result(TweakResultKind.Refused, "Este backup pertence a um ajuste que não existe mais no catálogo.");
            if (!rec.Status.StartsWith("Aplicado", StringComparison.Ordinal))
                return Result(TweakResultKind.Refused, $"Este backup está no estado \"{rec.Status}\" e não pode ser restaurado.");

            // O backup só pode restaurar exatamente os valores do catálogo (protege contra arquivo de backup adulterado).
            var before = new ValueSnapshot[def.Changes.Count];
            for (var i = 0; i < def.Changes.Count; i++)
            {
                if (!rec.Before.TryGetValue($"reg.{i}.path", out var path) || path != def.Changes[i].KeyPath
                    || !rec.Before.TryGetValue($"reg.{i}.name", out var name) || name != def.Changes[i].ValueName
                    || !rec.Before.TryGetValue($"reg.{i}.before", out var encoded))
                    return Result(TweakResultKind.Refused, "O backup não corresponde ao catálogo de ajustes; nada foi alterado.");
                try { before[i] = ValueSnapshot.Decode(encoded); }
                catch (FormatException) { return Result(TweakResultKind.Refused, "O backup contém um valor ilegível; nada foi alterado."); }
            }

            ValueSnapshot[] current;
            try { current = ReadAll(def); }
            catch (Exception ex) when (IsRegistryError(ex))
            {
                var msg = $"Não foi possível ler o estado atual com segurança ({ex.GetType().Name}); nada foi alterado.";
                await LogAsync("restore", def, Args(def, before), OperationResult.Failed, msg, rec.Id, false, sw.Elapsed).ConfigureAwait(false);
                return Result(TweakResultKind.Failed, msg);
            }

            var conflicts = new List<string>();
            if (rec.After is { } recorded)
                for (var i = 0; i < def.Changes.Count; i++)
                    if (recorded.TryGetValue($"reg.{i}.after", out var enc) && !Equal(ValueSnapshot.Decode(enc), current[i]))
                        conflicts.Add($"{def.Changes[i].ValueName}: o valor atual ({current[i].Describe()}) é diferente do que o VZN Booster aplicou ({ValueSnapshot.Decode(enc).Describe()}).");
            if (conflicts.Count > 0 && !overrideConflict)
                return new TweakOutcome(TweakResultKind.Conflict,
                    "O valor foi alterado depois da aplicação (por outro programa ou por você). Confirme novamente para sobrescrevê-lo com o estado anterior.",
                    rec.Id, false, conflicts);

            var keep = new Dictionary<string, string>(rec.After ?? new Dictionary<string, string>());
            try
            {
                for (var i = 0; i < def.Changes.Count; i++) Put(def.Changes[i], before[i]);
            }
            catch (Exception ex) when (IsRegistryError(ex))
            {
                keep["restore.utc"] = DateTimeOffset.UtcNow.ToString("O");
                keep["restore.verified"] = bool.FalseString;
                await _backups.CompleteAsync(rec.Id, keep, "Reversão falhou; estado parcial — verifique", ct).ConfigureAwait(false);
                var msg = $"A reversão falhou ({ex.GetType().Name}); o ajuste pode estar parcialmente revertido. Verifique o estado atual.";
                await LogAsync("restore", def, Args(def, before), OperationResult.Failed, msg, rec.Id, false, sw.Elapsed).ConfigureAwait(false);
                return new TweakOutcome(TweakResultKind.Failed, msg, rec.Id, false, Array.Empty<string>());
            }

            bool verified;
            try { verified = ReadAll(def).Select(Equal3(before)).All(x => x); }
            catch (Exception ex) when (IsRegistryError(ex)) { verified = false; _ = ex; }

            keep["restore.utc"] = DateTimeOffset.UtcNow.ToString("O");
            keep["restore.verified"] = verified.ToString();
            await _backups.CompleteAsync(rec.Id, keep, verified ? "Revertido e verificado" : "Revertido, verificação falhou", ct).ConfigureAwait(false);

            var message = verified ? "Ajuste revertido e verificado." : "O comando de reversão foi executado, mas a leitura posterior não confirmou o estado anterior.";
            await LogAsync("restore", def, Args(def, before), verified ? OperationResult.Success : OperationResult.Failed,
                $"{message} Estado restaurado: [{string.Join("; ", before.Select(b => b.Describe()))}]", rec.Id, false, sw.Elapsed).ConfigureAwait(false);
            return new TweakOutcome(verified ? TweakResultKind.Success : TweakResultKind.Failed, message, rec.Id, verified,
                conflicts.Count > 0 ? conflicts : Array.Empty<string>());
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ utilitários

    private ValueSnapshot[] ReadAll(TweakDefinition def) =>
        def.Changes.Select(c => _registry.Read(c.KeyPath, c.ValueName)).ToArray();

    private void Put(RegistryChange change, ValueSnapshot snapshot)
    {
        if (!snapshot.Exists) _registry.Delete(change.KeyPath, change.ValueName);
        else _registry.Write(change.KeyPath, change.ValueName, snapshot.Kind!.Value, snapshot.Value ?? string.Empty);
    }

    private bool TryRollback(TweakDefinition def, ValueSnapshot[] before, List<int> written)
    {
        var ok = true;
        foreach (var i in Enumerable.Reverse(written))
        {
            try { Put(def.Changes[i], before[i]); }
            catch (Exception ex) when (IsRegistryError(ex)) { ok = false; }
        }
        return ok;
    }

    private static bool Matches(RegistryChange change, ValueSnapshot snap) =>
        snap.Exists && snap.Kind == change.Kind && SameValue(change.Kind, snap.Value, change.Desired);

    private static bool Equal(ValueSnapshot a, ValueSnapshot b) =>
        a.Exists == b.Exists && a.Kind == b.Kind && (!a.Exists || SameValue(a.Kind!.Value, a.Value, b.Value));

    private static Func<ValueSnapshot, int, bool> Equal3(ValueSnapshot[] expected) => (s, i) => Equal(expected[i], s);

    private static bool SameValue(RegKind kind, string? a, string? b) =>
        kind == RegKind.DWord && long.TryParse(a, out var x) && long.TryParse(b, out var y)
            ? x == y
            : string.Equals(a, b, StringComparison.Ordinal);

    private static bool IsRegistryError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or PlatformNotSupportedException;

    private static IReadOnlyList<string> Args(TweakDefinition def, ValueSnapshot[]? values) =>
        def.Changes.Select((c, i) =>
            $@"HKCU\{c.KeyPath}\{c.ValueName} = {(values is null ? c.Desired : values[i].Describe())}").ToList();

    private static TweakOutcome Result(TweakResultKind kind, string message) =>
        new(kind, message, null, false, Array.Empty<string>());

    private Task LogAsync(string verb, TweakDefinition def, IReadOnlyList<string> args, OperationResult result,
        string summary, string? backupId, bool restart, TimeSpan duration) =>
        _log.WriteAsync(new OperationLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "Tweaker", $"tweak.{verb}:{def.Id}",
            null, args, null, summary, null, duration, result, restart, backupId, false), CancellationToken.None);
}
