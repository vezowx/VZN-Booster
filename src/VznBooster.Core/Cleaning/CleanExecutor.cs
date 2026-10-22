using System.Diagnostics;
using VznBooster.Core.Logging;

namespace VznBooster.Core.Cleaning;

public enum CleanStatus { Cleaned, NotFound, AppRunning, Cancelled, Failed, Refused }

/// <param name="RemovedNames">Até 50 nomes de arquivos removidos (somente o nome, sem caminho), para o registro da operação.</param>
public sealed record CategoryCleanResult(
    CleanCategory Category, CleanStatus Status, int FilesDeleted, long BytesDeleted,
    int FilesSkipped, int DirsRemoved, string Message, IReadOnlyList<string>? RemovedNames = null);

/// <param name="FreeSpaceDeltaBytes">Variação do espaço livre das unidades afetadas (medida; pode incluir outras atividades do PC). Null se não mensurável.</param>
public sealed record CleanReport(
    bool Executed, string? RefusalReason, IReadOnlyList<CategoryCleanResult> Results,
    long? FreeSpaceDeltaBytes, bool Cancelled, TimeSpan Duration);

public sealed record CleanProgress(string Category, int FilesDeleted, long BytesDeleted);

/// <summary>
/// Exclusão do Cleaner. Recebe somente IDs de categoria e reconstrói as categorias a partir do catálogo interno,
/// então não aceita pastas arbitrárias. Arquivos em uso, somente-leitura ou sem permissão são ignorados (não são falha).
/// Não encerra processos: se o navegador estiver aberto, a categoria é pulada. Categorias de risco Avançado só rodam
/// se o ID estiver na lista de reconhecimento explícito. A operação NÃO é reversível.
/// </summary>
public sealed class CleanExecutor
{
    private const int MaxLoggedNames = 50;

    private static readonly Dictionary<string, string> BrowserProcesses = new()
    {
        ["chrome"] = "chrome", ["edge"] = "msedge", ["brave"] = "brave", ["vivaldi"] = "vivaldi", ["firefox"] = "firefox"
    };

