using System.Collections.ObjectModel;
using System.Windows.Input;
using VznBooster.Core.Backup;
using VznBooster.Core.Commands;
using VznBooster.Core.Dns;
using VznBooster.Core.Privacy;
using VznBooster.Core.Startup;
using VznBooster.Core.Restore;
using VznBooster.Core.Tweaker;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed class BackupRowViewModel
{
    public BackupRowViewModel(string id, string time, string title, string status, string riskText, string restartText,
        string details, bool canRestore, string note, ICommand restoreCommand)
    {
        Id = id; Time = time; Title = title; Status = status; RiskText = riskText; RestartText = restartText;
        Details = details; CanRestore = canRestore; Note = note; RestoreCommand = restoreCommand;
    }

    public string Id { get; }
    public string Time { get; }
    public string Title { get; }
    public string Status { get; }
    public string RiskText { get; }
    public string RestartText { get; }
    public string Details { get; }
    public bool CanRestore { get; }
    public string Note { get; }
    public bool HasNote => Note.Length > 0;
    public ICommand RestoreCommand { get; }
}

/// <summary>Histórico unificado de backups. Restauração seletiva só de ajustes do Tweaker nesta versão; DNS e pontos de restauração ainda não têm botão.</summary>
public sealed class RestoreViewModel : PageViewModel
{
    private bool _isBusy;
    private string _info = "Carregando backups…";

    private readonly RestorePointService _restorePoints;
    private string _restorePointResult = string.Empty;

