using System.IO;
using VznBooster.Core.Privacy;
using VznBooster.Core.Settings;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private string _revealSecondsText;
    private string _revealSecondsError = string.Empty;
    private string _updateStatusText;
    private bool _checkingUpdates;

    public SettingsViewModel(AppServices services) : base("Configurações", services)
    {
        _revealSecondsText = services.Settings.Current.RevealSeconds.ToString();
        SettingsPath = new SensitiveField(services.Privacy, SensitiveCategory.FilePath,
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VznBooster", "settings.json"));
        ResetCommand = new RelayCommand(ResetDefaults);
        RestartAsAdminCommand = new RelayCommand(() => AdminFlows.RestartAsAdministrator(services), () => !services.Elevation.IsElevated);
        CheckUpdatesCommand = new RelayCommand(CheckUpdates);
        VersionText = "VZN Booster " + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");
        _updateStatusText = services.Updates.IsInstalled
            ? "A verificação acontece sozinha a cada abertura do aplicativo. Você também pode verificar agora."
            : "Nesta execução (fora do Setup instalado) as atualizações automáticas ficam desativadas.";
    }

    public SensitiveField SettingsPath { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public bool IsElevated => Services.Elevation.IsElevated;
    public string ElevationText => IsElevated
        ? "O VZN Booster está em execução com privilégios de administrador."
        : "O VZN Booster está em execução sem privilégios de administrador, que é o recomendado por padrão. " +
          "Operações administrativas (limpar Windows\\Temp, Prefetch e minidumps, criar ponto de restauração) são recusadas ou têm itens ignorados.";
    public string VersionText { get; }
    private AppSettings Current => Services.Settings.Current;

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    private async void CheckUpdates()
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        UpdateStatusText = "Verificando atualizações…";
        try { UpdateStatusText = await UpdateFlows.CheckInteractivelyAsync(Services, silentWhenUpToDate: false); }
        finally { _checkingUpdates = false; }
    }

    public bool HideSensitiveData
    {
        get => Current.HideSensitiveData;
        set => Apply(s => s with { HideSensitiveData = value });
    }

    public bool IsAutoHide
    {
        get => Current.RevealMode == RevealMode.AutoHideAfterDelay;
        set { if (value) Apply(s => s with { RevealMode = RevealMode.AutoHideAfterDelay }); }
    }

    public bool IsHideOnLeave
    {
        get => Current.RevealMode == RevealMode.HideWhenLeavingSection;
        set { if (value) Apply(s => s with { RevealMode = RevealMode.HideWhenLeavingSection }); }
    }

    public bool IsKeepSession
    {
        get => Current.RevealMode == RevealMode.KeepForSession;
        set { if (value) Apply(s => s with { RevealMode = RevealMode.KeepForSession }); }
    }

    public string RevealSecondsText
    {
        get => _revealSecondsText;
        set
        {
            if (!SetProperty(ref _revealSecondsText, value)) return;
            if (int.TryParse(value, out var seconds) && seconds is >= 5 and <= 300)
            {
                RevealSecondsError = string.Empty;
                Apply(s => s with { RevealSeconds = seconds });
            }
            else
            {
                RevealSecondsError = "Use um valor inteiro entre 5 e 300 segundos.";
            }
        }
    }

    public string RevealSecondsError
    {
        get => _revealSecondsError;
        private set => SetProperty(ref _revealSecondsError, value);
    }

    public bool ReduceAnimations
    {
        get => Current.ReduceAnimations;
        set => Apply(s => s with { ReduceAnimations = value });
    }

    private void Apply(Func<AppSettings, AppSettings> change)
    {
        try { Services.Settings.Update(change); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Services.ReportError(ex); }
        RefreshAll();
    }

    private void ResetDefaults()
    {
        Apply(_ => AppSettings.Default);
        _revealSecondsText = Current.RevealSeconds.ToString();
        RevealSecondsError = string.Empty;
        OnPropertyChanged(nameof(RevealSecondsText));
        Services.SetStatus("Configurações do aplicativo restauradas para os padrões.");
    }

    private void RefreshAll()
    {
        OnPropertyChanged(nameof(HideSensitiveData));
        OnPropertyChanged(nameof(IsAutoHide));
        OnPropertyChanged(nameof(IsHideOnLeave));
        OnPropertyChanged(nameof(IsKeepSession));
        OnPropertyChanged(nameof(ReduceAnimations));
    }
}
