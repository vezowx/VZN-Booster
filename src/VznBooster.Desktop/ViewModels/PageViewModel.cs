using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(string title, AppServices services)
    {
        Title = title;
        Services = services;
    }

    public string Title { get; }
    protected AppServices Services { get; }

    /// <summary>Chamado quando a página passa a ser exibida. Coleta periódica de dados só deve rodar enquanto ativa.</summary>
    public virtual void OnActivated() { }

    public virtual void OnDeactivated() { }
}

public sealed class PlaceholderViewModel : PageViewModel
{
    public PlaceholderViewModel(string title, string description, AppServices services) : base(title, services)
        => Description = description;

    public string Description { get; }
    public string Notice => "Esta seção ainda não foi implementada. Nenhum dado é exibido para não apresentar informações fictícias.";
}
