using System.Collections.ObjectModel;
using VznBooster.Core.Cleaning;
using VznBooster.Desktop.Helpers;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed class CleanCategoryRow : ObservableObject
{
    private bool _isSelected;
    private string _resultText = "Não analisada";
    private string _sizeText = string.Empty;

    public CleanCategoryRow(CleanCategory category)
    {
        Category = category;
        _isSelected = category.SelectedByDefault;
        var notes = new List<string>();
        if (category.NeedsAdmin) notes.Add("Pode exigir administrador; itens protegidos serão ignorados.");
        if (category.NeedsAppClosed && category.AppHint is not null) notes.Add(category.AppHint);
        Notes = string.Join(" ", notes);
    }

    public CleanCategory Category { get; }
    public string Name => Category.Name;
    public string Description => Category.Description;
    public string Notes { get; }
    public bool HasNotes => Notes.Length > 0;
    public string? Warning => Category.Warning;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Category.Warning);
    public string RiskKey => Category.Risk.ToString();
    public string RiskText => Category.Risk switch
    {
        CleanRisk.Low => "Risco baixo",
        CleanRisk.Moderate => "Risco moderado",
        _ => "Avançado"
    };

    /// <summary>Preenchidos pela última análise; a limpeza só considera categorias analisadas e com itens.</summary>
    public int ItemCount { get; private set; }
    public long TotalBytes { get; private set; }
    public bool Scanned { get; private set; }

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }
    public string SizeText { get => _sizeText; private set => SetProperty(ref _sizeText, value); }

    public void MarkWaiting() { ResultText = "Aguardando…"; SizeText = string.Empty; }

    public void ApplyScan(CategoryScanResult r)
    {
        Scanned = true;
        ItemCount = r.ItemCount;
        TotalBytes = r.TotalBytes;
        if (!r.RootsFound)
        {
            ResultText = "Não encontrada";
            SizeText = string.Empty;
            return;
        }
        ResultText = $"{r.ItemCount} item(ns)" + (r.SkippedCount > 0 ? $" — {r.SkippedCount} não puderam ser lidos" : "");
        SizeText = $"{ByteFormatter.Format(r.TotalBytes)} (estimativa)";
    }

    public void ResetScan(string message)
    {
        Scanned = false;
        ItemCount = 0;
        TotalBytes = 0;
        ResultText = message;
        SizeText = string.Empty;
    }
}

public sealed class CleanGroupViewModel
{
    public CleanGroupViewModel(string title, IEnumerable<CleanCategoryRow> rows)
    {
        Title = title;
        Rows = new ObservableCollection<CleanCategoryRow>(rows);
    }

    public string Title { get; }
    public ObservableCollection<CleanCategoryRow> Rows { get; }
}

public sealed record CleanResultRow(string Name, string Status, string Detail);

public sealed class CleanerViewModel : PageViewModel
{
    private const string AdvancedAck =
        "Entendo o impacto descrito acima e quero limpar também as categorias avançadas selecionadas.";

    private readonly CleanScanner _scanner = new();
    private readonly CleanExecutor _executor;
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private bool _hasScan;
    private string _summary = "Selecione as categorias e clique em Analisar. A análise não apaga nada.";
    private string _resultSummary = string.Empty;

    public CleanerViewModel(AppServices services) : base("Cleaner", services)
    {
        var env = CleanEnvironment.FromSystem();
        _executor = new CleanExecutor(env, services.Log);

        var all = CleanCategoryCatalog.Build(env).Select(c => new CleanCategoryRow(c)).ToList();
        foreach (var group in CleanGroups.Order)
        {
            var rows = all.Where(r => r.Category.Group == group).ToList();
            if (rows.Count > 0) Groups.Add(new CleanGroupViewModel(group, rows));
        }

        ScanCommand = new AsyncRelayCommand(ScanAsync, services.ReportError);
        CleanCommand = new AsyncRelayCommand(CleanAsync, services.ReportError, () => HasScan);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RestartAsAdminCommand = new RelayCommand(() => AdminFlows.RestartAsAdministrator(services));
    }

    public RelayCommand RestartAsAdminCommand { get; }
    public bool ShowAdminNotice => !Services.Elevation.IsElevated;