    private readonly CleanEnvironment _env;
    private readonly IOperationLog _log;
    private readonly Func<string, bool> _isProcessRunning;
    private readonly IRecycleBin _recycleBin;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CleanExecutor(CleanEnvironment env, IOperationLog log, Func<string, bool>? isProcessRunning = null, IRecycleBin? recycleBin = null)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _isProcessRunning = isProcessRunning ?? DefaultIsRunning;
        _recycleBin = recycleBin ?? new WindowsRecycleBin();
    }

    public async Task<CleanReport> CleanAsync(IReadOnlyList<string> categoryIds, bool userConfirmed,
        IProgress<CleanProgress>? progress = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? acknowledgedAdvancedIds = null)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);
        if (categoryIds.Count == 0) throw new ArgumentException("Selecione ao menos uma categoria.", nameof(categoryIds));

        var catalog = CleanCategoryCatalog.Build(_env);
        var selected = new List<CleanCategory>();
        foreach (var id in categoryIds.Distinct())
            selected.Add(catalog.FirstOrDefault(c => c.Id == id)
                         ?? throw new ArgumentException($"Categoria desconhecida: {id}", nameof(categoryIds)));

        if (!userConfirmed)
            return new CleanReport(false, "Confirmação explícita ausente; nada foi apagado.",
                Array.Empty<CategoryCleanResult>(), null, false, TimeSpan.Zero);

        // Sem token de propósito: WaitAsync(0) não bloqueia, e com o token ele lançaria TaskCanceledException
        // se o cancelamento já tivesse sido pedido. O cancelamento é tratado abaixo e devolve um relatório.
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            throw new InvalidOperationException("Já existe uma limpeza em andamento.");
        try
        {
            var total = Stopwatch.StartNew();
            var before = SnapshotFreeSpace(selected);
            var results = new List<CategoryCleanResult>();
            var cancelled = false;

            foreach (var cat in selected)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                var sw = Stopwatch.StartNew();
                var acknowledged = acknowledgedAdvancedIds?.Contains(cat.Id) ?? false;
                var res = await Task.Run(() => CleanOne(cat, acknowledged, progress, ct), CancellationToken.None).ConfigureAwait(false);
                results.Add(res);
                await LogAsync(res, sw.Elapsed).ConfigureAwait(false);
                if (res.Status == CleanStatus.Cancelled) { cancelled = true; break; }
            }

            var after = SnapshotFreeSpace(selected);
            long? delta = null;
            foreach (var (drive, b) in before)
                if (after.TryGetValue(drive, out var a)) delta = (delta ?? 0) + (a - b);

            return new CleanReport(true, null, results, delta, cancelled, total.Elapsed);
        }
        finally { _gate.Release(); }
    }

    private CategoryCleanResult CleanOne(CleanCategory cat, bool acknowledged, IProgress<CleanProgress>? progress, CancellationToken ct)
    {
        if (cat.Risk == CleanRisk.Advanced && !acknowledged)
            return new(cat, CleanStatus.Refused, 0, 0, 0, 0, "Categoria avançada: exige confirmação adicional explícita.");
        if (cat.IsRecycleBin) return CleanRecycleBin(cat);

        if (cat.Roots.Count == 0 || !cat.Roots.Any(Directory.Exists))
            return new(cat, CleanStatus.NotFound, 0, 0, 0, 0, "Pasta não encontrada.");
        var proc = BrowserProcessFor(cat.Id);
        if (proc is not null && _isProcessRunning(proc))
            return new(cat, CleanStatus.AppRunning, 0, 0, 0, 0, cat.AppHint ?? "Feche o aplicativo antes de limpar.");

        var validator = new CleanPathValidator(cat.Roots);
        int deleted = 0, skipped = 0, dirs = 0;
        long bytes = 0;
        var names = new List<string>();
        try
        {
            foreach (var f in CleanEnumerator.EnumerateFiles(cat, validator, ct))
            {
                try
                {
                    var len = f.Length;
                    var name = f.Name;
                    f.Delete();
                    deleted++;
                    bytes += len;
                    if (names.Count < MaxLoggedNames) names.Add(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    skipped++;
                }
                if ((deleted + skipped) % 50 == 0) progress?.Report(new CleanProgress(cat.Name, deleted, bytes));
            }
            if (cat.Recursive) dirs = RemoveEmptyDirectories(cat, validator, ct);
        }
        catch (OperationCanceledException)
        {
            return new(cat, CleanStatus.Cancelled, deleted, bytes, skipped, dirs, "Cancelado pelo usuário.", names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(cat, CleanStatus.Failed, deleted, bytes, skipped, dirs, $"Erro inesperado: {ex.GetType().Name}.", names);
        }

        progress?.Report(new CleanProgress(cat.Name, deleted, bytes));
        return new(cat, CleanStatus.Cleaned, deleted, bytes, skipped, dirs,
            skipped > 0 ? $"{skipped} item(ns) ignorado(s): em uso, somente leitura ou sem permissão." : "Concluído.", names);
    }

    private CategoryCleanResult CleanRecycleBin(CleanCategory cat)
    {
        var before = _recycleBin.Query();
        if (before is null) return new(cat, CleanStatus.Failed, 0, 0, 0, 0, "Não foi possível consultar a Lixeira.");
        if (before.Items == 0) return new(cat, CleanStatus.Cleaned, 0, 0, 0, 0, "A Lixeira já estava vazia.");

        var hr = _recycleBin.Empty();
        var after = _recycleBin.Query();
        var removedItems = (int)Math.Min(int.MaxValue, Math.Max(0, before.Items - (after?.Items ?? 0)));
        var removedBytes = Math.Max(0, before.Bytes - (after?.Bytes ?? 0));
        var ok = hr == 0 || after is { Items: 0 };
        return ok
            ? new(cat, CleanStatus.Cleaned, removedItems, removedBytes, 0, 0, "Lixeira esvaziada.")
            : new(cat, CleanStatus.Failed, removedItems, removedBytes, 0, 0, $"A Lixeira não foi esvaziada por completo (código 0x{hr:X8}).");
    }

    private static string? BrowserProcessFor(string id)
    {
        var parts = id.Split('.', 3);
        return parts.Length >= 2 && parts[0] == "browser" && BrowserProcesses.TryGetValue(parts[1], out var p) ? p : null;
    }

    /// <summary>Remove apenas pastas VAZIAS (Directory.Delete sem recursão) dentro das raízes; nunca a raiz.</summary>
    private static int RemoveEmptyDirectories(CleanCategory cat, CleanPathValidator validator, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };
        var removed = 0;
        foreach (var root in cat.Roots)
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;
            var dirs = Directory.EnumerateDirectories(root, "*", options).OrderByDescending(d => d.Length).ToList();
            foreach (var d in dirs)
            {
                ct.ThrowIfCancellationRequested();
                if (!validator.IsAllowed(d, out _)) continue;
                try { Directory.Delete(d, false); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* não vazia ou protegida */ }
            }
        }
        return removed;
    }

    private Task LogAsync(CategoryCleanResult r, TimeSpan duration)
    {
        var result = r.Status switch
        {
            CleanStatus.Cleaned or CleanStatus.NotFound => OperationResult.Success,
            CleanStatus.Cancelled => OperationResult.Cancelled,
            CleanStatus.AppRunning or CleanStatus.Refused => OperationResult.Refused,
            _ => OperationResult.Failed
        };
        var summary = $"Status={r.Status}; removidos={r.FilesDeleted}; ignorados={r.FilesSkipped}; " +
                      $"bytes={r.BytesDeleted}; pastasVazias={r.DirsRemoved}. {r.Message}";
        if (r.RemovedNames is { Count: > 0 })
            summary += $" Itens removidos (até {MaxLoggedNames}, somente nomes): {string.Join("; ", r.RemovedNames)}";
        var entry = new OperationLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "Cleaner", $"cleaner.{r.Category.Id}",
            null, Array.Empty<string>(), null, summary, null, duration, result, false, null, false);
        return _log.WriteAsync(entry, CancellationToken.None);
    }

    private static Dictionary<string, long> SnapshotFreeSpace(IEnumerable<CleanCategory> cats)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in cats.SelectMany(c => c.Roots))
        {
            try
            {
                var drive = Path.GetPathRoot(root);
                if (string.IsNullOrEmpty(drive) || map.ContainsKey(drive)) continue;
                map[drive] = new DriveInfo(drive).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { /* não mensurável */ }
        }
        return map;
    }

    private static bool DefaultIsRunning(string name)
    {
        var procs = Process.GetProcessesByName(name);
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }
}
