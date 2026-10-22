using System.Windows.Threading;
using VznBooster.Core.Privacy;
using VznBooster.Core.Settings;

namespace VznBooster.Desktop.Services;

/// <summary>
/// Política única de privacidade da interface. A ocultação é visual: os valores reais seguem disponíveis para os
/// diagnósticos. A revelação é explícita e individual por categoria, e volta a ocultar sozinha conforme a configuração.
/// </summary>
public sealed class PrivacyService
{
    private readonly SettingsService _settings;
    private readonly HashSet<SensitiveCategory> _revealed = new();
    private readonly Dictionary<SensitiveCategory, DispatcherTimer> _timers = new();

    public PrivacyService(SettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings.Changed += () => HideAll();
    }

    public event Action? Changed;

    public bool IsRevealed(SensitiveCategory category) => _revealed.Contains(category);

    private bool ShouldMask(SensitiveCategory category) =>
        _settings.Current.HideSensitiveData && !_revealed.Contains(category);

    public void Toggle(SensitiveCategory category)
    {
        if (_revealed.Contains(category)) Hide(category);
        else Reveal(category);
        Changed?.Invoke();
    }

    public void HideAll()
    {
        if (_revealed.Count == 0) return;
        foreach (var c in _revealed.ToList()) Hide(c);
        Changed?.Invoke();
    }

    /// <summary>Chamado ao trocar de seção: oculta tudo, exceto se o usuário escolheu manter visível durante a sessão.</summary>
    public void OnNavigated()
    {
        if (_settings.Current.RevealMode != RevealMode.KeepForSession) HideAll();
    }

    public string Display(SensitiveCategory category, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        if (!ShouldMask(category)) return raw;
        return category switch
        {
            SensitiveCategory.IpAddress or SensitiveCategory.Gateway => DataMasker.MaskAddress(raw),
            SensitiveCategory.MacAddress => DataMasker.MaskMac(raw),
            SensitiveCategory.FilePath => DataMasker.MaskPath(raw, Environment.UserName),
            _ => DataMasker.MaskName(raw)
        };
    }

    private void Reveal(SensitiveCategory category)
    {
        _revealed.Add(category);
        if (_settings.Current.RevealMode != RevealMode.AutoHideAfterDelay) return;

        StopTimer(category);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.Current.RevealSeconds) };
        timer.Tick += (_, _) =>
        {
            Hide(category);
            Changed?.Invoke();
        };
        _timers[category] = timer;
        timer.Start();
    }

    private void Hide(SensitiveCategory category)
    {
        _revealed.Remove(category);
        StopTimer(category);
    }

    private void StopTimer(SensitiveCategory category)
    {
        if (!_timers.Remove(category, out var timer)) return;
        timer.Stop();
    }
}
