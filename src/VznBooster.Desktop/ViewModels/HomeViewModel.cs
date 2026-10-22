using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using VznBooster.Core.Logging;
using VznBooster.Core.Monitoring;
using VznBooster.Core.Networking;
using VznBooster.Core.Privacy;
using VznBooster.Core.Recommendations;
using VznBooster.Desktop.Helpers;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed record DriveRow(string Name, double UsedPercent, string Text);
public sealed record ActionRow(string Time, string Operation, string Result);
public sealed record RecommendationRow(
    string Title, string Evidence, string Action, string Benefit, string Risk, string AdminText,
    string? TargetSection, ICommand? GoCommand)
{
    public bool HasTarget => GoCommand is not null;
    public string TargetLabel => TargetSection is null ? string.Empty : $"Abrir {TargetSection}";
}

/// <summary>
/// Home com dados reais. A coleta (CPU/RAM a cada intervalo configurado; discos, rede e histórico a cada ativação)
/// só roda enquanto a página está visível. GPU e temperaturas não são coletadas nesta versão e aparecem como indisponíveis.
/// </summary>
public sealed class HomeViewModel : PageViewModel
{
    private const int HistoryLength = 60;

    private readonly ISystemSampler _sampler = new WindowsSystemSampler();
    private readonly PingTestService _ping = new(new IcmpPingProbe());
    private readonly DispatcherTimer _timer = new();
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _ramHistory = new();
    private CpuTimes? _lastCpu;
    private double? _lastCpuPercent;
    private MemorySnapshot? _lastMemory;
    private IReadOnlyList<DriveSnapshot> _drives = Array.Empty<DriveSnapshot>();
    private string? _gateway;
    private int _ticks;

    private string _cpuText = "medindo…";
    private string _ramText = "medindo…";
    private string _ramDetail = string.Empty;
    private IReadOnlyList<double> _cpuValues = Array.Empty<double>();
    private IReadOnlyList<double> _ramValues = Array.Empty<double>();
    private string _uptimeText = string.Empty;
    private string _connectionText = "Verificando…";
    private string _latencyText = "Nenhuma medição ainda. A latência só é medida quando você pedir.";
    private bool _isMeasuring;
    private string _cleanerText = "Ainda não analisado nesta sessão.";
    private bool _hasNoActions = true;

    public HomeViewModel(AppServices services) : base("Home", services)
    {
        ComputerName = new SensitiveField(services.Privacy, SensitiveCategory.ComputerName, () => Environment.MachineName);
        UserName = new SensitiveField(services.Privacy, SensitiveCategory.UserName, () => Environment.UserName);
        var build = Environment.OSVersion.Version.Build;
        OsText = $"Windows {(build >= 22000 ? "11" : "10")} (build {build})";
        UptimeText = FormatUptime();

        _timer.Tick += (_, _) => OnTick();
        MeasureLatencyCommand = new AsyncRelayCommand(MeasureLatencyAsync, services.ReportError, () => _gateway is not null);
        GoNetworkCommand = new RelayCommand(() => services.Navigate("Network"));
        GoCleanerCommand = new RelayCommand(() => services.Navigate("Cleaner"));
    }

    public SensitiveField ComputerName { get; }
    public SensitiveField UserName { get; }
    public string OsText { get; }
    public ObservableCollection<DriveRow> Drives { get; } = new();
    public ObservableCollection<RecommendationRow> Recommendations { get; } = new();
    public ObservableCollection<ActionRow> RecentActions { get; } = new();
    public AsyncRelayCommand MeasureLatencyCommand { get; }
    public RelayCommand GoNetworkCommand { get; }
    public RelayCommand GoCleanerCommand { get; }

