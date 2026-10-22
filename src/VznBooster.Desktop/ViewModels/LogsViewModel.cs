using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using VznBooster.Core.Logging;
using VznBooster.Core.Privacy;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed class FilterOption<T> : ObservableObject
{
    private readonly Action<FilterOption<T>> _onSelected;
    private bool _isSelected;

    public FilterOption(string label, T value, Action<FilterOption<T>> onSelected, bool selected = false)
    {
        Label = label;
        Value = value;
        _onSelected = onSelected;
        _isSelected = selected;
    }

    public string Label { get; }
    public T Value { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value) && value) _onSelected(this); }
    }
}

public sealed class LogRowViewModel : ObservableObject
{
    private readonly Func<string, string> _mask;
    private bool _isExpanded;

    public LogRowViewModel(OperationLogEntry e, Func<string, string> mask)
    {
        Entry = e;
        _mask = mask;
        Time = e.TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        ResultKey = e.Result.ToString();
        ResultText = LogExporter.ResultText(e.Result);
        DurationText = e.Duration.TotalSeconds >= 1 ? $"{e.Duration.TotalSeconds:0.0} s" : $"{e.Duration.TotalMilliseconds:0} ms";
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public OperationLogEntry Entry { get; }
    public string Time { get; }
    public string Category => Entry.Category;
    public string Operation => Entry.Operation;
    public string ResultKey { get; }
    public string ResultText { get; }
    public string DurationText { get; }
    public RelayCommand ToggleCommand { get; }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    // Caminhos com o nome de usuário seguem a política de privacidade da interface (olho "Caminhos").
    public string Summary => _mask(Entry.StdOut ?? string.Empty);
    public string Detail => _mask(BuildDetail(Entry));

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Detail));
    }

    private static string BuildDetail(OperationLogEntry e)
    {
        var lines = new List<string> { $"Id da operação: {e.Id}" };
        if (!string.IsNullOrEmpty(e.Command)) lines.Add($"Comando: {e.Command}");
        if (e.Arguments.Count > 0) lines.Add("Parâmetros:\n  " + string.Join("\n  ", e.Arguments));
        if (e.ExitCode is { } code) lines.Add($"Código de saída: {code}");
        if (!string.IsNullOrEmpty(e.StdErr)) lines.Add($"Erro: {e.StdErr}");
        if (e.BackupId is not null) lines.Add($"Backup relacionado: {e.BackupId} (veja a seção Restauração)");
        lines.Add(e.RestartRequired ? "Exige reinicialização." : "Não exige reinicialização.");
        lines.Add(e.Redacted ? "Dados sensíveis foram ocultados neste registro." : "Nenhum dado sensível precisou ser ocultado.");
        return string.Join("\n", lines);
    }
}

/// <summary>Histórico das operações realmente executadas, com filtros (texto, resultado, categoria, período) e exportação. Mostra as 300 mais recentes que atendem aos filtros.</summary>
public sealed class LogsViewModel : PageViewModel
{
    private const int MaxRows = 300;

    private List<OperationLogEntry> _entries = new();
    private List<OperationLogEntry> _filtered = new();
    private string _searchText = string.Empty;
    private string _info = "Carregando…";
    private bool _includePersonal;
    private OperationResult? _result;
    private string? _category;
    private TimeSpan? _period;

