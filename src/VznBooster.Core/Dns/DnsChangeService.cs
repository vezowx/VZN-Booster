using System.Net;
using System.Net.Sockets;
using VznBooster.Core.Backup;
using VznBooster.Core.Commands;
using VznBooster.Core.Logging;
using VznBooster.Core.Networking;

namespace VznBooster.Core.Dns;

public sealed record DnsApplyPlan(
    NetworkAdapterInfo Adapter, DnsProfile Profile, IReadOnlyList<string> NewServers,
    string CurrentDescription, IReadOnlyList<string> Warnings);

public sealed record DnsChangeOutcome(bool Success, bool Verified, string Message, string? BackupId, OperationLogEntry? LogEntry);

/// <summary>
/// Fluxo: planejar (mostrar atual x novo) → confirmar → backup → aplicar → verificar → registrar → restaurar.
/// Altera somente o DNS IPv4 do adaptador escolhido. Recusa se não for possível saber se o DNS atual era
/// automático ou manual (sem isso a restauração não seria confiável). Uma alteração por vez.
/// </summary>
public sealed class DnsChangeService
{
    public const string BackupOperation = "dns.apply";
    private readonly SystemOperationExecutor _exec;
    private readonly BackupStore _backups;
    private readonly IAdapterSource _adapters;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DnsChangeService(SystemOperationExecutor exec, BackupStore backups, IAdapterSource adapters)
    {
        _exec = exec ?? throw new ArgumentNullException(nameof(exec));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
    }

