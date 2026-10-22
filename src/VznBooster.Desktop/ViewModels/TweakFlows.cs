using VznBooster.Core.Tweaker;

namespace VznBooster.Desktop.ViewModels;

/// <summary>Fluxo de restauração compartilhado (Tweaker, Inicialização e Restauração): confirmação, conflito e resultado, sem duplicar lógica.</summary>
public static class TweakFlows
{
    public static Task<TweakOutcome?> RestoreWithPromptsAsync(AppServices services, string backupId, string title) =>
        RestoreWithPromptsAsync(services, backupId, title, (id, confirmed, over) => services.Tweaks.RestoreAsync(id, confirmed, over));

    public static async Task<TweakOutcome?> RestoreWithPromptsAsync(AppServices services, string backupId, string title,
        Func<string, bool, bool, Task<TweakOutcome>> restore)
    {
        var first = new ConfirmRequest("Restaurar alteração", $"Restaurar o estado anterior de \"{title}\"?",
            new[]
            {
                "O valor salvo antes da alteração será gravado de volta (inclusive remover o valor, se ele não existia antes).",
                "A reversão é completa para esta alteração: apenas os valores dela são tocados.",
                "Se o valor tiver sido alterado depois por outro programa ou por você, o aplicativo avisa antes de sobrescrever.",
                "O resultado será verificado e registrado nos logs."
            }, "Restaurar");
        if (!services.Confirm(first)) return null;

        var outcome = await restore(backupId, true, false);
        if (outcome.Kind != TweakResultKind.Conflict) return outcome;

        var lines = new List<string> { outcome.Message };
        lines.AddRange(outcome.Conflicts.Select(c => "• " + c));
        lines.Add("Se continuar, o valor atual será substituído pelo estado anterior salvo, e a mudança feita depois será perdida.");
        var second = new ConfirmRequest("Conflito encontrado", "O valor mudou depois da alteração. Sobrescrever mesmo assim?", lines,
            "Sobrescrever e restaurar", "Entendo que a alteração feita depois será desfeita.");
        return services.Confirm(second) ? await restore(backupId, true, true) : null;
    }
}
