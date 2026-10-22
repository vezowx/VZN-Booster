using System.Collections.ObjectModel;
using System.Windows.Input;
using VznBooster.Core.Networking;
using VznBooster.Core.Tweaker;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed class GameOption : ObservableObject
{
    private readonly Action<GameOption> _onSelected;
    private bool _isSelected;

    public GameOption(GameProfile profile, Action<GameOption> onSelected)
    {
        Profile = profile;
        _onSelected = onSelected;
    }

    public GameProfile Profile { get; }
    public string Name => Profile.Name;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value) && value) _onSelected(this); }
    }
}

public sealed class RegionOption : ObservableObject
{
    private readonly Action<RegionOption> _onSelected;
    private bool _isSelected;

    public RegionOption(GameRegion region, Action<RegionOption> onSelected)
    {
        Region = region;
        _onSelected = onSelected;
    }

    public GameRegion Region { get; }
    public string Name => Region.Name;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value) && value) _onSelected(this); }
    }
}

/// <summary>
/// Gaming Network: ESTIMATIVA INDIRETA de proximidade (handshake TCP até um endpoint regional público da AWS). Nenhum jogo publica um destino de
/// teste verificado que o aplicativo possa usar, então o servidor da partida não é medido e nenhum endereço de servidor de jogo é inventado.
/// </summary>
public sealed class GamingViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly GameNetworkService _service = new(new SocketTcpProbe());
    private readonly List<GameOption> _allGames;
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private string _searchText = string.Empty;
    private string _resultText = "Escolha um jogo e uma região e clique em \"Estimar\".";

    public GamingViewModel(AppServices services)
    {
        _services = services;
        _allGames = GameCatalog.All.Select(g => new GameOption(g, SelectGame)).ToList();
        ApplyFilter();

        MeasureCommand = new AsyncRelayCommand(MeasureAsync, services.ReportError, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
    }

    public ObservableCollection<GameOption> Games { get; } = new();
    public ObservableCollection<RegionOption> Regions { get; } = new();
    public ObservableCollection<string> History { get; } = new();
    public AsyncRelayCommand MeasureCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool HasRegions => Regions.Count > 0;
    public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
    }

    private GameOption? SelectedGame => _allGames.FirstOrDefault(g => g.IsSelected);
    private RegionOption? SelectedRegion => Regions.FirstOrDefault(r => r.IsSelected);

    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        Games.Clear();
        foreach (var g in _allGames.Where(g => term.Length == 0 || g.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            Games.Add(g);
    }

    private void SelectGame(GameOption chosen)
    {
        foreach (var g in _allGames) if (!ReferenceEquals(g, chosen)) g.IsSelected = false;
        Regions.Clear();
        foreach (var r in chosen.Profile.Regions) Regions.Add(new RegionOption(r, SelectRegion));
        OnPropertyChanged(nameof(HasRegions));
    }

    private void SelectRegion(RegionOption chosen)
    {
        foreach (var r in Regions) if (!ReferenceEquals(r, chosen)) r.IsSelected = false;
    }

    private async Task MeasureAsync()
    {
        if (SelectedGame is not { } game || SelectedRegion is not { } region)
        {
            ResultText = "Selecione um jogo e uma região.";
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        _services.SetStatus($"Estimando proximidade para {game.Name} — {region.Name}…");
        try
        {
            var vpnLikely = await Task.Run(() =>
                VpnProxyDetector.Evaluate(NetworkAdapterReader.ReadAll(), ProxyReader.Read(new CurrentUserRegistry())).VpnLikely, cts.Token);
            var r = await _service.RunAsync(game.Profile, region.Region, 10, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2), cts.Token);
            Show(r, vpnLikely);
            _services.SetStatus("Estimativa concluída.");
        }
        catch (OperationCanceledException)
        {
            ResultText = "Estimativa cancelada; nenhum resultado parcial é exibido.";
            _services.SetStatus("Estimativa cancelada.");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    private void Show(GameNetworkResult r, bool vpnLikely)
    {
        static string Ms(double? v) => v is null ? "indisponível" : $"{v:0.0} ms";
        var s = r.Stats;
        var label = r.Kind == MeasurementKind.Estimated
            ? $"ESTIMADO (indireto) — confiança {r.Confidence}"
            : "INDISPONÍVEL — nenhuma resposta do destino auxiliar";

        ResultText =
            $"{label}\n" +
            $"Jogo: {r.Game.Name}   Região: {r.Region.Name}   Horário: {r.StartedUtc.ToLocalTime():HH:mm:ss}\n" +
            $"Método: {r.Method}\n" +
            $"Amostras: {s.Sent}   Respostas: {s.Received}   Perda: {s.LossPercent:0.#}%\n" +
            $"Mínimo: {Ms(s.MinMs)}   Média: {Ms(s.AvgMs)}   Máximo: {Ms(s.MaxMs)}   Jitter: {Ms(s.JitterMs)}\n\n" +
            string.Join("\n", r.Notes.Select(n => "• " + n)) +
            (vpnLikely
                ? "\n• Indício de VPN conectada: o resultado reflete o caminho pela VPN, e comparar com medições sem VPN não prova que ela melhora ou piora a conexão."
                : "\n• Nenhum indício de VPN foi encontrado (detecção por indícios, não é prova).") +
            "\n• Só o handshake TCP é enviado ao endpoint da AWS; nenhum dado pessoal é transmitido.";

        History.Insert(0, $"{DateTime.Now:HH:mm:ss} — {r.Game.Name} / {r.Region.Name} — {(r.Kind == MeasurementKind.Estimated ? "estimado" : "indisponível")} — média {Ms(s.AvgMs)} — perda {s.LossPercent:0.#}%");
        while (History.Count > 20) History.RemoveAt(History.Count - 1);
    }
}
