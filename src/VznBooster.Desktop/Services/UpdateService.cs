using Velopack;
using Velopack.Sources;

namespace VznBooster.Desktop.Services;

public enum UpdateCheckStatus { UpdateAvailable, UpToDate, NotInstalled, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Info, string? ErrorMessage)
{
    public string? NewVersion => Info?.TargetFullRelease.Version.ToString();
}

/// <summary>
/// Atualizações automáticas via Velopack, hospedadas no GitHub Releases do projeto.
/// A verificação NUNCA derruba o aplicativo: qualquer falha (sem internet, limite da API do GitHub,
/// execução fora do Setup) vira um resultado de falha e é tratada pela interface.
/// Em desenvolvimento ("dotnet run"), o app não está instalado pelo Velopack: o status é NotInstalled
/// e nenhuma atualização é oferecida.
/// </summary>
public sealed class UpdateService
{
    // TODO: trocar pela URL do SEU repositório no GitHub (mesmo usada no Publicar-Velopack.ps1).
    public const string RepositoryUrl = "https://github.com/vezowx/VZN-Booster";

    private readonly Func<IUpdateSource> _sourceFactory;

    public UpdateService(string? repositoryUrl = null, Func<IUpdateSource>? sourceFactory = null)
    {
        _sourceFactory = sourceFactory
            ?? (() => new GithubSource(repositoryUrl ?? RepositoryUrl, accessToken: null, prerelease: false));
    }

    /// <summary>True quando o app foi instalado pelo Setup do Velopack (não é o caso em "dotnet run").</summary>
    public bool IsInstalled
    {
        get
        {
            try { return new UpdateManager(_sourceFactory()).IsInstalled; }
            catch (Exception) { return false; }
        }
    }

    public async Task<UpdateCheckResult> CheckAsync()
    {
        try
        {
            var manager = new UpdateManager(_sourceFactory());
            if (!manager.IsInstalled) return new UpdateCheckResult(UpdateCheckStatus.NotInstalled, null, null);
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            return info is null
                ? new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, null)
                : new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, info, null);
        }
        catch (Exception ex)
        {
            // Atualização é conveniência, nunca requisito: sem internet, rate limit do GitHub,
            // release malformada etc. viram uma mensagem, não uma exceção.
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, ex.Message);
        }
    }

    public async Task DownloadAsync(UpdateInfo info, Action<int> progress)
    {
        ArgumentNullException.ThrowIfNull(info);
        var manager = new UpdateManager(_sourceFactory());
        await manager.DownloadUpdatesAsync(info, progress).ConfigureAwait(false);
    }

    /// <summary>Aplica a atualização baixada e reinicia o aplicativo já na versão nova. Não retorna.</summary>
    public void ApplyAndRestart(UpdateInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var manager = new UpdateManager(_sourceFactory());
        manager.ApplyUpdatesAndRestart(info);
    }
}
