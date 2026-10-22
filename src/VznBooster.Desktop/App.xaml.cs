
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
    private LicenseService? _license;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Mantém o aplicativo aberto enquanto a licença é verificada.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            ShowError(args.Exception);
            args.Handled = true;
        };

        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VznBooster");

        // ---------- LICENÇA ----------
        _license = new LicenseService(baseDir);

        _license.Client.SessionEnded += (_, args) => Dispatcher.Invoke(() =>
        {
            MessageBox.Show(
                args.Message,
                "VZN Booster — licença",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Shutdown();
        });

        if (!await EnsureLicensedAsync(_license))
        {
            Shutdown();
            return;
        }
        // -----------------------------

        var settings = new SettingsService(
            new SettingsStore(Path.Combine(baseDir, "settings.json")));

        var privacy = new PrivacyService(settings);

        var log = new JsonlOperationLog(
            Path.Combine(baseDir, "logs", "operations.jsonl"));

        var backups = new BackupStore(
            Path.Combine(baseDir, "backups"));

        var tweaks = new TweakEngine(
            new CurrentUserRegistry(),
            backups,
            log);

        var elevation = new ElevationService();
        var updates = new UpdateService();

        var dns = new DnsChangeService(
            new SystemOperationExecutor(
                new ProcessCommandRunner(),
                log,
                () => elevation.IsElevated),
            backups,
            new SystemAdapterSource());

        var startupRegistry = new WindowsStartupRegistry();

        var startup = new StartupEngine(
            startupRegistry,
            new StartupEnumerator(
                startupRegistry,
                userStartupFolder: Environment.GetFolderPath(
                    Environment.SpecialFolder.Startup)),
            backups,
            log);

        var services = new AppServices(
            settings,
            privacy,
            log,
            backups,
            tweaks,
            elevation,
            dns,
            startup,
            updates)
        {
            ReportError = ShowError
        };

        var window = new MainWindow
        {
            DataContext = new MainViewModel(services)
        };

        services.Confirm = request =>
            new ConfirmDialog(request)
            {
                Owner = window
            }.ShowDialog() == true;

        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        _ = AutoCheckForUpdatesAsync(services);
    }

    private static async Task<bool> EnsureLicensedAsync(
        LicenseService license)
    {
        string? aviso = null;
        var salva = license.LoadSavedKey();

        if (salva is not null)
        {
            try
            {
                var (ok, msg) = await license.LoginAsync(
                    salva,
                    confirmMove: _ => false);

                if (ok)
                    return true;

                aviso = msg;
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                aviso = "Sem conexão com o servidor de licenças. Verifique sua internet.";
            }
        }

        return new LicenseWindow(
            license,
            salva,
            aviso).ShowDialog() == true;
    }

    /// <summary>
    /// Verifica atualizações em segundo plano após a interface inicializar.
    /// </summary>
    private static async Task AutoCheckForUpdatesAsync(AppServices services)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));

        try
        {
            await UpdateFlows.CheckInteractivelyAsync(
                services,
                silentWhenUpToDate: true);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    /// <summary>
    /// Exibe erros com caminhos e dados sensíveis mascarados.
    /// </summary>
    private static void ShowError(Exception ex)
    {
        var message = LogRedactor.Redact(
            DataMasker.MaskPath(ex.Message, Environment.UserName),
            out _);

        MessageBox.Show(
            $"Ocorreu um erro: {message}\n\n" +
            $"Tipo: {ex.GetType().Name}\n" +
            "Detalhes técnicos foram gravados em " +
            "%LOCALAPPDATA%\\VznBooster\\logs\\crash.log.",
            "VZN Booster",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "VznBooster",
                "logs");

            Directory.CreateDirectory(dir);

            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {ex}" +
                $"{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logEx) when (
            logEx is IOException or UnauthorizedAccessException)
        {
            // Não foi possível gravar o log.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _license?.Dispose();
        base.OnExit(e);
    }
}