    public RestoreViewModel(AppServices services) : base("Restauração", services)
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, services.ReportError, () => !_isBusy);
        _restorePoints = new RestorePointService(
            new SystemOperationExecutor(new ProcessCommandRunner(), services.Log, () => services.Elevation.IsElevated));
        CreateRestorePointCommand = new AsyncRelayCommand(CreateRestorePointAsync, services.ReportError, () => !_isBusy);
        RestartAsAdminCommand = new RelayCommand(() => AdminFlows.RestartAsAdministrator(services));
    }

    public AsyncRelayCommand CreateRestorePointCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }
    public bool ShowAdminNotice => !Services.Elevation.IsElevated;
    public string RestorePointResult
    {
        get => _restorePointResult;
        private set { if (SetProperty(ref _restorePointResult, value)) OnPropertyChanged(nameof(HasRestorePointResult)); }
    }
    public bool HasRestorePointResult => _restorePointResult.Length > 0;

    private async Task CreateRestorePointAsync()
    {
        var request = new ConfirmRequest("Ponto de restauração do Windows", "Criar um ponto de restauração do Windows agora?",
            new[]
            {
                "Será criado um ponto chamado \"VZN Booster\" na unidade do Windows, usando o recurso nativo (Checkpoint-Computer).",
                "Exige administrador e a Proteção do Sistema ativada. O Windows costuma limitar a criação a um ponto a cada 24 horas.",
                "Um ponto de restauração NÃO cobre todos os arquivos, aplicativos ou configurações; os ajustes do Tweaker continuam usando backups próprios.",
                "O resultado é verificado contando os pontos antes e depois, e registrado nos logs."
            }, "Criar ponto");
        if (!Services.Confirm(request))
        {
            Services.SetStatus("Nenhum ponto de restauração foi criado.");
            return;
        }

        _isBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            Services.SetStatus("Criando ponto de restauração…");
            var outcome = await _restorePoints.CreateAsync(true);
            RestorePointResult = outcome.Message;
            Services.SetStatus(outcome.Success ? "Ponto de restauração criado." : "O ponto de restauração não foi criado.");
        }
        finally
        {
            _isBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ObservableCollection<BackupRowViewModel> Rows { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public string Info { get => _info; private set => SetProperty(ref _info, value); }

    public override void OnActivated() => RefreshCommand.Execute(null);

    private async Task RefreshAsync()
    {
        var all = await Services.Backups.ListAsync();
        Rows.Clear();
        foreach (var rec in all) Rows.Add(ToRow(rec));
        Info = all.Count == 0
            ? "Nenhum backup ainda. Os backups são criados automaticamente antes de cada ajuste aplicado."
            : $"{all.Count} backup(s), do mais recente para o mais antigo.";
    }

    private BackupRowViewModel ToRow(BackupRecord rec)
    {
        var time = rec.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        if (rec.Operation == DnsChangeService.BackupOperation)
        {
            rec.Before.TryGetValue("source", out var source);
            rec.Before.TryGetValue("staticServers", out var statics);
            rec.Before.TryGetValue("adapterName", out var adapterName);
            rec.Before.TryGetValue("profile", out var profile);
            var previous = source switch
            {
                "Automatic" => "automático (DHCP)",
                "Static" => "manual — " + string.Join(", ", (statics ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => Services.Privacy.Display(SensitiveCategory.IpAddress, x))),
                _ => "desconhecido"
            };
            var restorable = rec.Status.StartsWith("Aplicado", StringComparison.Ordinal);
            var dnsId = rec.Id;
            var label = adapterName ?? "adaptador";
            ICommand dnsCommand = new AsyncRelayCommand(() => RestoreDnsAsync(dnsId, label), Services.ReportError, () => !_isBusy);
            return new BackupRowViewModel(rec.Id, time, $"DNS — {label}", rec.Status, "Baixo", "Não exige",
                $"DNS anterior: {previous}\nPerfil aplicado: {profile ?? "?"}", restorable,
                restorable ? "Exige administrador para restaurar." : "Este backup não pode mais ser restaurado (já revertido ou falhou).", dnsCommand);
        }

        if (rec.Operation == StartupEngine.BackupOperation)
        {
            rec.Before.TryGetValue("name", out var startupName);
            rec.Before.TryGetValue("source", out var startupSource);
            var wasEnabled = rec.Before.GetValueOrDefault("wasEnabled") == bool.TrueString;
            var startupRestorable = rec.Status.StartsWith("Aplicado", StringComparison.Ordinal);
            var startupId = rec.Id;
            var startupTitle = $"Inicialização — {startupName ?? "entrada"}";
            ICommand startupCommand = new AsyncRelayCommand(async () =>
            {
                _isBusy = true;
                CommandManager.InvalidateRequerySuggested();
                try
                {
                    var outcome = await TweakFlows.RestoreWithPromptsAsync(Services, startupId, startupTitle,
                        (b, c, o) => Services.Startup.RestoreAsync(b, c, o));
                    Services.SetStatus(outcome is null ? "Restauração cancelada. Nada foi alterado." : $"{startupTitle}: {outcome.Message}");
                    await RefreshAsync();
                }
                finally
                {
                    _isBusy = false;
                    CommandManager.InvalidateRequerySuggested();
                }
            }, Services.ReportError, () => !_isBusy);
            return new BackupRowViewModel(rec.Id, time, startupTitle, rec.Status, "Baixo", "Próximo logon",
                $"Origem: {startupSource ?? "?"}\nEstado anterior: {(wasEnabled ? "ativada" : "desativada")} → depois: {(wasEnabled ? "desativada" : "ativada")}",
                startupRestorable,
                startupRestorable ? string.Empty : "Este backup não pode mais ser restaurado (já revertido ou falhou).", startupCommand);
        }

        if (rec.Operation != TweakEngine.BackupOperation)
        {
            return new BackupRowViewModel(rec.Id, time, rec.Operation, rec.Status, "—", "—",
                "Detalhes não exibidos para este tipo de backup.", false, string.Empty, new RelayCommand(() => { }));
        }

        rec.Before.TryGetValue("tweakName", out var name);
        rec.Before.TryGetValue("tweakId", out var tweakId);
        rec.Before.TryGetValue("risk", out var risk);
        rec.Before.TryGetValue("restart", out var restart);
        var def = tweakId is null ? null : TweakCatalog.Find(tweakId);

        var details = new List<string>();
        for (var i = 0; rec.Before.ContainsKey($"reg.{i}.name"); i++)
        {
            var valueName = rec.Before[$"reg.{i}.name"];
            var before = Describe(rec.Before.GetValueOrDefault($"reg.{i}.before"));
            var after = rec.After is { } a ? Describe(a.GetValueOrDefault($"reg.{i}.after")) : "—";
            details.Add($"{valueName}: antes {before} → depois {after}");
        }

        var canRestore = def is not null && rec.Status.StartsWith("Aplicado", StringComparison.Ordinal);
        var riskText = risk switch { "Low" => "Baixo", "Moderate" => "Moderado", "High" => "Alto", _ => "—" };
        var restartText = restart switch { "SignOut" => "Encerrar sessão", "Restart" => "Reiniciar", "None" => "Não exige", _ => "—" };
        var title = name ?? "Ajuste";
        var id = rec.Id;
        ICommand cmd = new AsyncRelayCommand(() => RestoreAsync(id, title), Services.ReportError, () => !_isBusy);
        return new BackupRowViewModel(rec.Id, time, title, rec.Status, riskText, restartText, string.Join("\n", details), canRestore,
            canRestore ? string.Empty : "Este backup não pode mais ser restaurado (já revertido, falhou ou o ajuste saiu do catálogo).", cmd);
    }

    private async Task RestoreDnsAsync(string backupId, string label)
    {
        if (!Services.Elevation.IsElevated)
        {
            Services.SetStatus("Restaurar o DNS exige administrador. Reinicie o VZN Booster como administrador (Configurações → Permissões).");
            return;
        }
        var request = new ConfirmRequest("Restaurar DNS", $"Restaurar o DNS anterior do adaptador \"{label}\"?",
            new[]
            {
                "O modo anterior será restaurado: automático (DHCP) ou os servidores manuais que estavam configurados.",
                "O resultado será verificado e registrado nos logs."
            }, "Restaurar DNS");
        if (!Services.Confirm(request)) return;

        _isBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var outcome = await Services.Dns.RestoreAsync(backupId, true);
            Services.SetStatus(outcome.Message);
            await RefreshAsync();
        }
        finally
        {
            _isBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static string Describe(string? encoded)
    {
        if (encoded is null) return "—";
        try { return ValueSnapshot.Decode(encoded).Describe(); }
        catch (FormatException) { return "ilegível"; }
    }

    private async Task RestoreAsync(string backupId, string title)
    {
        _isBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var outcome = await TweakFlows.RestoreWithPromptsAsync(Services, backupId, title);
            Services.SetStatus(outcome is null ? "Restauração cancelada. Nada foi alterado." : $"{title}: {outcome.Message}");
            await RefreshAsync();
        }
        finally
        {
            _isBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
