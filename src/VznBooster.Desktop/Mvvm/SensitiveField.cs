using VznBooster.Core.Privacy;
using VznBooster.Desktop.Services;

namespace VznBooster.Desktop.Mvvm;

/// <summary>
/// Valor sensível com botão de olho. Textos de dica nunca contêm o valor. Para um controle sem valor (apenas o
/// botão de uma categoria), passe um provedor que retorna null.
/// </summary>
public sealed class SensitiveField : ObservableObject
{
    private readonly PrivacyService _privacy;
    private readonly SensitiveCategory _category;
    private readonly Func<string?> _raw;

    public SensitiveField(PrivacyService privacy, SensitiveCategory category, Func<string?> raw)
    {
        _privacy = privacy;
        _category = category;
        _raw = raw;
        ToggleCommand = new RelayCommand(() => _privacy.Toggle(_category));
        _privacy.Changed += Refresh;
    }

    public RelayCommand ToggleCommand { get; }
    public string Display => _privacy.Display(_category, _raw());
    public string Icon => _privacy.IsRevealed(_category) ? "\uED1A" : "\uE7B3";
    public string ToolTipText => _privacy.IsRevealed(_category) ? "Ocultar novamente" : "Mostrar temporariamente";

    private void Refresh()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(ToolTipText));
    }
}