    public DnsApplyPlan Plan(string adapterId, string profileId)
    {
        var adapter = Find(adapterId) ?? throw new ArgumentException("Adaptador não encontrado.", nameof(adapterId));
        var profile = DnsProfiles.All.FirstOrDefault(p => p.Id == profileId)
                      ?? throw new ArgumentException("Perfil DNS desconhecido.", nameof(profileId));
        if (!string.Equals(adapter.Status, "Up", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O adaptador não está ativo.");
        if (adapter.InterfaceIndex is null)
            throw new InvalidOperationException("O adaptador não tem IPv4 habilitado e não é suportado.");

        var warnings = new List<string>
        {
            "Somente o DNS IPv4 será alterado.",
            "O DNS afeta principalmente a resolução de nomes; não há garantia de redução de ping em partidas."
        };
        if (profile.Variant.Contains("bloque", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Este perfil filtra domínios: pode bloquear sites ou sobrepor filtros que você já usa.");
        if (adapter.DnsSource == DnsConfigSource.Unknown)
            warnings.Add("Não foi possível determinar se o DNS atual é automático ou manual: a aplicação será recusada.");

        var current = adapter.DnsSource switch
        {
            DnsConfigSource.Automatic => "Automático (DHCP)",
            DnsConfigSource.Static => "Manual: " + string.Join(", ", adapter.StaticDnsServers),
            _ => "Indeterminado"
        };
        return new DnsApplyPlan(adapter, profile, profile.IPv4, current, warnings);
    }

    public async Task<DnsChangeOutcome> ApplyAsync(DnsApplyPlan plan, bool userConfirmed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!userConfirmed) return Fail("Confirmação explícita ausente; nada foi alterado.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var adapter = Find(plan.Adapter.Id);
            if (adapter is null || adapter.InterfaceIndex is null) return Fail("O adaptador não está mais disponível.");
            if (adapter.DnsSource == DnsConfigSource.Unknown)
                return Fail("Não é possível determinar o DNS atual com segurança; nada foi alterado.");

            var before = new Dictionary<string, string>
            {
                ["adapterId"] = adapter.Id,
                ["adapterName"] = adapter.Name,
                ["source"] = adapter.DnsSource.ToString(),
                ["staticServers"] = string.Join(",", adapter.StaticDnsServers),
                ["profile"] = plan.Profile.Id
            };
            var backup = await _backups.CreateAsync(BackupOperation, before,
                "DnsChangeService.RestoreAsync(backupId)", ct).ConfigureAwait(false);

            var op = DnsOperations.Set(adapter.InterfaceIndex.Value, plan.NewServers);
            var entry = await _exec.ExecuteAsync(op, true, backup.Id, ct).ConfigureAwait(false);
            if (entry.Result != OperationResult.Success)
            {
                await _backups.CompleteAsync(backup.Id, new Dictionary<string, string>(),
                    "Falhou; estado final não confirmado", ct).ConfigureAwait(false);
                return new DnsChangeOutcome(false, false,
                    $"A alteração não foi concluída ({entry.Result}). {entry.StdErr}".Trim(), backup.Id, entry);
            }

            var now = Find(adapter.Id);
            var verified = now is not null && SameSet(Ipv4Only(now.DnsServers), plan.NewServers);
            await _backups.CompleteAsync(backup.Id,
                new Dictionary<string, string>
                {
                    ["servers"] = string.Join(",", now is null ? Array.Empty<string>() : Ipv4Only(now.DnsServers)),
                    ["verified"] = verified.ToString()
                },
                verified ? "Aplicado e verificado" : "Aplicado, verificação falhou", ct).ConfigureAwait(false);

            return new DnsChangeOutcome(true, verified,
                verified ? "DNS aplicado e verificado."
                         : "O comando foi concluído, mas a leitura posterior não confirmou o novo DNS.",
                backup.Id, entry);
        }
        finally { _gate.Release(); }
    }

    public async Task<DnsChangeOutcome> RestoreAsync(string backupId, bool userConfirmed, CancellationToken ct = default)
    {
        if (!userConfirmed) return Fail("Confirmação explícita ausente; nada foi alterado.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rec = await _backups.GetAsync(backupId, ct).ConfigureAwait(false);
            if (rec is null || rec.Operation != BackupOperation) return Fail("Backup de DNS não encontrado.");
            if (!rec.Before.TryGetValue("adapterId", out var id) || !rec.Before.TryGetValue("source", out var src))
                return Fail("Backup inválido ou incompleto.");

            var adapter = Find(id);
            if (adapter is null || adapter.InterfaceIndex is null) return Fail("O adaptador do backup não existe mais.");

            ValidatedOperation op;
            IReadOnlyList<string> expected = Array.Empty<string>();
            if (src == nameof(DnsConfigSource.Automatic))
            {
                op = DnsOperations.Reset(adapter.InterfaceIndex.Value);
            }
            else if (src == nameof(DnsConfigSource.Static))
            {
                rec.Before.TryGetValue("staticServers", out var joined);
                expected = (joined ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
                try { op = DnsOperations.Set(adapter.InterfaceIndex.Value, expected); }
                catch (ArgumentException) { return Fail("O backup contém endereços DNS inválidos; nada foi alterado."); }
            }
            else return Fail("O backup não registra o modo de DNS anterior; nada foi alterado.");

            var entry = await _exec.ExecuteAsync(op, true, rec.Id, ct).ConfigureAwait(false);
            var keep = rec.After ?? new Dictionary<string, string>();
            if (entry.Result != OperationResult.Success)
            {
                await _backups.CompleteAsync(rec.Id, keep, "Restauração falhou", ct).ConfigureAwait(false);
                return new DnsChangeOutcome(false, false,
                    $"A restauração não foi concluída ({entry.Result}). {entry.StdErr}".Trim(), rec.Id, entry);
            }

            var now = Find(adapter.Id);
            var verified = now is not null && (src == nameof(DnsConfigSource.Automatic)
                ? now.DnsSource == DnsConfigSource.Automatic
                : SameSet(Ipv4Only(now.DnsServers), expected));
            await _backups.CompleteAsync(rec.Id, keep,
                verified ? "Restaurado e verificado" : "Restaurado, verificação falhou", ct).ConfigureAwait(false);
            return new DnsChangeOutcome(true, verified,
                verified ? "Configuração anterior restaurada e verificada."
                         : "O comando foi concluído, mas a leitura posterior não confirmou a restauração.",
                rec.Id, entry);
        }
        finally { _gate.Release(); }
    }

    private NetworkAdapterInfo? Find(string id) =>
        _adapters.ReadAll().FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    private static DnsChangeOutcome Fail(string message) => new(false, false, message, null, null);

    private static IReadOnlyList<string> Ipv4Only(IEnumerable<string> addresses) =>
        addresses.Where(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                 .Select(a => IPAddress.Parse(a).ToString()).ToList();

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        a.Select(Canon).ToHashSet().SetEquals(b.Select(Canon));

    private static string Canon(string s) => IPAddress.TryParse(s, out var ip) ? ip.ToString() : s;
}
