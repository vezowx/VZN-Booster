using System.IO;
using System.Windows;
using Velopack;
using VznBooster.Core.Backup;
using VznBooster.Core.Commands;
using VznBooster.Core.Dns;
using VznBooster.Core.Logging;
using VznBooster.Core.Networking;
using VznBooster.Core.Privacy;
using VznBooster.Core.Settings;
using VznBooster.Core.Startup;
using VznBooster.Core.Tweaker;
using VznBooster.Desktop.Services;
using VznBooster.Desktop.ViewModels;
using VznBooster.Desktop.Views;

namespace VznBooster.Desktop;

public partial class App : Application
{
    public App()
    {
        // Velopack precisa ser o PRIMEIRO código do processo: ele intercepta os ganchos de
        // instalação/atualização (--veloapp-*) e retorna imediatamente numa execução normal.
        VelopackApp.Build().Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            ShowError(args.Exception);
            args.Handled = true;
        };

        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VznBooster");
        var settings = new SettingsService(new SettingsStore(Path.Combine(baseDir, "settings.json")));
        var privacy = new PrivacyService(settings);
        var log = new JsonlOperationLog(Path.Combine(baseDir, "logs", "operations.jsonl"));
        var backups = new BackupStore(Path.Combine(baseDir, "backups"));
        var tweaks = new TweakEngine(new CurrentUserRegistry(), backups, log);
        var elevation = new ElevationService();
        var updates = new UpdateService();
        var dns = new DnsChangeService(
            new SystemOperationExecutor(new ProcessCommandRunner(), log, () => elevation.IsElevated), backups, new SystemAdapterSource());
        var startupRegistry = new WindowsStartupRegistry();
        var startup = new StartupEngine(startupRegistry,
            new StartupEnumerator(startupRegistry, userStartupFolder: Environment.GetFolderPath(Environment.SpecialFolder.Startup)), backups, log);
        var services = new AppServices(settings, privacy, log, backups, tweaks, elevation, dns, startup, updates) { ReportError = ShowError };

        var window = new MainWindow { DataContext = new MainViewModel(services) };
        services.Confirm = request => new ConfirmDialog(request) { Owner = window }.ShowDialog() == true;
        MainWindow = window;
        window.Show();

        _ = AutoCheckForUpdatesAsync(services);
    }

    /// <summary>
    /// Verificação silenciosa na abertura: espera a interface assentar, consulta o GitHub Releases em
    /// segundo plano e só interrompe o usuário se houver versão nova. "Já atualizado" e falhas de
    /// rede ficam em silêncio (silentWhenUpToDate). Roda no contexto de UI, sem thread extra.
    /// </summary>
    private static async Task AutoCheckForUpdatesAsync(AppServices services)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        try { await UpdateFlows.CheckInteractivelyAsync(services, silentWhenUpToDate: true); }
        catch (Exception ex) { LogCrash(ex); } // rede/API nunca derrubam o app; qualquer surpresa vira crash.log
    }

    /// <summary>A mensagem mostrada ao usuário passa pelas máscaras de privacidade (caminhos com nome de usuário, IPs, MACs).</summary>
    private static void ShowError(Exception ex)
    {
        var message = LogRedactor.Redact(DataMasker.MaskPath(ex.Message, Environment.UserName), out _);
        MessageBox.Show(
            $"Ocorreu um erro: {message}\n\nTipo: {ex.GetType().Name}\nDetalhes técnicos foram gravados em %LOCALAPPDATA%\\VznBooster\\logs\\crash.log.",
            "VZN Booster", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VznBooster", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException) { /* sem onde gravar: o erro já é mostrado ao usuário */ }
    }
}
