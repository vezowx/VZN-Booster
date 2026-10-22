using System.Collections.ObjectModel;
using System.Windows.Input;
using VznBooster.Core.Tweaker;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed class TweakCardViewModel : ObservableObject
{
    private readonly TweakerViewModel _owner;
    private bool _isSelected;
    private bool _isExpanded;
    private TweakStatus _status = TweakStatus.Unknown;
    private string _currentText = "Lendo estado atual…";
    private bool _isApplicable = true;
    private string? _notApplicableReason;
    private string? _activeBackupId;

    public TweakCardViewModel(TweakDefinition definition, TweakerViewModel owner, Action<Exception> onError)
    {
        Definition = definition;
        _owner = owner;
        ApplyCommand = new AsyncRelayCommand(() => owner.ApplyOneAsync(this), onError, () => CanApply && !owner.IsBusy);
        RestoreCommand = new AsyncRelayCommand(() => owner.RestoreOneAsync(this), onError, () => CanRestore && !owner.IsBusy);
        ToggleDetailsCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public TweakDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Summary => Definition.Summary;
    public string Technical => Definition.Technical;
    public string Goal => Definition.Goal;
    public string ExpectedBenefit => Definition.ExpectedBenefit;
    public string UsefulWhen => Definition.UsefulWhen;
    public string SideEffects => Definition.SideEffects;
    public string? CompatibilityNote => Definition.CompatibilityNote;
    public bool HasCompatibilityNote => !string.IsNullOrWhiteSpace(Definition.CompatibilityNote);
    public string RiskKey => Definition.Risk.ToString();
    public string RiskText => Definition.Risk switch { TweakRisk.Low => "Risco baixo", TweakRisk.Moderate => "Risco moderado", _ => "Risco alto" };
    public string BenefitText => Definition.BenefitKind switch
    {
        TweakBenefit.Measured => "verificado por medição",
        TweakBenefit.Expected => "esperado, mas depende do cenário",
        TweakBenefit.Visual => "principalmente visual ou comportamental",
        _ => "não mensurável de forma confiável"
    };
    public string RestartText => Definition.Restart switch
    {
        RestartRequirement.None => "Não exige reinício.",
        RestartRequirement.SignOut => "Exige encerrar a sessão do Windows e entrar de novo.",
        _ => "Exige reiniciar o computador."
    };
    public string AdminText => Definition.NeedsAdmin ? "Requer administrador." : "Não requer administrador (altera apenas o seu usuário).";
    public string ReversibleText => "Reversível: o valor anterior é salvo antes de aplicar e restaurado depois; se algo mudar nesse meio tempo, o aplicativo avisa antes de sobrescrever.";

    public string StatusKey => _status.ToString();
    public string StatusText => !_isApplicable ? "Indisponível neste Windows" : _status switch
    {
        TweakStatus.Applied => "Aplicado",
        TweakStatus.Partial => "Parcialmente aplicado",
        TweakStatus.NotApplied => "Não aplicado",
        _ => "Estado desconhecido"
    };
    public string CurrentText { get => _currentText; private set => SetProperty(ref _currentText, value); }
    public string? NotApplicableReason => _notApplicableReason;
    public bool HasNotApplicableReason => !_isApplicable && !string.IsNullOrWhiteSpace(_notApplicableReason);

    public bool CanApply => _isApplicable && _status != TweakStatus.Applied && _status != TweakStatus.Unknown;
    public bool HasActiveBackup => _activeBackupId is not null;
    public bool CanRestore => HasActiveBackup;
    public string? ActiveBackupId => _activeBackupId;
    public string RestoreHint => _status is TweakStatus.Applied or TweakStatus.Partial && !HasActiveBackup
        ? "Este ajuste está em vigor, mas não há backup do VZN Booster para ele (aplicado fora do aplicativo ou backup removido). Não é possível reverter com segurança por aqui."
        : string.Empty;
    public bool HasRestoreHint => RestoreHint.Length > 0;

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    public ICommand ApplyCommand { get; }
    public ICommand RestoreCommand { get; }
    public RelayCommand ToggleDetailsCommand { get; }

    public void ApplyState(TweakState state, string? activeBackupId)
    {
        _status = state.Status;
        _isApplicable = state.Applicable;
        _notApplicableReason = state.NotApplicableReason;
        _activeBackupId = activeBackupId;
        CurrentText = "Estado atual: " + string.Join("  •  ", state.CurrentValues);
        foreach (var name in new[]
                 {
                     nameof(StatusKey), nameof(StatusText), nameof(NotApplicableReason), nameof(HasNotApplicableReason),
                     nameof(CanApply), nameof(HasActiveBackup), nameof(CanRestore), nameof(ActiveBackupId),
                     nameof(RestoreHint), nameof(HasRestoreHint)
                 })
            OnPropertyChanged(name);
    }
}

public sealed class TweakGroupViewModel
{
    public TweakGroupViewModel(string title, IEnumerable<TweakCardViewModel> cards)
    {
        Title = title;
        Cards = new ObservableCollection<TweakCardViewModel>(cards);
    }

    public string Title { get; }
    public ObservableCollection<TweakCardViewModel> Cards { get; }
}

public sealed record ProfileItem(TweakProfile Profile, ICommand SelectCommand)
{
    public string Name => Profile.Name;
}

public sealed class TweakerViewModel : PageViewModel
{
    private const string HighRiskAck =
        "Entendo os efeitos e riscos descritos acima, incluindo os de alto risco, e quero aplicar estes ajustes.";

    private bool _isBusy;
    private string _lastResult = string.Empty;
    private string _profileInfo = "Escolha um perfil para marcar ajustes sugeridos. Nada é aplicado até você confirmar.";

    public TweakerViewModel(AppServices services) : base("Tweaker", services)
    {
        var cards = TweakCatalog.All.Select(t => new TweakCardViewModel(t, this, services.ReportError)).ToList();
        foreach (var category in TweakCatalog.CategoryOrder)
        {
            var inGroup = cards.Where(c => c.Definition.Category == category).ToList();
            if (inGroup.Count > 0) Groups.Add(new TweakGroupViewModel(category, inGroup));
        }

        foreach (var profile in TweakCatalog.Profiles)
            Profiles.Add(new ProfileItem(profile, new RelayCommand(() => SelectProfile(profile))));

        ApplySelectedCommand = new AsyncRelayCommand(ApplySelectedAsync, services.ReportError, () => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, services.ReportError, () => !IsBusy);
    }

    public ObservableCollection<TweakGroupViewModel> Groups { get; } = new();
    public ObservableCollection<ProfileItem> Profiles { get; } = new();
    public AsyncRelayCommand ApplySelectedCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public string UnavailableText => "Ainda não disponíveis nesta versão: " + string.Join("; ", TweakCatalog.UnavailableCategories) + ".";

    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string LastResult { get => _lastResult; private set { if (SetProperty(ref _lastResult, value)) OnPropertyChanged(nameof(HasLastResult)); } }
    public bool HasLastResult => _lastResult.Length > 0;
    public string ProfileInfo { get => _profileInfo; private set => SetProperty(ref _profileInfo, value); }

    private IEnumerable<TweakCardViewModel> AllCards => Groups.SelectMany(g => g.Cards);

    public override void OnActivated() => _ = RefreshSafeAsync();

    private async Task RefreshSafeAsync()
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { Services.ReportError(ex); }
    }

    private async Task RefreshAsync()
    {
        var cards = AllCards.ToList();
        var states = await Task.Run(() => cards.Select(c => Services.Tweaks.GetState(c.Definition)).ToList());
        for (var i = 0; i < cards.Count; i++)
        {
            var backup = await Services.Tweaks.FindActiveBackupAsync(cards[i].Id);
            cards[i].ApplyState(states[i], backup?.Id);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private void SelectProfile(TweakProfile profile)
    {
        foreach (var c in AllCards) c.IsSelected = profile.TweakIds.Contains(c.Id) && c.CanApply;
        var skipped = profile.TweakIds.Count(id => AllCards.First(c => c.Id == id) is { CanApply: false });
        ProfileInfo = profile.Description + (skipped > 0
            ? $" {skipped} ajuste(s) deste perfil já estão aplicados ou não estão disponíveis neste Windows e não foram marcados."
            : string.Empty) + " Você pode desmarcar ajustes individualmente.";
    }

    // ---------------- aplicar ----------------

    public async Task ApplyOneAsync(TweakCardViewModel card) => await ApplyAsync(new List<TweakCardViewModel> { card });

    private async Task ApplySelectedAsync()
    {
        var chosen = AllCards.Where(c => c.IsSelected && c.CanApply).ToList();
        if (chosen.Count == 0)
        {
            LastResult = "Nenhum ajuste aplicável está selecionado.";
            return;
        }
        await ApplyAsync(chosen);
    }

    private async Task ApplyAsync(List<TweakCardViewModel> chosen)
    {
        var hasHigh = chosen.Any(c => c.Definition.Risk == TweakRisk.High);
        if (!Services.Confirm(BuildConfirmation(chosen, hasHigh)))
        {
            Services.SetStatus("Nada foi alterado.");
            return;
        }

        IsBusy = true;
        try
        {
            var lines = new List<string>();
            foreach (var card in chosen)
            {
                Services.SetStatus($"Aplicando: {card.Name}…");
                var outcome = await Services.Tweaks.ApplyAsync(card.Id, true);
                lines.Add($"{card.Name}: {outcome.Message}");
                if (outcome.Kind is TweakResultKind.Failed or TweakResultKind.Refused)
                {
                    lines.Add("Os ajustes seguintes não foram aplicados porque este falhou.");
                    break;
                }
            }
            LastResult = string.Join("\n", lines);
            Services.SetStatus("Operação concluída. Veja o resultado e os logs.");
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    private static ConfirmRequest BuildConfirmation(List<TweakCardViewModel> chosen, bool hasHigh)
    {
        var lines = new List<string>();
        foreach (var c in chosen)
        {
            lines.Add($"• {c.Name} [{c.RiskText}]");
            lines.Add($"   O que muda: {c.Technical}");
            lines.Add($"   Por que: {c.Goal}");
            lines.Add($"   Benefício ({c.BenefitText}): {c.ExpectedBenefit}");
            lines.Add($"   Possíveis problemas: {c.SideEffects}");
            lines.Add($"   {c.RestartText}  {c.AdminText}");
        }
        lines.Add("Antes de alterar, o estado atual é salvo em backup. Depois, o valor é lido de novo para verificar a mudança, e o resultado é registrado nos logs.");
        lines.Add("Para desfazer: use \"Restaurar\" no ajuste ou na seção Restauração. A reversão é completa para estes valores.");
        return new ConfirmRequest("Confirmar ajustes", chosen.Count == 1 ? $"Aplicar \"{chosen[0].Name}\"?" : $"Aplicar {chosen.Count} ajustes?",
            lines, "Aplicar", hasHigh ? HighRiskAck : null);
    }

    // ---------------- restaurar ----------------

    public async Task RestoreOneAsync(TweakCardViewModel card)
    {
        if (card.ActiveBackupId is not { } backupId) return;
        IsBusy = true;
        try
        {
            var outcome = await TweakFlows.RestoreWithPromptsAsync(Services, backupId, card.Name);
            if (outcome is null)
            {
                Services.SetStatus("Restauração cancelada. Nada foi alterado.");
                return;
            }
            LastResult = $"{card.Name}: {outcome.Message}";
            Services.SetStatus("Restauração concluída. Veja o resultado e os logs.");
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }
}
