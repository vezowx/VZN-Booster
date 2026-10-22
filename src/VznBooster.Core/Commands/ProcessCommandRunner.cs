using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VznBooster.Core.Commands;

/// <summary>Executa um executável SEM shell, com ArgumentList, timeout e cancelamento. Nomes simples resolvem em System32.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var path = Path.IsPathRooted(fileName) ? fileName : Path.Combine(Environment.SystemDirectory, fileName);
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var enc = OemEncoding();
        if (enc is not null) { psi.StandardOutputEncoding = enc; psi.StandardErrorEncoding = enc; }
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }

        // Algumas ferramentas (ex.: sfc.exe) escrevem em UTF-16; ao redirecionar, sobram caracteres nulos entre as letras.
        var stdout = (await outTask.ConfigureAwait(false)).Replace("\0", string.Empty);
        var stderr = (await errTask.ConfigureAwait(false)).Replace("\0", string.Empty);
        sw.Stop();
        return new CommandResult(timedOut ? -1 : process.ExitCode, stdout, stderr, sw.Elapsed, timedOut);
    }

    // Ferramentas de console do Windows escrevem na página de código OEM (ex.: 850), não em UTF-8.
    private static Encoding? OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cp = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            return cp > 0 ? Encoding.GetEncoding(cp) : null;
        }
        catch (ArgumentException) { return null; }
    }
}
