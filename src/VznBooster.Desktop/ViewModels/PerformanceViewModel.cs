using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using VznBooster.Core.Monitoring;
using VznBooster.Core.Privacy;
using VznBooster.Desktop.Helpers;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed class CoreRow : ObservableObject
{
    private double _percent;
    private string _text = "—";

    public CoreRow(string name) => Name = name;

    public string Name { get; }
    public double Percent { get => _percent; private set => SetProperty(ref _percent, value); }
    public string Text { get => _text; private set => SetProperty(ref _text, value); }

    public void Update(double? percent)
    {
        Percent = percent ?? 0;
        Text = percent is { } p ? $"{p:0}%" : "—";
    }
}

public sealed record ProcessRowData(int Pid, string Name, string KindText, string CpuText, string RamText, string PathText, ICommand? CloseCommand)
{
    public bool HasClose => CloseCommand is not null;
}

/// <summary>
/// Desempenho em tempo real. A coleta (CPU total e por núcleo, frequência, RAM e processos) só roda enquanto a página está aberta,
/// fora da thread da interface. Processos protegidos mostram CPU indisponível. Não encerra nem suspende processos nesta versão.
/// GPU e atividade de disco ainda não são coletadas.
/// </summary>
public sealed class PerformanceViewModel : PageViewModel
{
    private const int MaxRows = 30;

    private readonly ISystemSampler _sampler = new WindowsSystemSampler();
    private readonly IProcessSampler _processes = new SystemProcessSampler();
    private readonly ProcessControlService _control;
    private readonly DispatcherTimer _timer = new();
    private readonly Dictionary<int, string?> _paths = new();
    private CpuTimes? _lastTotal;
    private IReadOnlyList<CpuTimes> _lastCores = Array.Empty<CpuTimes>();
    private Dictionary<int, TimeSpan> _lastProcessCpu = new();
    private DateTime _lastSampleUtc;
    private bool _sampling;
    private IReadOnlyList<ProcessSnapshot> _snapshot = Array.Empty<ProcessSnapshot>();
    private IReadOnlyDictionary<int, double> _cpuByPid = new Dictionary<int, double>();

    private string _cpuText = "medindo…";
    private string _frequencyText = string.Empty;
    private string _ramText = "medindo…";
    private string _ramDetail = string.Empty;
    private string _commitText = string.Empty;
    private string _selfText = string.Empty;
    private string _processInfo = string.Empty;
    private string _searchText = string.Empty;
    private bool _sortByCpu = true;