    public LogsViewModel(AppServices services) : base("Logs", services)
    {
        PathsField = new SensitiveField(services.Privacy, SensitiveCategory.FilePath, () => null);
        services.Privacy.Changed += () => { foreach (var r in Rows) r.RefreshText(); };

        ResultOptions.Add(new FilterOption<OperationResult?>("Todos", null, o => Pick(ResultOptions, o, v => _result = v), true));
        ResultOptions.Add(new FilterOption<OperationResult?>("Sucesso", OperationResult.Success, o => Pick(ResultOptions, o, v => _result = v)));
        ResultOptions.Add(new FilterOption<OperationResult?>("Falhou", OperationResult.Failed, o => Pick(ResultOptions, o, v => _result = v)));
        ResultOptions.Add(new FilterOption<OperationResult?>("Recusada", OperationResult.Refused, o => Pick(ResultOptions, o, v => _result = v)));
        ResultOptions.Add(new FilterOption<OperationResult?>("Cancelada", OperationResult.Cancelled, o => Pick(ResultOptions, o, v => _result = v)));

        PeriodOptions.Add(new FilterOption<TimeSpan?>("Todo o período", null, o => Pick(PeriodOptions, o, v => _period = v), true));
        PeriodOptions.Add(new FilterOption<TimeSpan?>("Últimas 24 h", TimeSpan.FromHours(24), o => Pick(PeriodOptions, o, v => _period = v)));
        PeriodOptions.Add(new FilterOption<TimeSpan?>("Últimos 7 dias", TimeSpan.FromDays(7), o => Pick(PeriodOptions, o, v => _period = v)));
        PeriodOptions.Add(new FilterOption<TimeSpan?>("Últimos 30 dias", TimeSpan.FromDays(30), o => Pick(PeriodOptions, o, v => _period = v)));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, services.ReportError);
        ExportTextCommand = new AsyncRelayCommand(() => ExportAsync(json: false), services.ReportError, () => _filtered.Count > 0);
        ExportJsonCommand = new AsyncRelayCommand(() => ExportAsync(json: true), services.ReportError, () => _filtered.Count > 0);
    }

    public SensitiveField PathsField { get; }
    public ObservableCollection<LogRowViewModel> Rows { get; } = new();
    public ObservableCollection<FilterOption<OperationResult?>> ResultOptions { get; } = new();
    public ObservableCollection<FilterOption<TimeSpan?>> PeriodOptions { get; } = new();
    public ObservableCollection<FilterOption<string?>> CategoryOptions { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ExportTextCommand { get; }
    public AsyncRelayCommand ExportJsonCommand { get; }

    public string Info { get => _info; private set => SetProperty(ref _info, value); }
    public bool IncludePersonal { get => _includePersonal; set => SetProperty(ref _includePersonal, value); }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
    }

    public override void OnActivated() => RefreshCommand.Execute(null);

    private void Pick<T>(IEnumerable<FilterOption<T>> group, FilterOption<T> chosen, Action<T> assign)
    {
        foreach (var o in group) if (!ReferenceEquals(o, chosen)) o.IsSelected = false;
        assign(chosen.Value);
        ApplyFilter();
    }

    private async Task RefreshAsync()
    {
        var entries = await Services.Log.ReadAllAsync();
        _entries = entries.OrderByDescending(e => e.TimestampUtc).ToList();

        var categories = _entries.Select(e => e.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        if (_category is not null && !categories.Contains(_category, StringComparer.OrdinalIgnoreCase)) _category = null;
        CategoryOptions.Clear();
        CategoryOptions.Add(new FilterOption<string?>("Todas", null, o => Pick(CategoryOptions, o, v => _category = v), _category is null));
        foreach (var c in categories)
            CategoryOptions.Add(new FilterOption<string?>(c, c, o => Pick(CategoryOptions, o, v => _category = v),
                string.Equals(c, _category, StringComparison.OrdinalIgnoreCase)));
        ApplyFilter();
    }

    private LogFilter CurrentFilter() =>
        new(SearchText, _result, _category, _period is { } p ? DateTimeOffset.UtcNow - p : null);

    private void ApplyFilter()
    {
        _filtered = CurrentFilter().Apply(_entries).ToList();
        Rows.Clear();
        foreach (var e in _filtered.Take(MaxRows))
            Rows.Add(new LogRowViewModel(e, s => Services.Privacy.Display(SensitiveCategory.FilePath, s)));
        Info = _entries.Count == 0
            ? "Nenhuma operação registrada ainda."
            : $"{_filtered.Count} de {_entries.Count} registro(s) atendem aos filtros; exibindo {Rows.Count} (máximo de {MaxRows} mais recentes). A exportação inclui todos os {_filtered.Count}.";
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private async Task ExportAsync(bool json)
    {
        if (_filtered.Count == 0) return;

        if (IncludePersonal)
        {
            var confirm = new ConfirmRequest("Exportar logs", "Incluir dados pessoais na exportação?",
                new[]
                {
                    "O arquivo conterá o nome do usuário em caminhos e o nome do computador, quando aparecerem nos registros.",
                    "Endereços IP, MAC e segredos já são ocultados na gravação e continuarão ocultos.",
                    "Para uma exportação sem dados pessoais, cancele e desmarque a opção."
                }, "Incluir e exportar");
            if (!Services.Confirm(confirm)) return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar logs",
            Filter = json ? "JSON (*.json)|*.json" : "Texto (*.txt)|*.txt",
            FileName = json ? "VZN-Booster-logs.json" : "VZN-Booster-logs.txt"
        };
        if (dialog.ShowDialog() != true) return;

        var now = DateTimeOffset.Now;
        var content = json
            ? LogExporter.ToJson(_filtered, now, IncludePersonal, Environment.UserName, Environment.MachineName)
            : LogExporter.ToText(_filtered, now, IncludePersonal, Environment.UserName, Environment.MachineName);
        await File.WriteAllTextAsync(dialog.FileName, content, new UTF8Encoding(false));
        Services.SetStatus(IncludePersonal
            ? $"{_filtered.Count} registro(s) exportados, incluindo dados pessoais por sua escolha."
            : $"{_filtered.Count} registro(s) exportados sem dados pessoais.");
    }
}
