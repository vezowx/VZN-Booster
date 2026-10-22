namespace VznBooster.Core.Cleaning;

/// <summary>Só autoriza exclusão de itens estritamente DENTRO de raízes previamente aprovadas.</summary>
public sealed class CleanPathValidator
{
    private static readonly StringComparison Cmp =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly string[] _roots;

    public CleanPathValidator(IEnumerable<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        _roots = allowedRoots.Select(r =>
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r));
            if (Path.GetPathRoot(full) is { } pr && Path.TrimEndingDirectorySeparator(pr) == full)
                throw new ArgumentException($"A raiz de unidade não pode ser autorizada: {r}", nameof(allowedRoots));
            return full;
        }).ToArray();
        if (_roots.Length == 0) throw new ArgumentException("Nenhuma raiz autorizada.", nameof(allowedRoots));
    }

    public bool IsAllowed(string? path, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) { reason = "Caminho vazio ou inválido."; return false; }
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { reason = "Caminho não pôde ser normalizado."; return false; }

        foreach (var root in _roots)
        {
            if (string.Equals(full, root, Cmp)) { reason = "A própria pasta raiz não pode ser excluída."; return false; }
            if (full.StartsWith(root + Path.DirectorySeparatorChar, Cmp)) return true;
        }
        reason = "Caminho fora das pastas autorizadas.";
        return false;
    }
}
