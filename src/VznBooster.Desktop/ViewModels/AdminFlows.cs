using System.Windows;

namespace VznBooster.Desktop.ViewModels;

/// <summary>Fluxo único de "reiniciar como administrador": confirmação, UAC e encerramento da instância atual somente se a nova abrir.</summary>
public static class AdminFlows
{
    public static void RestartAsAdministrator(AppServices services)
    {
        if (services.Elevation.IsElevated)
        {
            services.SetStatus("O VZN Booster já está em execução como administrador.");
            return;
        }

        var request = new ConfirmRequest("Reiniciar como administrador", "Reiniciar o VZN Booster com privilégios de administrador?",
            new[]
            {
                "O aplicativo será fechado e aberto de novo, e o Windows pedirá sua autorização (UAC).",
                "Com administrador, operações como limpar Windows\\Temp, Prefetch e minidumps e criar pontos de restauração deixam de ser recusadas ou ignoradas.",
                "Toda a interface passará a rodar com privilégios elevados nesta sessão. Feche e abra o aplicativo normalmente depois para voltar ao modo comum.",
                "Nada é executado automaticamente: cada operação continua pedindo sua confirmação."
            }, "Reiniciar como administrador");
        if (!services.Confirm(request)) return;

        if (services.Elevation.TryRestartAsAdministrator(out var message))
            Application.Current.Shutdown();
        else
            services.SetStatus(message);
    }
}
