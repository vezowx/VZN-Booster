namespace VznBooster.Core.Cleaning;

/// <summary>
/// Enumeração única usada pela varredura E (no futuro) pela exclusão, para que o que foi mostrado na prévia
/// seja exatamente o que pode ser apagado. Não segue links simbólicos/junções e ignora arquivos de sistema.
/// </summary>
public static class CleanEnumerator
{
    public static IEnumerable<FileInfo> EnumerateFiles(CleanCategory category, CleanPathValidator validator, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(validator);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = category.Recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            ReturnSpecialDirectories = false
        };

        foreach (var root in category.Roots)
        {
            if (!Directory.Exists(root)) continue;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) continue;

            foreach (var path in Directory.EnumerateFiles(root, category.FilePattern, options))
            {
                ct.ThrowIfCancellationRequested();
                if (!validator.IsAllowed(path, out _)) continue;
                yield return new FileInfo(path);
            }
        }
    }
}
