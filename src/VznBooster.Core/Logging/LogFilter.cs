namespace VznBooster.Core.Logging;

/// <summary>Filtros do histórico: texto livre, resultado, categoria e período. Todos opcionais; combinados com E.</summary>
public sealed record LogFilter(string? Text = null, OperationResult? Result = null, string? Category = null, DateTimeOffset? SinceUtc = null)
{
    public IEnumerable<OperationLogEntry> Apply(IEnumerable<OperationLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(Matches);
    }

    public bool Matches(OperationLogEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Result is { } r && e.Result != r) return false;
        if (!string.IsNullOrEmpty(Category) && !string.Equals(e.Category, Category, StringComparison.OrdinalIgnoreCase)) return false;
        if (SinceUtc is { } since && e.TimestampUtc < since) return false;

        var term = Text?.Trim();
        if (string.IsNullOrEmpty(term)) return true;
        return Contains(e.Operation, term) || Contains(e.Category, term) || Contains(e.Command, term)
               || Contains(LogExporter.ResultText(e.Result), term) || Contains(e.Result.ToString(), term)
               || Contains(e.StdOut, term) || Contains(e.StdErr, term) || Contains(e.BackupId, term)
               || e.Arguments.Any(a => Contains(a, term));
    }

    private static bool Contains(string? text, string term) => text is not null && text.Contains(term, StringComparison.OrdinalIgnoreCase);
}
