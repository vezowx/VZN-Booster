using System.Collections.ObjectModel;
using System.Windows.Input;
using VznBooster.Core.Commands;
using VznBooster.Core.Dns;
using VznBooster.Core.Logging;
using VznBooster.Core.Networking;
using VznBooster.Core.Tweaker;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed record FixOption(string Id, string Name, string Description, string Warning, string RiskText, ICommand Command);

public sealed class FindingRow
{
    public FindingRow(NetworkFinding finding, IReadOnlyList<FixOption> fixes)
    {
        Title = finding.Title;
        Evidence = finding.Evidence;
        SeverityKey = finding.Severity.ToString();
        SeverityText = finding.Severity switch { FindingSeverity.Problem => "Problema", FindingSeverity.Warning => "Atenção", _ => "Informativo" };
        Fixes = fixes;
    }

    public string Title { get; }
    public string Evidence { get; }
    public string SeverityKey { get; }
    public string SeverityText { get; }
    public IReadOnlyList<FixOption> Fixes { get; }
    public bool HasFixes => Fixes.Count > 0;
}

/// <summary>
/// "Diagnosticar minha rede": primeiro só mede e lista (priorizado). Correções são oferecidas por achado, nunca aplicadas em sequência cega:
/// cada uma pede confirmação com os efeitos, exige administrador, é medida antes e depois (quando não exige reinício) e é registrada nos logs.
/// </summary>
public sealed class ConnectivityViewModel : ObservableObject
{
    private static readonly string[] AdvancedHighImpact = { "net.winsock-reset", "net.ip-reset" };

    private readonly AppServices _services;
    private readonly NetworkDiagnosticService _diagnostics;
    private readonly NetworkFixService _fixes;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private string _summary = "Clique em \"Diagnosticar minha rede\". Nada é alterado nesta etapa.";
    private string _fixResult = string.Empty;

    public ConnectivityViewModel(AppServices services)
    {
        _services = services;
        var executor = new SystemOperationExecutor(new ProcessCommandRunner(), services.Log, () => services.Elevation.IsElevated);
        _diagnostics = new NetworkDiagnosticService(new SystemAdapterSource(), new PingTestService(new IcmpPingProbe()), new UdpDnsProbe(), new CurrentUserRegistry());
        _fixes = new NetworkFixService(executor, _diagnostics);

        DiagnoseCommand = new AsyncRelayCommand(DiagnoseAsync, services.ReportError, () => !_busy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _busy);
        RestartAsAdminCommand = new RelayCommand(() => AdminFlows.RestartAsAdministrator(services));
        foreach (var id in NetworkFixService.FixIds) AllFixes.Add(MakeFix(id));
    }

