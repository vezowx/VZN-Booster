using VznBooster.Core.Backup;
using VznBooster.Core.Dns;
using VznBooster.Core.Logging;
using VznBooster.Core.Startup;
using VznBooster.Core.Tweaker;
using VznBooster.Desktop.Services;

namespace VznBooster.Desktop.ViewModels;

/// <summary>Serviços compartilhados entre a janela principal e as páginas.</summary>
public sealed class AppServices
{
    public AppServices(SettingsService settings, PrivacyService privacy, IOperationLog log, BackupStore backups,
        TweakEngine tweaks, ElevationService elevation, DnsChangeService dns, StartupEngine startup, UpdateService updates)
    {
        Settings = settings;
        Privacy = privacy;
        Log = log;
        Backups = backups;
        Tweaks = tweaks;
        Elevation = elevation;
        Dns = dns;
        Startup = startup;
        Updates = updates;
    }

    public BackupStore Backups { get; }
    public TweakEngine Tweaks { get; }
    public ElevationService Elevation { get; }
    public DnsChangeService Dns { get; }
    public StartupEngine Startup { get; }
    public UpdateService Updates { get; }

    public SettingsService Settings { get; }
    public PrivacyService Privacy { get; }
    public IOperationLog Log { get; }

    public Action<string> SetStatus { get; set; } = _ => { };
    public Action<Exception> ReportError { get; set; } = _ => { };
    public Action<string> Navigate { get; set; } = _ => { };

    /// <summary>Janela de confirmação detalhada. O padrão recusa (seguro): sem interface configurada, nada destrutivo roda.</summary>
    public Func<ConfirmRequest, bool> Confirm { get; set; } = _ => false;

    /// <summary>Última estimativa de espaço recuperável da análise do Cleaner nesta sessão (null = ainda não analisado).</summary>
    public long? LastCleanerEstimateBytes { get; set; }
}
