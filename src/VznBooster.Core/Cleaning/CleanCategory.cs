namespace VznBooster.Core.Cleaning;

public enum CleanRisk { Low, Moderate, Advanced }

public static class CleanGroups
{
    public const string Temporary = "Arquivos temporários";
    public const string WindowsCache = "Cache do Windows";
    public const string Browsers = "Navegadores";
    public const string System = "Sistema e armazenamento";
    public const string Advanced = "Limpeza avançada";

    public static IReadOnlyList<string> Order { get; } = new[] { Temporary, WindowsCache, Browsers, System, Advanced };
}

/// <param name="Roots">Pastas já resolvidas e validadas. Apenas ARQUIVOS dentro delas são candidatos.</param>
/// <param name="Warning">Impacto específico mostrado antes da execução (obrigatório para risco Moderado e Avançado).</param>
/// <param name="IsRecycleBin">Categoria especial: usa a API da Lixeira em vez de pastas.</param>
public sealed record CleanCategory(
    string Id, string Name, string Description,
    IReadOnlyList<string> Roots, string FilePattern, bool Recursive,
    bool NeedsAdmin, bool NeedsAppClosed, string? AppHint, bool SelectedByDefault,
    string Group = CleanGroups.Temporary, CleanRisk Risk = CleanRisk.Low,
    string? Warning = null, bool IsRecycleBin = false);

public sealed record CleanEnvironment(string LocalAppData, string UserTemp, string WindowsDir, string CommonAppData = "")
{
    public static CleanEnvironment FromSystem() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.GetTempPath(),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
}
