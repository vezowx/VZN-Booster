namespace VznBooster.Core.Settings;

public enum RevealMode { AutoHideAfterDelay, HideWhenLeavingSection, KeepForSession }

/// <summary>
/// Preferências do aplicativo. Propriedades com valor padrão (não construtor posicional) para que um arquivo
/// antigo ou incompleto nunca desligue a privacidade por acidente.
/// </summary>
public sealed record AppSettings
{
    public bool HideSensitiveData { get; init; } = true;
    public RevealMode RevealMode { get; init; } = RevealMode.AutoHideAfterDelay;
    public int RevealSeconds { get; init; } = 30;
    public bool ReduceAnimations { get; init; } = false;
    public bool SidebarCollapsed { get; init; } = false;
    public int MonitorIntervalSeconds { get; init; } = 1;

    public static AppSettings Default { get; } = new();

    public AppSettings Normalize() => this with
    {
        RevealSeconds = Math.Clamp(RevealSeconds, 5, 300),
        MonitorIntervalSeconds = Math.Clamp(MonitorIntervalSeconds, 1, 10),
        RevealMode = Enum.IsDefined(typeof(RevealMode), RevealMode) ? RevealMode : RevealMode.AutoHideAfterDelay
    };
}