    public string CpuText { get => _cpuText; private set => SetProperty(ref _cpuText, value); }
    public string RamText { get => _ramText; private set => SetProperty(ref _ramText, value); }
    public string RamDetail { get => _ramDetail; private set => SetProperty(ref _ramDetail, value); }
    public IReadOnlyList<double> CpuValues { get => _cpuValues; private set => SetProperty(ref _cpuValues, value); }
    public IReadOnlyList<double> RamValues { get => _ramValues; private set => SetProperty(ref _ramValues, value); }
    public string UptimeText { get => _uptimeText; private set => SetProperty(ref _uptimeText, value); }
    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }
    public string LatencyText { get => _latencyText; private set => SetProperty(ref _latencyText, value); }
    public bool IsMeasuring { get => _isMeasuring; private set => SetProperty(ref _isMeasuring, value); }
    public string CleanerText { get => _cleanerText; private set => SetProperty(ref _cleanerText, value); }
    public bool HasNoActions { get => _hasNoActions; private set => SetProperty(ref _hasNoActions, value); }
    public bool HasNoDrives => Drives.Count == 0;
    public bool HasRecommendations => Recommendations.Count > 0;
    public bool HasNoRecommendations => Recommendations.Count == 0;

    public override void OnActivated()
    {
        _cpuHistory.Clear();
        _ramHistory.Clear();
        _lastCpu = null;
        _ticks = 0;
        CpuValues = Array.Empty<double>();
        RamValues = Array.Empty<double>();
        CpuText = "medindo…";
        RamText = "medindo…";

        UptimeText = FormatUptime();
        UpdateCleanerText();
        _timer.Interval = TimeSpan.FromSeconds(Services.Settings.Current.MonitorIntervalSeconds);
        OnTick();
        _timer.Start();

        _ = SafeAsync(RefreshDrivesAsync);
        _ = SafeAsync(RefreshNetworkAsync);
        _ = SafeAsync(RefreshActionsAsync);
    }

    public override void OnDeactivated() => _timer.Stop();

    private void OnTick()
    {
        var times = _sampler.ReadCpuTimes();
        if (times is null)
        {
            CpuText = "indisponível";
        }
        else
        {
            double? cpu = null;
            if (_lastCpu is not null) cpu = CpuUsageCalculator.Calculate(_lastCpu, times);
            _lastCpu = times;
            if (cpu is { } c)
            {
                _lastCpuPercent = c;
                Push(_cpuHistory, c);
                CpuValues = _cpuHistory.ToArray();
                CpuText = $"{c:0}%";
            }
        }

        var mem = _sampler.ReadMemory();
        _lastMemory = mem;
        if (mem is null)
        {
            RamText = "indisponível";
            RamDetail = string.Empty;
        }
        else
        {
            Push(_ramHistory, mem.UsedPercent);
            RamValues = _ramHistory.ToArray();
            RamText = $"{mem.UsedPercent:0}%";
            RamDetail = $"{ByteFormatter.Format(mem.UsedBytes)} em uso de {ByteFormatter.Format(mem.TotalBytes)}";
        }

        _ticks++;
        if (_ticks % 30 == 0) _ = SafeAsync(RefreshDrivesAsync);
        if (_ticks % 10 == 0) RebuildRecommendations();
    }

    private static void Push(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }

    private async Task RefreshDrivesAsync()
    {
        var drives = await Task.Run(() => _sampler.ReadDrives());
        _drives = drives;
        Drives.Clear();
        foreach (var d in drives)
            Drives.Add(new DriveRow(d.Name, d.UsedPercent,
                $"{ByteFormatter.Format(d.FreeBytes)} livres de {ByteFormatter.Format(d.TotalBytes)}"));
        OnPropertyChanged(nameof(HasNoDrives));
        RebuildRecommendations();
    }

    private async Task RefreshNetworkAsync()
    {
        var adapters = await Task.Run(() => NetworkAdapterReader.ReadAll());
        var active = adapters.FirstOrDefault(a => a.Status == "Up" && a.Gateways.Any(g => g.Contains('.')));
        if (active is null)
        {
            _gateway = null;
            ConnectionText = "Nenhuma conexão com gateway IPv4 detectada.";
        }
        else
        {
            _gateway = active.Gateways.First(g => g.Contains('.'));
            var kind = active.Type switch { "Ethernet" => "Ethernet", "Wireless80211" => "Wi-Fi", _ => active.Type };
            ConnectionText = $"Conectado via {kind}";
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task MeasureLatencyAsync()
    {
        var gateway = _gateway;
        if (gateway is null) return;
        IsMeasuring = true;
        try
        {
            var r = await _ping.RunAsync(gateway, 5, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
            LatencyText = r.Kind == MeasurementKind.Measured
                ? $"{r.Stats.AvgMs:0.0} ms de média até o gateway ({r.Stats.Received}/{r.Stats.Sent} respostas), perda {r.Stats.LossPercent:0.#}% — medido às {DateTime.Now:HH:mm}."
                : $"Sem resposta ICMP do gateway em {r.Stats.Sent} tentativas — o roteador pode bloquear ICMP.";
        }
        finally { IsMeasuring = false; }
    }

    private async Task RefreshActionsAsync()
    {
        var all = await Services.Log.ReadAllAsync();
        RecentActions.Clear();
        foreach (var e in all.OrderByDescending(x => x.TimestampUtc).Take(5))
            RecentActions.Add(new ActionRow(e.TimestampUtc.ToLocalTime().ToString("dd/MM HH:mm"), e.Operation, ResultText(e.Result)));
        HasNoActions = RecentActions.Count == 0;
    }

    private void UpdateCleanerText() =>
        CleanerText = Services.LastCleanerEstimateBytes is { } b
            ? $"{ByteFormatter.Format(b)} (estimativa da última análise)"
            : "Ainda não analisado nesta sessão.";

    private void RebuildRecommendations()
    {
        UpdateCleanerText();
        var snapshot = new SystemSnapshot(_lastCpuPercent, _lastMemory, _drives);
        var recs = RecommendationEngine.Evaluate(snapshot, Services.LastCleanerEstimateBytes);
        Recommendations.Clear();
        foreach (var r in recs)
        {
            ICommand? go = r.TargetSection is { } target ? new RelayCommand(() => Services.Navigate(target)) : null;
            Recommendations.Add(new RecommendationRow(r.Title, r.Evidence, r.Action, r.ExpectedBenefit, r.Risk,
                r.NeedsAdmin ? "Requer administrador." : "Não requer administrador.", r.TargetSection, go));
        }
        OnPropertyChanged(nameof(HasRecommendations));
        OnPropertyChanged(nameof(HasNoRecommendations));
    }

    private static string ResultText(OperationResult r) => r switch
    {
        OperationResult.Success => "Sucesso",
        OperationResult.Failed => "Falhou",
        OperationResult.Cancelled => "Cancelada",
        _ => "Recusada"
    };

    private static string FormatUptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h {t.Minutes} min" : $"{t.Hours} h {t.Minutes} min";
    }

    private async Task SafeAsync(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { Services.ReportError(ex); }
    }
}
