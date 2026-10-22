using System.Text.Json;
using System.Text.Json.Serialization;

namespace VznBooster.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path;

    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Preenchido quando o arquivo existia mas não pôde ser lido (os padrões foram usados).</summary>
    public string? LastLoadWarning { get; private set; }

    public AppSettings Load()
    {
        LastLoadWarning = null;
        if (!File.Exists(_path)) return AppSettings.Default;
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json);
            if (loaded is not null) return loaded.Normalize();
            LastLoadWarning = "O arquivo de configurações estava vazio; os padrões foram usados.";
        }
        catch (JsonException)
        {
            LastLoadWarning = "O arquivo de configurações estava corrompido; os padrões foram usados.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadWarning = "Não foi possível ler o arquivo de configurações; os padrões foram usados.";
        }
        return AppSettings.Default;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings.Normalize(), Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
