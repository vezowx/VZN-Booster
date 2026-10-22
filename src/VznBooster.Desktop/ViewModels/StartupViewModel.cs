using System.Collections.ObjectModel;
using System.Windows.Input;
using VznBooster.Core.Privacy;
using VznBooster.Core.Startup;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed class StartupEntryRow : ObservableObject
{
    private readonly AppServices _services;

    public StartupEntryRow(StartupEntry entry, AppServices services, ICommand toggleCommand)
    {
        Entry = entry;
        _services = services;
        ToggleCommand = toggleCommand;
    }

    public StartupEntry Entry { get; }
    public ICommand ToggleCommand { get; }
    public string Name => Entry.Name;
    public string SourceText => Entry.SourceText;
    public string PublisherText => Entry.Publisher is { Length: > 0 } p ? p : "editor não identificado";
    public string StateText => Entry.Enabled ? "Ativada" : "Desativada";
    public string StateKey => Entry.Enabled ? "Enabled" : "Disabled";
    public string ActionText => Entry.Enabled ? "Desativar" : "Ativar";
    public bool CanChange => Entry.CanChange;
    public string? Note => Entry.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Entry.Note);
    public bool Broken => Entry.ExecutablePath is not null && !Entry.ExecutableExists;

    /// <summary>Linha de comando/caminho com o nome de usuário oculto (categoria "caminhos de arquivos").</summary>
    public string CommandText => _services.Privacy.Display(SensitiveCategory.FilePath, Entry.Command);

    public void RefreshText() => OnPropertyChanged(nameof(CommandText));
}

/// <summary>
/// Programas que iniciam com o Windows (registro do usuário e do computador, pasta Inicializar do usuário). Desativar/ativar usa o mesmo mecanismo do
/// Gerenciador de Tarefas, com backup, verificação, log e reversão. Itens do computador (HKLM) são só leitura. Serviços e tarefas agendadas não são cobertos.
/// Nenhum item é classificado como seguro ou malicioso pelo nome.
/// </summary>
public sealed class StartupViewModel : PageViewModel
{
    private const string SecurityAck = "Entendo que este item parece ser de segurança e quero desativá-lo mesmo assim.";

    private bool _busy;
    private string _summary = "Carregando…";
    private string _lastResult = string.Empty;

    public StartupViewModel(AppServices services) : base("Inicialização", services)
    {
        PathsField = new SensitiveField(services.Privacy, SensitiveCategory.FilePath, () => null);
        services.Privacy.Changed += () => { foreach (var r in Rows) r.RefreshText(); };
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, services.ReportError, () => !_busy);
    }

    public SensitiveField PathsField { get; }
    public ObservableCollection<StartupEntryRow> Rows { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string LastResult { get => _lastResult; private set { if (SetProperty(ref _lastResult, value)) OnPropertyChanged(nameof(HasLastResult)); } }
    public bool HasLastResult => _lastResult.Length > 0;

    public override void OnActivated() => RefreshCommand.Execute(null);

    private async Task RefreshAsync()
    {
        var entries = await Task.Run(() => Services.Startup.Enumerate());
        Rows.Clear();
        foreach (var e in entries)
        {
            StartupEntryRow? row = null;
            var cmd = new AsyncRelayCommand(() => ToggleAsync(row!), Services.ReportError, () => !_busy && e.CanChange);
            row = new StartupEntryRow(e, Services, cmd);
            Rows.Add(row);
        }

        var active = entries.Count(e => e.Enabled);
        var broken = entries.Count(e => e.ExecutablePath is not null && !e.ExecutableExists);
        Summary = entries.Count == 0
            ? "Nenhuma entrada de inicialização encontrada."
            : $"{entries.Count} entrada(s): {active} ativada(s), {entries.Count - active} desativada(s)" +
              (broken > 0 ? $"; {broken} apontam para um executável que não existe mais." : ".");
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ToggleAsync(StartupEntryRow row)
    {
        var entry = row.Entry;
        var enable = !entry.Enabled;
        var lines = new List<string>
        {
            enable
                ? $"Ativa a entrada \"{entry.Name}\": o programa voltará a iniciar com o Windows."
                : $"Desativa a entrada \"{entry.Name}\": o programa NÃO iniciará mais com o Windows, mas continua instalado e pode ser aberto manualmente.",
            "Como funciona: a entrada é marcada como desativada no mesmo local usado pelo Gerenciador de Tarefas. Nenhum arquivo e nenhuma entrada são apagados.",
            $"Origem: {entry.SourceText}. Editor: {row.PublisherText}.",
            "Não exige administrador nem reinicialização; o efeito vale a partir do próximo logon.",
            "Se este item for de segurança (antivírus, firewall) ou de acessibilidade, mantenha-o ativo.",
            "Antes de alterar, o estado atual é salvo em backup; depois, o valor é lido de novo para verificar, e o resultado vai para os logs. Para desfazer: \"Restauração\"."
        };
        if (row.Broken) lines.Insert(1, "Atenção: o executável desta entrada não foi encontrado; ela pode estar quebrada (programa removido).");

        var needsAck = !enable && entry.LooksSecurityRelated;
        var request = new ConfirmRequest(enable ? "Ativar na inicialização" : "Desativar na inicialização",
            $"{(enable ? "Ativar" : "Desativar")} \"{entry.Name}\" na inicialização?", lines, enable ? "Ativar" : "Desativar",
            needsAck ? SecurityAck : null);
        if (!Services.Confirm(request))
        {
            Services.SetStatus("Nada foi alterado.");
            return;
        }

        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var outcome = await Services.Startup.SetEnabledAsync(entry.Id, enable, true, needsAck);
            LastResult = $"{entry.Name}: {outcome.Message}";
            Services.SetStatus(outcome.IsSuccess ? "Alteração concluída." : "A alteração não foi concluída.");
        }
        finally { _busy = false; }
        await RefreshAsync();
    }
}
