using System.Text.Json;
using System.Text.Json.Serialization;

namespace VznBooster.Core.Logging;

/// <summary>
/// Log em arquivo JSON Lines (uma entrada por linha). Toda entrada é redigida ANTES de ir ao disco.
/// Linhas corrompidas são ignoradas na leitura para não derrubar a tela de Logs.
/// </summary>
public sealed class JsonlOperationLog : IOperationLog
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonlOperationLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public async Task WriteAsync(OperationLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(entry.RedactSensitiveData(), Json) + Environment.NewLine;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(_path, line, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<OperationLogEntry>> ReadAllAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return Array.Empty<OperationLogEntry>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var lines = await File.ReadAllLinesAsync(_path, ct).ConfigureAwait(false);
            var list = new List<OperationLogEntry>();
            foreach (var l in lines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<OperationLogEntry>(l, Json);
                    if (e is not null) list.Add(e);
                }
                catch (JsonException) { /* linha corrompida: ignorada */ }
            }
            return list;
        }
        finally { _gate.Release(); }
    }
}
