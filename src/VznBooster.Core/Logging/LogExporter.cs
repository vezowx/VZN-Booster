using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VznBooster.Core.Privacy;

namespace VznBooster.Core.Logging;

/// <summary>
/// Exportação de logs. Por padrão oculta dados pessoais (nome do usuário em caminhos, nome do computador) além do que já é ocultado na gravação
/// (IP, MAC e segredos). A inclusão de dados pessoais é uma escolha explícita. O arquivo informa se houve ocultação.
/// </summary>
public static class LogExporter
{
    private const string Hidden = "******";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string ResultText(OperationResult r) => r switch
    {
        OperationResult.Success => "Sucesso",
        OperationResult.Failed => "Falhou",
        OperationResult.Cancelled => "Cancelada",
        _ => "Recusada"
    };

    public static string MaskPersonal(string text, string? userName, string? machineName)
    {
        var result = DataMasker.MaskPath(text, userName);
        if (!string.IsNullOrWhiteSpace(machineName) && machineName.Length >= 3)
            result = result.Replace(machineName, Hidden, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public static OperationLogEntry Sanitize(OperationLogEntry e, bool includePersonal, string? userName, string? machineName)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (includePersonal) return e;

        var changed = false;
        string? Apply(string? v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            var masked = LogRedactor.Redact(MaskPersonal(v, userName, machineName), out var c);
            changed |= c || !string.Equals(masked, v, StringComparison.Ordinal);
            return masked;
        }

        var args = e.Arguments.Select(a => Apply(a) ?? string.Empty).ToList();
        return e with { Command = Apply(e.Command), Arguments = args, StdOut = Apply(e.StdOut), StdErr = Apply(e.StdErr), Redacted = e.Redacted || changed };
    }

    private static string Header(DateTimeOffset generatedAt, bool includePersonal, int count) =>
        $"Gerado em: {generatedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss} — {count} registro(s)\n" +
        (includePersonal
            ? "Dados pessoais: INCLUÍDOS por escolha do usuário (IPs, MACs e segredos já eram ocultados na gravação)."
            : "Dados pessoais: ocultados (nome do usuário em caminhos e nome do computador). IPs, MACs e segredos já eram ocultados na gravação.");

    public static string ToText(IReadOnlyList<OperationLogEntry> entries, DateTimeOffset generatedAt, bool includePersonal,
        string? userName = null, string? machineName = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sb = new StringBuilder();
        sb.AppendLine("VZN Booster — Exportação de logs");
        sb.AppendLine(Header(generatedAt, includePersonal, entries.Count));
        foreach (var raw in entries)
        {
            var e = Sanitize(raw, includePersonal, userName, machineName);
            sb.AppendLine();
            sb.AppendLine($"[{e.TimestampUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}] {e.Category} — {e.Operation} — {ResultText(e.Result)} — {e.Duration.TotalMilliseconds:0} ms");
            if (!string.IsNullOrEmpty(e.Command)) sb.AppendLine($"  Comando: {e.Command}");
            if (e.Arguments.Count > 0) sb.AppendLine($"  Parâmetros: {string.Join(" | ", e.Arguments)}");
            if (e.ExitCode is { } code) sb.AppendLine($"  Código de saída: {code}");
            if (!string.IsNullOrWhiteSpace(e.StdOut)) sb.AppendLine("  Saída: " + e.StdOut.Trim().Replace("\n", "\n    "));
            if (!string.IsNullOrWhiteSpace(e.StdErr)) sb.AppendLine("  Erro: " + e.StdErr.Trim().Replace("\n", "\n    "));
            if (e.BackupId is not null) sb.AppendLine($"  Backup relacionado: {e.BackupId}");
            sb.AppendLine(e.RestartRequired ? "  Exige reinicialização." : "  Não exige reinicialização.");
            if (e.Redacted) sb.AppendLine("  [dados sensíveis ocultados neste registro]");
        }
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<OperationLogEntry> entries, DateTimeOffset generatedAt, bool includePersonal,
        string? userName = null, string? machineName = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var doc = new
        {
            generatedAt = generatedAt.ToString("O"),
            personalDataIncluded = includePersonal,
            note = Header(generatedAt, includePersonal, entries.Count).Split('\n').Last(),
            entries = entries.Select(e => Sanitize(e, includePersonal, userName, machineName)).ToList()
        };
        return JsonSerializer.Serialize(doc, Json);
    }
}
