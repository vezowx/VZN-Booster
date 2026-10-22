using System.Diagnostics;

namespace VznBooster.Core.Cleaning;

/// <param name="Samples">Até 20 caminhos de exemplo. Contêm o nome de usuário: mascarar na exibição/exportação.</param>
/// <param name="SkippedCount">Arquivos que sumiram ou não puderam ser lidos durante a varredura.</param>
public sealed record CategoryScanResult(
    CleanCategory Category, bool RootsFound, int ItemCount, long TotalBytes,
    int SkippedCount, IReadOnlyList<string> Samples, TimeSpan Duration);

/// <summary>Somente leitura: nunca apaga nada. O tamanho é uma ESTIMATIVA (o resultado real só existe após a limpeza).</summary>
public sealed class CleanScanner
{
    private const int SampleSize = 20;
    private readonly IRecycleBin _recycleBin;

    public CleanScanner(IRecycleBin? recycleBin = null) => _recycleBin = recycleBin ?? new WindowsRecycleBin();

    public Task<IReadOnlyList<CategoryScanResult>> ScanAsync(
        IReadOnlyList<CleanCategory> categories, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(categories);
        return Task.Run<IReadOnlyList<CategoryScanResult>>(() =>
        {
            var results = new List<CategoryScanResult>();
            foreach (var cat in categories)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(cat.Name);
                results.Add(ScanOne(cat, ct));
            }
            return results;
        }, ct);
    }

    private CategoryScanResult ScanOne(CleanCategory cat, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (cat.IsRecycleBin)
        {
            var info = _recycleBin.Query();
            return info is null
                ? new CategoryScanResult(cat, false, 0, 0, 0, Array.Empty<string>(), sw.Elapsed)
                : new CategoryScanResult(cat, true, (int)Math.Min(int.MaxValue, info.Items), info.Bytes, 0, Array.Empty<string>(), sw.Elapsed);
        }

        var found = cat.Roots.Any(Directory.Exists);
        if (cat.Roots.Count == 0 || !found)
            return new CategoryScanResult(cat, found, 0, 0, 0, Array.Empty<string>(), sw.Elapsed);

        var validator = new CleanPathValidator(cat.Roots);
        int count = 0, skipped = 0;
        long bytes = 0;
        var samples = new List<string>(SampleSize);
        foreach (var f in CleanEnumerator.EnumerateFiles(cat, validator, ct))
        {
            try
            {
                bytes += f.Length;
                count++;
                if (samples.Count < SampleSize) samples.Add(f.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new CategoryScanResult(cat, true, count, bytes, skipped, samples, sw.Elapsed);
    }
}
