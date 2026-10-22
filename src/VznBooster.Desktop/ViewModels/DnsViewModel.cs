using System.Collections.ObjectModel;
using System.Windows.Input;
using VznBooster.Core.Backup;
using VznBooster.Core.Dns;
using VznBooster.Core.Networking;
using VznBooster.Core.Privacy;
using VznBooster.Desktop.Mvvm;

namespace VznBooster.Desktop.ViewModels;

public sealed class DnsAdapterOption : ObservableObject
{
    private readonly Action<DnsAdapterOption> _onSelected;
    private bool _isSelected;

    public DnsAdapterOption(NetworkAdapterInfo info, string label, Action<DnsAdapterOption> onSelected)
    {
        Info = info;
        Label = label;
        _onSelected = onSelected;
    }

    public NetworkAdapterInfo Info { get; }
    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value) && value) _onSelected(this); }
    }
}

public sealed class DnsProfileOption : ObservableObject
{
    private readonly Action<DnsProfileOption> _onSelected;
    private bool _isSelected;

    public DnsProfileOption(DnsProfile profile, Action<DnsProfileOption> onSelected)
    {
        Profile = profile;
        _onSelected = onSelected;
    }

    public DnsProfile Profile { get; }
    public string Label => $"{Profile.Name} — {Profile.Variant}";

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value) && value) _onSelected(this); }
    }
}

public sealed record DnsResultRow(string Name, string Detail);

