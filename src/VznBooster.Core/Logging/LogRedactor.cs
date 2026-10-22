using System.Text.RegularExpressions;
using VznBooster.Core.Privacy;

namespace VznBooster.Core.Logging;

/// <summary>
/// Oculta segredos, IPv4, IPv6 e MAC em texto de log. Limitação conhecida: nomes de computador/usuário
/// presentes na saída de ferramentas NÃO são detectados automaticamente.
/// </summary>
public static class LogRedactor
{
    private const string Octet = @"(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)";
    private static readonly Regex Ipv4 = new($@"\b{Octet}(?:\.{Octet}){{3}}\b", RegexOptions.Compiled);
    private const string Hex = @"[0-9A-Fa-f]{1,4}";
    // IPv6 completo (8 grupos) ou comprimido com "::" entre grupos; não casa horários (12:34:56) nem "Tipo]::Metodo".
    private static readonly Regex Ipv6 = new(
        $@"(?<![\w:])(?:(?:{Hex}:){{7}}{Hex}|(?:{Hex}:){{1,6}}:(?:{Hex}:){{0,5}}{Hex})(?:%\d+)?(?![\w:])",
        RegexOptions.Compiled);
    private static readonly Regex Mac = new(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled);
    private static readonly Regex Secret = new(@"\b(password|passwd|senha|token|secret|apikey)\b\s*[=:]\s*\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Redact(string input, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(input);
        var s = Secret.Replace(input, "$1=[segredo oculto]");
        s = Mac.Replace(s, m => DataMasker.MaskMac(m.Value));
        s = Ipv6.Replace(s, m => DataMasker.MaskIPv6(m.Value));
        s = Ipv4.Replace(s, m => DataMasker.MaskIPv4(m.Value));
        changed = !string.Equals(s, input, StringComparison.Ordinal);
        return s;
    }

    public static OperationLogEntry RedactSensitiveData(this OperationLogEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var changed = false;
        string? Apply(string? v)
        {
            if (string.IsNullOrEmpty(v)) return v;
            var r = Redact(v, out var c);
            changed |= c;
            return r;
        }
        var args = e.Arguments.Select(a => Apply(a)!).ToList();
        return e with
        {
            Command = Apply(e.Command),
            Arguments = args,
            StdOut = Apply(e.StdOut),
            StdErr = Apply(e.StdErr),
            Redacted = e.Redacted || changed
        };
    }
}
