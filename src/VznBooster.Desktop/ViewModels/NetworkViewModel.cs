using System.Collections.ObjectModel;
using VznBooster.Core.Networking;
using VznBooster.Core.Privacy;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed record AdapterRow(string Name, string Details);

public sealed class NetworkViewModel : PageViewModel
{
    private const string GatewayLabel = "gateway padrão";

    private readonly PingTestService _ping = new(new IcmpPingProbe());
    private IReadOnlyList<NetworkAdapterInfo> _raw = Array.Empty<NetworkAdapterInfo>();
    private CancellationTokenSource? _cts;
    private string? _gatewayAddress;
    private bool _targetIsGateway;
    private bool _isBusy;
    private string _target = "1.1.1.1";
    private string _samplesText = "10";
    private string _adaptersInfo = "Carregando adaptadores…";
    private string _resultText = "Nenhum teste executado ainda.";

    public NetworkViewModel(AppServices services) : base("Network", services)
    {
        // Os controles de olho não exibem valor; só alternam a categoria (revelação explícita e individual).
        IpField = new SensitiveField(services.Privacy, SensitiveCategory.IpAddress, () => null);
        GatewayField = new SensitiveField(services.Privacy, SensitiveCategory.Gateway, () => null);
        services.Privacy.Changed += RebuildRows;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, services.ReportError);
        RunPingCommand = new AsyncRelayCommand(RunPingAsync, services.ReportError);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        UseGatewayCommand = new RelayCommand(UseGateway, () => !IsBusy);
        Dns = new DnsViewModel(services);
        Gaming = new GamingViewModel(services);
        Connectivity = new ConnectivityViewModel(services);
    }

    public DnsViewModel Dns { get; }
    public GamingViewModel Gaming { get; }
    public ConnectivityViewModel Connectivity { get; }

    public override void OnActivated()
    {
        RefreshCommand.Execute(null);
        Dns.RefreshCommand.Execute(null);
    }

    public SensitiveField IpField { get; }
    public SensitiveField GatewayField { get; }
    public ObservableCollection<AdapterRow> Adapters { get; } = new();
    public ObservableCollection<string> History { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RunPingCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand UseGatewayCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Se o usuário digitar, deixa de ser "gateway padrão". O endereço real do gateway nunca vai para a caixa de texto.</summary>
    public string Target
    {
        get => _target;
        set { if (SetProperty(ref _target, value)) _targetIsGateway = false; }
    }

    public string SamplesText { get => _samplesText; set => SetProperty(ref _samplesText, value); }
    public string AdaptersInfo { get => _adaptersInfo; private set => SetProperty(ref _adaptersInfo, value); }
    public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }

    private async Task RefreshAsync()
    {
        AdaptersInfo = "Carregando adaptadores…";
        _raw = await Task.Run(() => NetworkAdapterReader.ReadAll());
        RebuildRows();
    }

    private void RebuildRows()
    {
        Adapters.Clear();
        foreach (var a in _raw) Adapters.Add(ToRow(a));
        AdaptersInfo = _raw.Count == 0
            ? "Nenhum adaptador encontrado."
            : $"{_raw.Count} adaptador(es). Use o olho ao lado de cada categoria para mostrar temporariamente.";
    }

    private AdapterRow ToRow(NetworkAdapterInfo a)
    {
        string List(IEnumerable<string> addresses, SensitiveCategory category)
        {
            var shown = addresses.Select(x => Services.Privacy.Display(category, x)).ToList();
            return shown.Count == 0 ? "nenhum ou indisponível" : string.Join(", ", shown);
        }

        var mode = a.DnsSource switch
        {
            DnsConfigSource.Automatic => "automático (DHCP)",
            DnsConfigSource.Static => "manual",
            _ => "modo indeterminado"
        };
        var status = a.Status switch { "Up" => "ativo", "Down" => "inativo", _ => a.Status };
        var speed = a.LinkSpeedMbps is { } s ? $"{s:0.#} Mbps (informado pelo adaptador)" : "indisponível";
        return new AdapterRow(a.Name,
            $"Tipo: {a.Type}   •   Estado: {status}   •   Link: {speed}\n" +
            $"DNS: {List(a.DnsServers, SensitiveCategory.IpAddress)} — {mode}\n" +
            $"Gateway: {List(a.Gateways, SensitiveCategory.Gateway)}");
    }

    private void UseGateway()
    {
        var gw = _raw.Where(a => a.Status == "Up")
                     .SelectMany(a => a.Gateways)
                     .FirstOrDefault(g => g.Contains('.'));
        if (gw is null)
        {
            Services.SetStatus("Nenhum gateway IPv4 encontrado em adaptadores ativos.");
            return;
        }
        _gatewayAddress = gw;
        Target = GatewayLabel;
        _targetIsGateway = true;      // depois de definir o texto, que zera a flag
        Services.SetStatus("Destino definido para o gateway padrão.");
    }

    private async Task RunPingAsync()
    {
        if (!int.TryParse(SamplesText, out var samples))
        {
            Services.SetStatus("Quantidade de amostras inválida.");
            return;
        }

        var useGateway = _targetIsGateway && _gatewayAddress is not null;
        var destination = useGateway ? _gatewayAddress! : Target;
        var shownTarget = useGateway ? GatewayLabel : Target.Trim();

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        Services.SetStatus("Medindo latência…");
        try
        {
            var r = await _ping.RunAsync(destination, samples, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2), cts.Token);
            ShowResult(r, shownTarget);
            Services.SetStatus("Teste concluído.");
        }
        catch (ArgumentException ex)
        {
            Services.SetStatus(ex.Message);
        }
        catch (OperationCanceledException)
        {
            Services.SetStatus("Teste cancelado.");
            ResultText = "Teste cancelado pelo usuário; nenhum resultado parcial é exibido.";
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    private void ShowResult(PingTestResult r, string shownTarget)
    {
        static string Ms(double? v) => v is null ? "indisponível" : $"{v:0.0} ms";
        var s = r.Stats;
        var kind = r.Kind == MeasurementKind.Measured ? "MEDIDO" : "INDISPONÍVEL (nenhuma resposta recebida)";
        var statuses = string.Join(", ", r.Statuses.GroupBy(x => x).Select(g => $"{Describe(g.Key)}: {g.Count()}"));

        // O método é montado aqui (e não por PingTestResult.Method) para nunca exibir o endereço real do gateway.
        ResultText =
            $"{kind} — ICMP echo até {shownTarget}\n" +
            $"Amostras: {s.Sent}   Respostas: {s.Received}   Perda: {s.LossPercent:0.#}%\n" +
            $"Mínimo: {Ms(s.MinMs)}   Média: {Ms(s.AvgMs)}   Máximo: {Ms(s.MaxMs)}\n" +
            $"Desvio padrão: {Ms(s.StdDevMs)}   Jitter: {Ms(s.JitterMs)}\n" +
            $"Detalhe das tentativas — {statuses}\n\n" +
            "Esta é a latência ICMP até o destino informado, em ms inteiros. Não representa o ping de uma partida. " +
            "Um timeout pode indicar perda real ou bloqueio de ICMP pelo destino.";

        History.Insert(0, $"{DateTime.Now:HH:mm:ss} — {shownTarget} — média {Ms(s.AvgMs)} — perda {s.LossPercent:0.#}% ({s.Sent} amostras)");
        while (History.Count > 20) History.RemoveAt(History.Count - 1);
    }

    private static string Describe(ProbeStatus s) => s switch
    {
        ProbeStatus.Reply => "respostas",
        ProbeStatus.TimedOut => "timeouts",
        ProbeStatus.Unreachable => "inalcançável",
        ProbeStatus.NameResolutionFailed => "falha de resolução de nome",
        _ => "erro"
    };
}