/// <summary>
/// DNS na seção Network: comparar (consultas reais) e aplicar/restaurar com backup. Alterar DNS exige administrador (recusado antes de
/// criar backup quando o app não está elevado). Os endereços dos perfis ainda não foram conferidos nas fontes oficiais: o diálogo exige reconhecimento.
/// </summary>
public sealed class DnsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DnsComparer _comparer = new(new UdpDnsProbe());
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _comparing;
    private string _planText = "Escolha um adaptador e um perfil e clique em \"Ver plano\". Nada é alterado até você confirmar.";
    private string _verdict = string.Empty;
    private string _lastBackupText = "Nenhum backup de DNS ativo.";
    private string? _activeBackupId;
    private string _adaptersInfo = "Carregando adaptadores…";

    public DnsViewModel(AppServices services)
    {
        _services = services;
        foreach (var p in DnsProfiles.All) Profiles.Add(new DnsProfileOption(p, SelectProfile));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, services.ReportError, () => !_busy);
        PlanCommand = new RelayCommand(ShowPlan, () => !_busy);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, services.ReportError, () => !_busy);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, services.ReportError, () => !_busy && _activeBackupId is not null);
        CompareCommand = new AsyncRelayCommand(CompareAsync, services.ReportError, () => !_busy);
        CancelCompareCommand = new RelayCommand(() => _cts?.Cancel(), () => _comparing);
        RestartAsAdminCommand = new RelayCommand(() => AdminFlows.RestartAsAdministrator(services));
    }

    public ObservableCollection<DnsAdapterOption> Adapters { get; } = new();
    public ObservableCollection<DnsProfileOption> Profiles { get; } = new();
    public ObservableCollection<DnsResultRow> CompareResults { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand PlanCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand CompareCommand { get; }
    public RelayCommand CancelCompareCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }

    public bool ShowAdminNotice => !_services.Elevation.IsElevated;
    public bool IsComparing { get => _comparing; private set { if (SetProperty(ref _comparing, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string PlanText { get => _planText; private set => SetProperty(ref _planText, value); }
    public string Verdict { get => _verdict; private set { if (SetProperty(ref _verdict, value)) OnPropertyChanged(nameof(HasVerdict)); } }
    public bool HasVerdict => _verdict.Length > 0;
    public string LastBackupText { get => _lastBackupText; private set => SetProperty(ref _lastBackupText, value); }
    public string AdaptersInfo { get => _adaptersInfo; private set => SetProperty(ref _adaptersInfo, value); }

    private DnsAdapterOption? SelectedAdapter => Adapters.FirstOrDefault(a => a.IsSelected);
    private DnsProfileOption? SelectedProfile => Profiles.FirstOrDefault(p => p.IsSelected);

    private void SelectProfile(DnsProfileOption chosen)
    {
        foreach (var p in Profiles) if (!ReferenceEquals(p, chosen)) p.IsSelected = false;
    }

    private void SelectAdapter(DnsAdapterOption chosen)
    {
        foreach (var a in Adapters) if (!ReferenceEquals(a, chosen)) a.IsSelected = false;
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task RefreshAsync()
    {
        var adapters = await Task.Run(() => NetworkAdapterReader.ReadAll());
        var previous = SelectedAdapter?.Info.Id;
        Adapters.Clear();
        foreach (var a in adapters.Where(x => x.Status == "Up" && x.InterfaceIndex is not null))
        {
            var mode = a.DnsSource switch
            {
                DnsConfigSource.Automatic => "DNS automático (DHCP)",
                DnsConfigSource.Static => "DNS manual",
                _ => "modo de DNS indeterminado"
            };
            var option = new DnsAdapterOption(a, $"{a.Name} ({a.Type}) — {mode}", SelectAdapter);
            Adapters.Add(option);
            if (a.Id == previous) option.IsSelected = true;
        }
        if (SelectedAdapter is null && Adapters.Count == 1) Adapters[0].IsSelected = true;
        AdaptersInfo = Adapters.Count == 0 ? "Nenhum adaptador ativo com IPv4 encontrado." : $"{Adapters.Count} adaptador(es) ativo(s).";

        var all = await _services.Backups.ListAsync();
        var active = all.FirstOrDefault(r => r.Operation == DnsChangeService.BackupOperation && r.Status.StartsWith("Aplicado", StringComparison.Ordinal));
        _activeBackupId = active?.Id;
        LastBackupText = active is null
            ? "Nenhum backup de DNS ativo."
            : $"Backup de DNS ativo de {active.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} (adaptador {active.Before.GetValueOrDefault("adapterName") ?? "?"}).";
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------------- plano e aplicação ----------------

    private bool TryPlan(out DnsApplyPlan? plan)
    {
        plan = null;
        if (SelectedAdapter is null || SelectedProfile is null)
        {
            PlanText = "Selecione um adaptador e um perfil de DNS.";
            return false;
        }
        try
        {
            plan = _services.Dns.Plan(SelectedAdapter.Info.Id, SelectedProfile.Profile.Id);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            PlanText = ex.Message;
            return false;
        }
    }

    private string CurrentShown(DnsApplyPlan plan) =>
        plan.Adapter.DnsSource == DnsConfigSource.Static
            ? "Manual: " + string.Join(", ", plan.Adapter.StaticDnsServers.Select(s => _services.Privacy.Display(SensitiveCategory.IpAddress, s)))
            : plan.CurrentDescription;

    private void ShowPlan()
    {
        if (!TryPlan(out var plan) || plan is null) return;
        PlanText = $"Adaptador: {plan.Adapter.Name}\nDNS atual: {CurrentShown(plan)}\n" +
                   $"Novo DNS: {plan.Profile.Name} ({string.Join(", ", plan.NewServers)})\n\n" +
                   string.Join("\n", plan.Warnings.Select(w => "• " + w));
    }

    private async Task ApplyAsync()
    {
        if (!TryPlan(out var plan) || plan is null) return;
        ShowPlan();

        if (!_services.Elevation.IsElevated)
        {
            _services.SetStatus("Alterar o DNS exige administrador. Reinicie o VZN Booster como administrador.");
            return;
        }

        var lines = new List<string>
        {
            $"Adaptador: {plan.Adapter.Name}. Somente o DNS IPv4 deste adaptador será alterado.",
            $"DNS atual: {CurrentShown(plan)}",
            $"Novo DNS: {plan.Profile.Name} — {plan.Profile.Variant} ({string.Join(", ", plan.NewServers)}).",
        };
        lines.AddRange(plan.Warnings.Select(w => "• " + w));
        lines.Add("Antes de alterar, o DNS atual é salvo em backup; depois, a configuração é lida de novo para verificar a mudança e o resultado é registrado nos logs.");
        lines.Add("Para desfazer: \"Restaurar DNS anterior\" ou a seção Restauração.");
        if (!plan.Profile.VerifiedAgainstOfficialSource)
            lines.Add($"ATENÇÃO: os endereços deste perfil ainda NÃO foram conferidos pelo VZN Booster na documentação oficial do provedor ({plan.Profile.DocumentationUrl}).");

        var request = new ConfirmRequest("Aplicar DNS", $"Aplicar o DNS \"{plan.Profile.Name}\"?", lines, "Aplicar DNS",
            plan.Profile.VerifiedAgainstOfficialSource ? null : "Conferi os endereços na documentação oficial do provedor e quero aplicar.");
        if (!_services.Confirm(request))
        {
            _services.SetStatus("Nada foi alterado.");
            return;
        }

        SetBusy(true);
        try
        {
            _services.SetStatus("Aplicando DNS…");
            var outcome = await _services.Dns.ApplyAsync(plan, true);
            PlanText = outcome.Message;
            _services.SetStatus(outcome.Success ? "DNS aplicado." : "O DNS não foi aplicado.");
            await RefreshAsync();
        }
        finally { SetBusy(false); }
    }

    private async Task RestoreAsync()
    {
        if (_activeBackupId is not { } backupId) return;
        if (!_services.Elevation.IsElevated)
        {
            _services.SetStatus("Restaurar o DNS exige administrador. Reinicie o VZN Booster como administrador.");
            return;
        }
        var request = new ConfirmRequest("Restaurar DNS", "Restaurar a configuração de DNS anterior?",
            new[]
            {
                "O modo anterior será restaurado: automático (DHCP) ou os servidores manuais que estavam configurados.",
                "O resultado será verificado e registrado nos logs."
            }, "Restaurar DNS");
        if (!_services.Confirm(request)) return;

        SetBusy(true);
        try
        {
            var outcome = await _services.Dns.RestoreAsync(backupId, true);
            PlanText = outcome.Message;
            _services.SetStatus(outcome.Success ? "DNS restaurado." : "O DNS não foi restaurado.");
            await RefreshAsync();
        }
        finally { SetBusy(false); }
    }

    // ---------------- comparação ----------------

    private async Task CompareAsync()
    {
        var candidates = new List<DnsCandidate>();
        foreach (var p in DnsProfiles.All) candidates.Add(new DnsCandidate(p.Id, p.Name, p.IPv4[0]));
        var current = SelectedAdapter?.Info.DnsServers.FirstOrDefault(s => s.Contains('.'));
        if (current is not null && candidates.All(c => c.Server != current))
            candidates.Insert(0, new DnsCandidate("current", "DNS atual do adaptador", current));

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsComparing = true;
        SetBusy(true);
        CompareResults.Clear();
        Verdict = string.Empty;
        try
        {
            var progress = new Progress<string>(name => _services.SetStatus($"Testando DNS: {name}"));
            var cmp = await _comparer.RunAsync(candidates, 3, TimeSpan.FromSeconds(2), progress, cts.Token);
            foreach (var r in cmp.Results.OrderBy(r => r.Stats.AvgMs ?? double.MaxValue))
            {
                static string Ms(double? v) => v is null ? "—" : $"{v:0.0} ms";
                CompareResults.Add(new DnsResultRow(r.Candidate.Name,
                    $"média {Ms(r.Stats.AvgMs)} • mín {Ms(r.Stats.MinMs)} • máx {Ms(r.Stats.MaxMs)} • jitter {Ms(r.Stats.JitterMs)} • " +
                    $"falhas {r.Failures}/{r.Queries}"));
            }
            Verdict = $"{(cmp.Kind == MeasurementKind.Measured ? "MEDIDO" : "INDISPONÍVEL")} em {cmp.StartedUtc.ToLocalTime():HH:mm:ss} — " +
                      $"{DnsComparer.Domains.Count * 3} consultas por servidor. {cmp.Verdict}";
            _services.SetStatus("Comparação concluída.");
        }
        catch (OperationCanceledException)
        {
            Verdict = "Comparação cancelada; nenhum resultado parcial é exibido.";
            _services.SetStatus("Comparação cancelada.");
        }
        finally
        {
            IsComparing = false;
            SetBusy(false);
            _cts = null;
        }
    }
}