    public ObservableCollection<CleanGroupViewModel> Groups { get; } = new();
    public ObservableCollection<CleanResultRow> Results { get; } = new();
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand CleanCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool HasScan { get => _hasScan; private set => SetProperty(ref _hasScan, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    public bool HasResults => Results.Count > 0;

    private IEnumerable<CleanCategoryRow> AllRows => Groups.SelectMany(g => g.Rows);

    // ---------------- análise (somente leitura) ----------------

    private async Task ScanAsync()
    {
        var rows = AllRows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
        {
            Summary = "Selecione ao menos uma categoria.";
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        foreach (var r in rows) r.MarkWaiting();
        try
        {
            var progress = new Progress<string>(name => Services.SetStatus($"Analisando: {name}"));
            var results = await _scanner.ScanAsync(rows.Select(r => r.Category).ToList(), progress, cts.Token);

            long totalBytes = 0;
            var totalItems = 0;
            foreach (var res in results)
            {
                var row = rows.First(x => x.Category.Id == res.Category.Id);
                row.ApplyScan(res);
                totalBytes += res.TotalBytes;
                totalItems += res.ItemCount;
            }

            HasScan = true;
            Services.LastCleanerEstimateBytes = totalBytes;
            Summary = $"Estimativa: {ByteFormatter.Format(totalBytes)} em {totalItems} item(ns) nas categorias analisadas. " +
                      "Nada foi apagado. Revise a seleção e use \"Limpar selecionadas\" para continuar.";
            Services.SetStatus("Análise concluída.");
        }
        catch (OperationCanceledException)
        {
            foreach (var r in rows) r.ResetScan("Análise cancelada");
            Summary = "Análise cancelada.";
            Services.SetStatus("Análise cancelada.");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    // ---------------- limpeza (exclusão real, com confirmação) ----------------

    private async Task CleanAsync()
    {
        var chosen = AllRows.Where(r => r.IsSelected && r.Scanned && r.ItemCount > 0).ToList();
        if (chosen.Count == 0)
        {
            Summary = "Nada a limpar: analise e selecione ao menos uma categoria que tenha itens.";
            return;
        }

        var advanced = chosen.Where(r => r.Category.Risk == CleanRisk.Advanced).ToList();
        if (!Services.Confirm(BuildConfirmation(chosen, advanced.Count > 0))) 
        {
            Summary = "Limpeza cancelada antes de começar. Nada foi apagado.";
            Services.SetStatus("Limpeza cancelada.");
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        Results.Clear();
        ResultSummary = string.Empty;
        OnPropertyChanged(nameof(HasResults));
        try
        {
            var progress = new Progress<CleanProgress>(p =>
                Services.SetStatus($"Limpando: {p.Category} — {p.FilesDeleted} removido(s), {ByteFormatter.Format(p.BytesDeleted)}"));
            var report = await _executor.CleanAsync(chosen.Select(r => r.Category.Id).ToList(), true, progress, cts.Token,
                advanced.Select(r => r.Category.Id).ToList());

            ShowReport(report, chosen);
            // Os números anteriores deixaram de valer: exige nova análise antes de outra limpeza.
            foreach (var r in chosen) r.ResetScan("Reanalise para atualizar");
            HasScan = AllRows.Any(r => r.Scanned);
            Services.LastCleanerEstimateBytes = null;
            Services.SetStatus(report.Cancelled ? "Limpeza interrompida." : "Limpeza concluída.");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    private static ConfirmRequest BuildConfirmation(List<CleanCategoryRow> chosen, bool hasAdvanced)
    {
        var lines = new List<string> { "Serão apagados permanentemente os itens das categorias abaixo (valores são estimativas da análise):" };
        foreach (var r in chosen)
        {
            var line = $"• {r.Name} — {r.ItemCount} item(ns), {ByteFormatter.Format(r.TotalBytes)} [{r.RiskText}]";
            if (r.HasWarning) line += $"\n   Atenção: {r.Warning}";
            lines.Add(line);
        }
        lines.Add("A exclusão não é reversível: os arquivos não vão para a Lixeira e não podem ser restaurados por este aplicativo.");
        lines.Add("Arquivos em uso, somente leitura ou sem permissão serão ignorados. Nenhum programa será encerrado.");
        if (chosen.Any(r => r.Category.NeedsAppClosed))
            lines.Add("Categorias de navegador são puladas se o navegador estiver aberto. Senhas, cookies, sessões, histórico, favoritos e extensões não são tocados.");
        if (chosen.Any(r => r.Category.NeedsAdmin))
            lines.Add("Algumas categorias costumam exigir administrador: sem ele, itens protegidos serão ignorados e contados no resultado.");
        lines.Add("Você poderá cancelar durante a execução; o que já tiver sido apagado continuará apagado.");

        return new ConfirmRequest("Confirmar limpeza", "Confirmar a limpeza das categorias selecionadas?", lines,
            "Limpar agora", hasAdvanced ? AdvancedAck : null);
    }

    private void ShowReport(CleanReport report, List<CleanCategoryRow> chosen)
    {
        if (!report.Executed)
        {
            ResultSummary = report.RefusalReason ?? "A limpeza não foi executada.";
            return;
        }

        long removed = 0;
        var files = 0;
        var skipped = 0;
        foreach (var res in report.Results)
        {
            removed += res.BytesDeleted;
            files += res.FilesDeleted;
            skipped += res.FilesSkipped;
            Results.Add(new CleanResultRow(res.Category.Name, StatusText(res.Status),
                $"{res.FilesDeleted} removido(s), {res.FilesSkipped} ignorado(s), {ByteFormatter.Format(res.BytesDeleted)} removidos. {res.Message}"));
        }
        OnPropertyChanged(nameof(HasResults));

        var estimated = chosen.Sum(r => r.TotalBytes);
        var free = report.FreeSpaceDeltaBytes is { } d
            ? $"Variação medida do espaço livre das unidades afetadas: {(d >= 0 ? "+" : "-")}{ByteFormatter.Format(Math.Abs(d))} " +
              "(inclui qualquer outra atividade do PC no mesmo período)."
            : "A variação do espaço livre não pôde ser medida.";
        ResultSummary =
            $"Estimado antes: {ByteFormatter.Format(estimated)}. Removido de fato: {ByteFormatter.Format(removed)} em {files} item(ns); " +
            $"{skipped} ignorado(s). {free}" + (report.Cancelled ? " A limpeza foi interrompida antes de terminar." : string.Empty);
        Summary = "Limpeza finalizada. O resultado está abaixo e foi registrado nos logs.";
    }

    private static string StatusText(CleanStatus s) => s switch
    {
        CleanStatus.Cleaned => "Concluída",
        CleanStatus.NotFound => "Pasta não encontrada",
        CleanStatus.AppRunning => "Pulada: aplicativo aberto",
        CleanStatus.Cancelled => "Cancelada",
        CleanStatus.Refused => "Recusada: exige confirmação avançada",
        _ => "Falhou"
    };
}