    public PerformanceViewModel(AppServices services) : base("Desempenho", services)
    {
        _control = new ProcessControlService(new SystemProcessController(), services.Log, Environment.ProcessId);
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public ObservableCollection<CoreRow> Cores { get; } = new();
    public ObservableCollection<ProcessRowData> Processes { get; } = new();

    public string CpuText { get => _cpuText; private set => SetProperty(ref _cpuText, value); }
    public string FrequencyText { get => _frequencyText; private set => SetProperty(ref _frequencyText, value); }
    public string RamText { get => _ramText; private set => SetProperty(ref _ramText, value); }
    public string RamDetail { get => _ramDetail; private set => SetProperty(ref _ramDetail, value); }
    public string CommitText { get => _commitText; private set => SetProperty(ref _commitText, value); }
    public string SelfText { get => _selfText; private set => SetProperty(ref _selfText, value); }
    public string ProcessInfo { get => _processInfo; private set => SetProperty(ref _processInfo, value); }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) RenderProcesses(); }
    }

    public bool IsSortCpu
    {
        get => _sortByCpu;
        set { if (value && !_sortByCpu) { _sortByCpu = true; OnPropertyChanged(nameof(IsSortCpu)); OnPropertyChanged(nameof(IsSortRam)); RenderProcesses(); } }
    }

    public bool IsSortRam
    {
        get => !_sortByCpu;
        set { if (value && _sortByCpu) { _sortByCpu = false; OnPropertyChanged(nameof(IsSortCpu)); OnPropertyChanged(nameof(IsSortRam)); RenderProcesses(); } }
    }

    public override void OnActivated()
    {
        _lastTotal = null;
        _lastCores = Array.Empty<CpuTimes>();
        _lastProcessCpu = new Dictionary<int, TimeSpan>();
        _lastSampleUtc = DateTime.UtcNow;
        CpuText = "medindo…";
        RamText = "medindo…";
        // Enumerar processos custa mais que o resto: intervalo mínimo de 2 s.
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(2, Services.Settings.Current.MonitorIntervalSeconds));
        _ = TickAsync();
        _timer.Start();
    }

    public override void OnDeactivated() => _timer.Stop();

    private async Task TickAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var data = await Task.Run(() => (
                Total: _sampler.ReadCpuTimes(),
                Cores: _sampler.ReadPerCoreCpuTimes(),
                Freq: _sampler.ReadProcessorFrequencies(),
                Mem: _sampler.ReadMemory(),
                Procs: _processes.Sample()));
            var now = DateTime.UtcNow;
            var elapsed = now - _lastSampleUtc;
            _lastSampleUtc = now;

            UpdateCpu(data.Total, data.Cores, data.Freq);
            UpdateMemory(data.Mem);

            var cpu = ProcessUsageCalculator.ComputeCpu(_lastProcessCpu, data.Procs, elapsed, Environment.ProcessorCount);
            _lastProcessCpu = data.Procs.Where(p => p.CpuTime is not null).ToDictionary(p => p.Pid, p => p.CpuTime!.Value);
            _snapshot = data.Procs;
            _cpuByPid = cpu;
            UpdateSelf();
            RenderProcesses();
        }
        catch (Exception ex)
        {
            Services.ReportError(ex);
            _timer.Stop();
        }
        finally { _sampling = false; }
    }

    private void UpdateCpu(CpuTimes? total, IReadOnlyList<CpuTimes> cores, IReadOnlyList<ProcessorFrequency>? freq)
    {
        if (total is null) CpuText = "indisponível";
        else
        {
            if (_lastTotal is not null && CpuUsageCalculator.Calculate(_lastTotal, total) is { } c) CpuText = $"{c:0}%";
            _lastTotal = total;
        }

        if (cores.Count == 0) Cores.Clear();
        else
        {
            if (Cores.Count != cores.Count)
            {
                Cores.Clear();
                for (var i = 0; i < cores.Count; i++) Cores.Add(new CoreRow($"Núcleo {i}"));
            }
            for (var i = 0; i < cores.Count; i++)
                Cores[i].Update(_lastCores.Count == cores.Count ? CpuUsageCalculator.Calculate(_lastCores[i], cores[i]) : null);
            _lastCores = cores;
        }

        FrequencyText = freq is { Count: > 0 }
            ? $"Frequência atual reportada pelo Windows: {freq.Average(f => f.CurrentMhz):0} MHz (máxima {freq.Max(f => f.MaxMhz)} MHz). " +
              "Em CPUs modernas o valor pode refletir o estado de energia, não o clock instantâneo."
            : "Frequência: indisponível.";
    }

    private void UpdateMemory(MemorySnapshot? mem)
    {
        if (mem is null)
        {
            RamText = "indisponível";
            RamDetail = string.Empty;
            CommitText = string.Empty;
            return;
        }
        RamText = $"{mem.UsedPercent:0}%";
        RamDetail = $"{ByteFormatter.Format(mem.UsedBytes)} em uso, {ByteFormatter.Format(mem.AvailableBytes)} disponíveis de {ByteFormatter.Format(mem.TotalBytes)}. " +
                    "A memória \"disponível\" inclui o cache do Windows, que não é desperdício.";
        CommitText = mem.CommitUsedBytes is { } used && mem.CommitLimitBytes is { } limit
            ? $"Memória comprometida: {ByteFormatter.Format(used)} de um limite de {ByteFormatter.Format(limit)}."
            : "Memória comprometida: indisponível.";
    }

    private void UpdateSelf()
    {
        var me = _snapshot.FirstOrDefault(p => p.Pid == Environment.ProcessId);
        if (me is null) { SelfText = string.Empty; return; }
        SelfText = _cpuByPid.TryGetValue(me.Pid, out var c)
            ? $"Consumo do próprio VZN Booster: {c:0.0}% de CPU e {ByteFormatter.Format(me.WorkingSetBytes)} de RAM."
            : $"Consumo do próprio VZN Booster: {ByteFormatter.Format(me.WorkingSetBytes)} de RAM (CPU ainda sendo medida).";
    }

    private void RenderProcesses()
    {
        var term = SearchText.Trim();
        var query = _snapshot.Where(p => term.Length == 0
            || p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || p.Pid.ToString().Contains(term, StringComparison.Ordinal));

        var ordered = _sortByCpu
            ? query.OrderByDescending(p => _cpuByPid.TryGetValue(p.Pid, out var c) ? c : -1).ThenByDescending(p => p.WorkingSetBytes)
            : query.OrderByDescending(p => p.WorkingSetBytes);

        Processes.Clear();
        foreach (var p in ordered.Take(MaxRows))
        {
            if (!_paths.TryGetValue(p.Pid, out var path))
            {
                path = ProcessPathResolver.TryGetPath(p.Pid);
                _paths[p.Pid] = path;
            }
            // O botão só existe para processos que o aplicativo pode encerrar (sistema, serviços, segurança e o próprio app são protegidos).
            ICommand? close = ProcessControlService.ProtectionReason(p, Environment.ProcessId) is null
                ? new AsyncRelayCommand(() => CloseProcessAsync(p.Pid, p.Name), Services.ReportError)
                : null;
            Processes.Add(new ProcessRowData(p.Pid, p.Name, KindText(ProcessClassifier.Classify(p)),
                _cpuByPid.TryGetValue(p.Pid, out var cpu) ? $"{cpu:0.0}%" : "—",
                ByteFormatter.Format(p.WorkingSetBytes),
                path is null ? "indisponível" : Services.Privacy.Display(SensitiveCategory.FilePath, path), close));
        }

        ProcessInfo = $"{_snapshot.Count} processos em execução; exibindo {Processes.Count}. CPU aparece como \"—\" para processos protegidos pelo Windows ou ainda sem leitura anterior. " +
                      "A classificação usa só características observáveis e não indica se um processo é seguro ou não.";
    }

    private async Task CloseProcessAsync(int pid, string name)
    {
        var first = new ConfirmRequest("Encerrar programa", $"Pedir que \"{name}\" (PID {pid}) feche?",
            new[]
            {
                "O VZN Booster pede ao programa que feche normalmente, como se você clicasse no X da janela. Nada é forçado neste passo.",
                "Dados não salvos nele podem ser perdidos; muitos programas perguntam se você quer salvar antes de fechar.",
                "Esta ação não é reversível. Processos do sistema, serviços, componentes de segurança e o próprio VZN Booster não podem ser encerrados aqui.",
                "O resultado é registrado nos logs."
            }, "Pedir para fechar");
        if (!Services.Confirm(first))
        {
            Services.SetStatus("Nada foi encerrado.");
            return;
        }

        var result = await _control.CloseAsync(pid, true);
        Services.SetStatus(result.Message);
        if (result.Kind != ProcessActionKind.StillRunning) return;

        var second = new ConfirmRequest("Forçar encerramento", $"Forçar o encerramento de \"{name}\"?",
            new[]
            {
                result.Message,
                "Forçar o encerramento fecha o programa na hora, sem chance de salvar. Dados não salvos serão perdidos e arquivos em uso podem ficar inconsistentes.",
                "Use apenas se o programa estiver travado e você aceitar esse risco."
            }, "Forçar encerramento", "Entendo que dados não salvos serão perdidos.");
        if (!Services.Confirm(second))
        {
            Services.SetStatus("O encerramento forçado foi cancelado.");
            return;
        }
        var forced = await _control.CloseAsync(pid, true, force: true);
        Services.SetStatus(forced.Message);
    }

    private static string KindText(ProcessKind k) => k switch
    {
        ProcessKind.System => "Sistema",
        ProcessKind.Service => "Serviço",
        ProcessKind.Application => "Aplicativo",
        _ => "Segundo plano"
    };
}