    public ObservableCollection<FindingRow> Findings { get; } = new();
    public ObservableCollection<string> VpnProxyLines { get; } = new();
    public ObservableCollection<FixOption> AllFixes { get; } = new();
    public AsyncRelayCommand DiagnoseCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }

    public bool ShowAdminNotice => !_services.Elevation.IsElevated;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string FixResult { get => _fixResult; private set { if (SetProperty(ref _fixResult, value)) OnPropertyChanged(nameof(HasFixResult)); } }
    public bool HasFixResult => _fixResult.Length > 0;
    public bool HasVpnProxyLines => VpnProxyLines.Count > 0;

    private FixOption MakeFix(string id)
    {
        OperationCatalog.TryGet(id, out var op);
        var name = id switch
        {
            "net.dns-flush" => "Limpar cache DNS",
            "net.dhcp-renew" => "Renovar concessão DHCP",
            "net.winsock-reset" => "Redefinir Winsock",
            "net.ip-reset" => "Redefinir parâmetros TCP/IP",
            _ => id
        };
        var risk = op?.Risk switch
        {
            RiskLevel.HighImpact => "Alto impacto",
            RiskLevel.ReversibleChange => "Alteração reversível",
            _ => "Baixo impacto"
        };
        var command = new AsyncRelayCommand(() => ApplyFixAsync(id, name), _services.ReportError, () => !_busy);
        return new FixOption(id, name, op?.Description ?? name, op?.Warning ?? string.Empty, risk, command);
    }

    private async Task DiagnoseAsync()
    {
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        Findings.Clear();
        VpnProxyLines.Clear();
        Summary = "Medindo gateway, DNS, proxy e adaptadores…";
        _services.SetStatus("Diagnosticando a rede…");
        try
        {
            var snapshot = await _diagnostics.CaptureAsync(cts.Token);
            var findings = NetworkDiagnosticService.Evaluate(snapshot);
            foreach (var f in findings)
                Findings.Add(new FindingRow(f, f.SuggestedFixIds.Select(MakeFix).ToList()));
            foreach (var v in snapshot.VpnProxy.Findings) VpnProxyLines.Add(CategoryText(v.Category) + ": " + RedactedDetail(v.Detail));
            OnPropertyChanged(nameof(HasVpnProxyLines));

            var problems = findings.Count(f => f.Severity == FindingSeverity.Problem);
            var warnings = findings.Count(f => f.Severity == FindingSeverity.Warning);
            Summary = findings.Count(f => f.Severity != FindingSeverity.Info) == 0
                ? $"Nenhum problema encontrado nesta medição ({snapshot.CapturedUtc.ToLocalTime():HH:mm:ss}). Uma medição curta não é prova definitiva da qualidade da conexão."
                : $"{problems} problema(s) e {warnings} aviso(s) encontrados ({snapshot.CapturedUtc.ToLocalTime():HH:mm:ss}), do mais grave ao menos grave.";
            _services.SetStatus("Diagnóstico concluído.");
        }
        catch (OperationCanceledException)
        {
            Summary = "Diagnóstico cancelado; nenhum resultado parcial é exibido.";
            _services.SetStatus("Diagnóstico cancelado.");
        }
        finally
        {
            IsBusy = false;
            _cts = null;
        }
    }

    // Proxy/PAC podem conter endereços: passam pela mesma redação dos logs.
    private static string RedactedDetail(string text) => LogRedactor.Redact(text, out _);

    private static string CategoryText(VpnProxyCategory c) => c switch
    {
        VpnProxyCategory.VpnConnectedLikely => "VPN detectada e aparentemente conectada",
        VpnProxyCategory.VpnPossibleUnconfirmed => "Possível VPN, sem confirmação da conexão",
        VpnProxyCategory.ProxyConfigured => "Proxy configurado",
        VpnProxyCategory.NoEvidence => "Nenhuma evidência detectada",
        _ => "Estado inconclusivo"
    };

    private async Task ApplyFixAsync(string id, string name)
    {
        if (!_services.Elevation.IsElevated)
        {
            _services.SetStatus("Esta correção exige administrador. Reinicie o VZN Booster como administrador.");
            return;
        }
        OperationCatalog.TryGet(id, out var op);
        var highImpact = AdvancedHighImpact.Contains(id);
        var lines = new List<string>
        {
            op?.Description ?? name,
            op?.Warning ?? string.Empty,
            "O aplicativo mede o gateway e o DNS antes e depois (quando a correção não exige reinício) e mostra os números lado a lado, sem afirmar que o comando causou a diferença.",
            "O resultado é registrado nos logs."
        };
        if (op?.RequiresRestart == true)
            lines.Add("Esta correção exige REINICIAR o computador para ter efeito completo; não há comparação antes/depois agora.");
        if (id == "net.dns-flush" || id == "net.dhcp-renew")
            lines.Add("Se a conexão cair por alguns segundos, aguarde: o aplicativo mede de novo depois de uma pausa.");

        var request = new ConfirmRequest("Correção de rede", $"Executar \"{name}\"?", lines.Where(l => l.Length > 0).ToList(), "Executar",
            highImpact ? "Entendo que a conexão pode cair e que pode ser necessário reiniciar o computador." : null);
        if (!_services.Confirm(request))
        {
            _services.SetStatus("Nada foi alterado.");
            return;
        }

        IsBusy = true;
        try
        {
            _services.SetStatus($"Executando: {name}…");
            var outcome = await _fixes.ApplyAsync(id, true);
            var word = outcome.Entry.Result switch
            {
                OperationResult.Success => "comando concluído",
                OperationResult.Refused => "recusado",
                OperationResult.Cancelled => "cancelado",
                _ => "falhou"
            };
            FixResult = $"{name}: {word}.\n{outcome.Summary}";
            _services.SetStatus(outcome.Entry.Result == OperationResult.Success ? "Correção executada. Veja os números medidos." : "A correção não foi concluída.");
        }
        finally { IsBusy = false; }
    }
}
