using System.Collections.ObjectModel;
using System.Windows.Input;
using VznBooster.Core.Commands;
using VznBooster.Core.Logging;
using VznBooster.Core.Tools;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed record ToolItem(string Name, string Description, ICommand OpenCommand);

public sealed class ToolGroupViewModel
{
    public ToolGroupViewModel(string title, IEnumerable<ToolItem> items)
    {
        Title = title;
        Items = new ObservableCollection<ToolItem>(items);
    }

    public string Title { get; }
    public ObservableCollection<ToolItem> Items { get; }
}

/// <summary>
/// Atalhos para ferramentas nativas do Windows (identificados como atalhos) e verificações de integridade SOMENTE de leitura (SFC /verifyonly e
/// DISM /CheckHealth). Nenhum reparo é executado: exigem administrador, avisam do tempo e do consumo, e podem ser canceladas.
/// </summary>
public sealed class ToolsViewModel : PageViewModel
{
    private readonly ToolLauncher _launcher = new();
    private readonly SystemOperationExecutor _executor;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private string _result = string.Empty;

    public ToolsViewModel(AppServices services) : base("Ferramentas", services)
    {
        _executor = new SystemOperationExecutor(new ProcessCommandRunner(), services.Log, () => services.Elevation.IsElevated);

        foreach (var group in ToolCatalog.GroupOrder)
        {
            var items = ToolCatalog.Shortcuts.Where(t => t.Group == group)
                .Select(t => new ToolItem(t.Name, t.Description, new RelayCommand(() => Open(t.Id)))).ToList();
            if (items.Count > 0) Groups.Add(new ToolGroupViewModel(group, items));
        }

        SfcCommand = new AsyncRelayCommand(() => RunIntegrityAsync("tool.sfc-verify", "Verificar arquivos do sistema (SFC)"), services.ReportError, () => !_busy);
        DismCommand = new AsyncRelayCommand(() => RunIntegrityAsync("tool.dism-check", "Verificar imagem do Windows (DISM)"), services.ReportError, () => !_busy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _busy);
        RestartAsAdminCommand = new RelayCommand(() => AdminFlows.RestartAsAdministrator(services));
    }

    public ObservableCollection<ToolGroupViewModel> Groups { get; } = new();
    public AsyncRelayCommand SfcCommand { get; }
    public AsyncRelayCommand DismCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }

    public bool ShowAdminNotice => !Services.Elevation.IsElevated;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Result { get => _result; private set { if (SetProperty(ref _result, value)) OnPropertyChanged(nameof(HasResult)); } }
    public bool HasResult => _result.Length > 0;

    private void Open(string toolId)
    {
        var r = _launcher.Launch(toolId);
        Services.SetStatus(r.Message);
    }

    private async Task RunIntegrityAsync(string operationId, string title)
    {
        if (!Services.Elevation.IsElevated)
        {
            Services.SetStatus("Esta verificação exige administrador. Reinicie o VZN Booster como administrador (Configurações → Permissões).");
            return;
        }
        OperationCatalog.TryGet(operationId, out var op);
        var request = new ConfirmRequest(title, $"Executar \"{title}\"?",
            new[]
            {
                op?.Description ?? title,
                op?.Warning ?? string.Empty,
                "Esta verificação não repara nada. Se ela indicar problemas, a decisão de reparar é sua e deve ser feita fora do VZN Booster.",
                "Você pode cancelar durante a execução. O resultado é registrado nos logs."
            }.Where(l => l.Length > 0).ToList(), "Executar");
        if (!Services.Confirm(request))
        {
            Services.SetStatus("Nada foi executado.");
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        Result = string.Empty;
        try
        {
            Services.SetStatus($"{title} em andamento… pode levar vários minutos.");
            var entry = await _executor.ExecuteAsync(operationId, true, null, cts.Token);
            var summary = OutputSummary.LastLines(entry.StdOut, 12);
            Result = entry.Result switch
            {
                OperationResult.Success => $"{title}: a ferramenta terminou com código 0 (nenhum problema reportado por ela).\n{summary}",
                OperationResult.Cancelled => $"{title}: cancelada pelo usuário; nenhum resultado parcial é considerado.",
                OperationResult.Refused => $"{title}: recusada. {entry.StdErr}",
                _ => $"{title}: a ferramenta terminou com código {entry.ExitCode?.ToString() ?? "desconhecido"}, o que pode indicar que foram encontrados problemas. " +
                     $"Leia o resumo abaixo e os detalhes em Logs.\n{summary}\n{entry.StdErr}".Trim()
            };
            Services.SetStatus("Verificação finalizada.");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }
}
