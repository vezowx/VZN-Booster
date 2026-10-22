using VznBooster.Desktop.Services;

namespace VznBooster.Desktop.ViewModels;

/// <summary>
/// Fluxo único de atualização: verificar → (se houver versão nova) confirmar → baixar com progresso → aplicar e reiniciar.
/// Segue a mesma filosofia do resto do app: nada é baixado ou instalado sem confirmação explícita.
/// Chame a partir da thread de interface (a janela de confirmação precisa dela); os awaits internos
/// não usam ConfigureAwait(false) de propósito, para voltar ao contexto de UI.
/// </summary>
public static class UpdateFlows
{
    /// <summary>
    /// Verifica e, se houver novidade, oferece a atualização. Retorna uma mensagem curta do resultado
    /// (também enviada à barra de status). Com silentWhenUpToDate = true (abertura do app), "já está
    /// atualizado" e falhas de rede não incomodam o usuário.
    /// </summary>
    public static async Task<string> CheckInteractivelyAsync(AppServices services, bool silentWhenUpToDate)
    {
        var result = await services.Updates.CheckAsync();

        switch (result.Status)
        {
            case UpdateCheckStatus.NotInstalled:
                return Finish(services, silentWhenUpToDate, null,
                    "Atualizações automáticas só funcionam na versão instalada pelo Setup. Nesta execução (desenvolvimento) elas ficam desativadas.");

            case UpdateCheckStatus.Failed:
                return Finish(services, silentWhenUpToDate, null,
                    $"Não foi possível verificar atualizações agora ({result.ErrorMessage}). O aplicativo tenta de novo na próxima abertura.");

            case UpdateCheckStatus.UpToDate:
                return Finish(services, silentWhenUpToDate, null, "Você já está na versão mais recente do VZN Booster.");

            case UpdateCheckStatus.UpdateAvailable:
                return await OfferDownloadAsync(services, result);
        }

        return string.Empty;
    }

    private static async Task<string> OfferDownloadAsync(AppServices services, UpdateCheckResult result)
    {
        var version = result.NewVersion ?? "nova";
        var request = new ConfirmRequest("Atualização disponível",
            $"A versão {version} do VZN Booster está disponível. Baixar e instalar agora?",
            new[]
            {
                "O download vem do GitHub Releases oficial do projeto e é verificado pelo Velopack antes de ser instalado.",
                "O aplicativo fecha e reabre já na versão nova. Configurações, logs e backups são preservados.",
                "Se preferir atualizar depois, a oferta aparece de novo na próxima abertura do aplicativo."
            }, "Baixar e instalar");
        if (!services.Confirm(request))
            return Finish(services, false, null, $"Atualização {version} adiada. Ela será oferecida de novo na próxima abertura.");

        try
        {
            var progress = new Progress<int>(p => services.SetStatus($"Baixando a versão {version}… {p}%"));
            await services.Updates.DownloadAsync(result.Info!, progress);
        }
        catch (Exception ex)
        {
            return Finish(services, false, ex,
                $"O download da atualização falhou ({ex.Message}). Nada foi alterado; tente de novo mais tarde.");
        }

        services.SetStatus($"Instalando a versão {version} e reiniciando…");
        services.Updates.ApplyAndRestart(result.Info!);
        return "Instalando a atualização e reiniciando…"; // em geral não chega aqui: o app reinicia
    }

    private static string Finish(AppServices services, bool silent, Exception? error, string message)
    {
        if (error is not null) services.ReportError(error);
        if (!silent) services.SetStatus(message);
        return message;
    }
}
