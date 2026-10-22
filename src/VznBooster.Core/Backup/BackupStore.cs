using System.Text.Json;
using System.Text.RegularExpressions;

namespace VznBooster.Core.Backup;

public sealed record BackupRecord(
    int FormatVersion, string Id, DateTimeOffset CreatedUtc, string Operation,
    IReadOnlyDictionary<string, string> Before, IReadOnlyDictionary<string, string>? After,
    string RestoreMethod, string Status);

/// <summary>Backups locais em JSON, um arquivo por backup, com escrita atômica e IDs validados (sem path traversal).</summary>
public sealed class BackupStore
{
    public const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Regex IdPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _dir = Path.GetFullPath(directory);
    }

    public async Task<BackupRecord> CreateAsync(string operation, IReadOnlyDictionary<string, string> before,
        string restoreMethod, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreMethod);
        var rec = new BackupRecord(CurrentFormatVersion, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            operation, new Dictionary<string, string>(before), null, restoreMethod, "Criado");
        await WriteAsync(rec, ct).ConfigureAwait(false);
        return rec;
    }

    public async Task<BackupRecord> CompleteAsync(string id, IReadOnlyDictionary<string, string> after,
        string status, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        var rec = await GetAsync(id, ct).ConfigureAwait(false)
                  ?? throw new KeyNotFoundException($"Backup não encontrado: {id}");
        var updated = rec with { After = new Dictionary<string, string>(after), Status = status };
        await WriteAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<BackupRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        if (id is null || !IdPattern.IsMatch(id)) return null;
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var rec = JsonSerializer.Deserialize<BackupRecord>(text, Json)
                  ?? throw new InvalidDataException($"Backup ilegível: {id}");
        if (rec.FormatVersion > CurrentFormatVersion)
            throw new NotSupportedException($"Formato de backup {rec.FormatVersion} é mais novo que o suportado.");
        return rec;
    }

    public async Task<IReadOnlyList<BackupRecord>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_dir)) return Array.Empty<BackupRecord>();
        var list = new List<BackupRecord>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            var rec = await GetAsync(Path.GetFileNameWithoutExtension(file), ct).ConfigureAwait(false);
            if (rec is not null) list.Add(rec);
        }
        return list.OrderByDescending(r => r.CreatedUtc).ToList();
    }

    private string PathFor(string id) => Path.Combine(_dir, id + ".json");

    private async Task WriteAsync(BackupRecord rec, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_dir);
            var path = PathFor(rec.Id);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(rec, Json), ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally { _gate.Release(); }
    }
}
