using System.IO;
using System.Collections.ObjectModel;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed class NavItemViewModel : ObservableObject
{
    private readonly Func<PageViewModel> _factory;
    private readonly Action<NavItemViewModel> _onSelected;
    private PageViewModel? _page;
    private bool _isSelected;

    public NavItemViewModel(string title, string glyph, Func<PageViewModel> factory, Action<NavItemViewModel> onSelected)
    {
        Title = title;
        Glyph = glyph;
        _factory = factory;
        _onSelected = onSelected;
    }

    public string Title { get; }
    public string Glyph { get; }
    public PageViewModel Page => _page ??= _factory();

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value) && value) _onSelected(this); }
    }
}

public sealed class NavGroupViewModel
{
    public NavGroupViewModel(string title, IReadOnlyList<NavItemViewModel> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<NavItemViewModel> Items { get; }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly List<NavItemViewModel> _all;
    private PageViewModel? _currentPage;
    private string _statusMessage = "Pronto.";

    public MainViewModel(AppServices services)
    {
        _services = services;
        services.SetStatus = s => StatusMessage = s;
        services.Navigate = Navigate;
        services.Settings.Changed += () =>
        {
            OnPropertyChanged(nameof(IsSidebarCollapsed));
            OnPropertyChanged(nameof(ReduceAnimations));
        };
        ToggleSidebarCommand = new RelayCommand(ToggleSidebar);

        NavItemViewModel Item(string title, string glyph, Func<PageViewModel> factory) => new(title, glyph, factory, OnSelected);

        Groups = new List<NavGroupViewModel>
        {
            new("VISÃO GERAL", new[]
            {
                Item("Home", "\uE80F", () => new HomeViewModel(services)),
                Item("Desempenho", "\uE9D9", () => new PerformanceViewModel(services)),
            }),
            new("OTIMIZAÇÃO", new[]
            {
                Item("Network", "\uE774", () => new NetworkViewModel(services)),
                Item("Cleaner", "\uE74D", () => new CleanerViewModel(services)),
                Item("Tweaker", "\uE9E9", () => new TweakerViewModel(services)),
            }),
            new("SISTEMA", new[]
            {
                Item("Hardware", "\uE950", () => new HardwareViewModel(services)),
                Item("Inicialização", "\uE7E8", () => new StartupViewModel(services)),
                Item("Ferramentas", "\uE90F", () => new ToolsViewModel(services)),
            }),
            new("MANUTENÇÃO", new[]
            {
                Item("Restauração", "\uE777", () => new RestoreViewModel(services)),
                Item("Logs", "\uE8A5", () => new LogsViewModel(services)),
                Item("Configurações", "\uE713", () => new SettingsViewModel(services)),
            }),
        };
        _all = Groups.SelectMany(g => g.Items).ToList();

        _all[0].IsSelected = true;
        if (services.Settings.LoadWarning is { } warning) StatusMessage = warning;
    }

    public IReadOnlyList<NavGroupViewModel> Groups { get; }
    public RelayCommand ToggleSidebarCommand { get; }

    public PageViewModel? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsSidebarCollapsed => _services.Settings.Current.SidebarCollapsed;
    public bool ReduceAnimations => _services.Settings.Current.ReduceAnimations;
    public string VersionText => "Versão " + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");

    private void Navigate(string title)
    {
        var target = _all.FirstOrDefault(i => i.Title == title);
        if (target is not null) target.IsSelected = true;
    }

    private void ToggleSidebar()
    {
        try { _services.Settings.Update(s => s with { SidebarCollapsed = !s.SidebarCollapsed }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _services.ReportError(ex); }
    }

    private void OnSelected(NavItemViewModel item)
    {
        foreach (var other in _all)
            if (!ReferenceEquals(other, item)) other.IsSelected = false;

        var previous = CurrentPage;
        var page = item.Page;
        if (!ReferenceEquals(previous, page))
        {
            previous?.OnDeactivated();
            CurrentPage = page;
            _services.Privacy.OnNavigated();
            page.OnActivated();
        }
        StatusMessage = "Pronto.";
    }
}
