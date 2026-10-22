using System.Net;
using System.Net.Sockets;

namespace VznBooster.Core.Commands;

/// <summary>
/// Operação já validada. O construtor é interno: só este assembly consegue criá-la, então código externo
/// não consegue pedir ao executor que rode comandos arbitrários.
/// </summary>
public sealed class ValidatedOperation
{
    internal ValidatedOperation(AllowedOperation op) => Operation = op;
    public AllowedOperation Operation { get; }
}

/// <summary>
/// Fábricas de operações de DNS. Os únicos valores variáveis são um índice de interface (inteiro positivo) e
/// endereços IPv4 reescritos na forma canônica após o parse — nada textual do usuário entra no comando.
/// </summary>
public static class DnsOperations
{
    private const string PowerShell = @"WindowsPowerShell\v1.0\powershell.exe";

    public static ValidatedOperation Set(int interfaceIndex, IReadOnlyList<string> ipv4Servers)
    {
        CheckIndex(interfaceIndex);
        ArgumentNullException.ThrowIfNull(ipv4Servers);
        if (ipv4Servers.Count < 1 || ipv4Servers.Count > 4)
            throw new ArgumentException("Informe de 1 a 4 servidores DNS.", nameof(ipv4Servers));

        var canonical = new List<string>();
        foreach (var s in ipv4Servers)
        {
            if (!IPAddress.TryParse(s, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Endereço DNS IPv4 inválido.", nameof(ipv4Servers));
            canonical.Add(ip.ToString());
        }
        var list = string.Join(",", canonical.Select(c => $"'{c}'"));
        var cmd = $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses ({list}) -ErrorAction Stop";
        return Build("dns.set", "Define servidores DNS IPv4 do adaptador.", cmd,
            "Altera o DNS IPv4 do adaptador selecionado. Requer administrador. Reversível pela Restauração.");
    }

    public static ValidatedOperation Reset(int interfaceIndex)
    {
        CheckIndex(interfaceIndex);
        var cmd = $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses -ErrorAction Stop";
        return Build("dns.reset", "Volta o DNS do adaptador para automático (DHCP).", cmd,
            "Volta o DNS do adaptador ao modo automático. Requer administrador.");
    }

    private static void CheckIndex(int i)
    {
        if (i <= 0) throw new ArgumentOutOfRangeException(nameof(i), "Índice de interface inválido.");
    }

    private static ValidatedOperation Build(string id, string description, string command, string warning) =>
        new(new AllowedOperation(id, "DNS", description, PowerShell,
            new[] { "-NoProfile", "-NonInteractive", "-Command", command },
            RiskLevel.ReversibleChange, true, false, warning, TimeSpan.FromSeconds(30)));
}
