using VznBooster.Core.Formatting;
using VznBooster.Core.Monitoring;

namespace VznBooster.Core.Recommendations;

public sealed record SystemSnapshot(double? CpuPercent, MemorySnapshot? Memory, IReadOnlyList<DriveSnapshot> Drives);

public sealed record Recommendation(
    string Id, string Title, string Evidence, string Action, string ExpectedBenefit,
    string Risk, bool NeedsAdmin, string? TargetSection);

/// <summary>
/// Recomendações baseadas SOMENTE em dados medidos. Cada regra explica a evidência, a ação, o benefício esperado e o risco.
/// Nenhuma ação é executada automaticamente.
/// </summary>
public static class RecommendationEngine
{
    private const long TenGb = 10L * 1024 * 1024 * 1024;
    private const long CleanerThreshold = 500L * 1024 * 1024;

    public static IReadOnlyList<Recommendation> Evaluate(SystemSnapshot s, long? cleanerEstimateBytes)
    {
        ArgumentNullException.ThrowIfNull(s);
        var list = new List<Recommendation>();

        foreach (var d in s.Drives)
        {
            if (d.TotalBytes <= 0) continue;
            if (d.FreePercent < 10 || d.FreeBytes < TenGb)
                list.Add(new Recommendation($"disk.low:{d.Name}", $"Pouco espaço livre em {d.Name}",
                    $"{ByteSize.Format(d.FreeBytes)} livres de {ByteSize.Format(d.TotalBytes)} ({d.FreePercent:0.#}%).",
                    "Analise o Cleaner para ver quanto pode ser recuperado e revise também arquivos grandes que você não usa mais.",
                    "Mais espaço livre ajuda atualizações, cache e arquivos temporários do Windows; o ganho de velocidade, se houver, depende do cenário.",
                    "Baixo: a análise não apaga nada. A exclusão não é reversível e só ocorre com sua confirmação.",
                    false, "Cleaner"));
        }

        if (s.Memory is { } m && m.UsedPercent >= 85)
            list.Add(new Recommendation("memory.high", "Uso de memória elevado",
                $"{m.UsedPercent:0}% da RAM em uso ({ByteSize.Format(m.UsedBytes)} de {ByteSize.Format(m.TotalBytes)}). " +
                "O cache do Windows não entra nessa conta: é memória realmente ocupada.",
                "Veja no Gerenciador de Tarefas quais aplicativos mantêm esse consumo e feche os que não precisa. Se o uso alto for constante, avalie mais RAM.",
                "Menos pressão de memória reduz uso de disco por paginação; o ganho depende dos aplicativos em uso.",
                "Nenhum: recomendação informativa. O VZN Booster não encerra processos automaticamente.",
                false, null));

        if (cleanerEstimateBytes is { } est && est >= CleanerThreshold)
            list.Add(new Recommendation("cleaner.potential", "Há espaço recuperável no Cleaner",
                $"A última análise estimou {ByteSize.Format(est)} (estimativa; o valor real só é conhecido após a limpeza).",
                "Abra o Cleaner, revise as categorias e limpe apenas as que escolher.",
                "Espaço em disco liberado; não há promessa de ganho de desempenho.",
                "Baixo nas categorias padrão. A exclusão não é reversível.",
                false, "Cleaner"));

        return list;
    }
}
