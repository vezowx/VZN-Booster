using VznBooster.Core.Settings;

namespace VznBooster.Desktop.Services;

public sealed class SettingsService
{
    private readonly SettingsStore _store;

    public SettingsService(SettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Current = store.Load();
        LoadWarning = store.LastLoadWarning;
    }

    public AppSettings Current { get; private set; }
    public string? LoadWarning { get; }
    public event Action? Changed;

    /// <summary>Grava primeiro; só atualiza o estado em memória se a gravação funcionar (falhas de E/S sobem ao chamador).</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(Current).Normalize();
        if (next == Current) return;
        _store.Save(next);
        Current = next;
        Changed?.Invoke();
    }
}
