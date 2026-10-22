using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace VznBooster.Core.Privacy;

/// <summary>
/// Máscaras de exibição/exportação. O valor mascarado nunca deve ser tratado como o dado completo e a máscara
/// é apenas visual: os valores reais continuam disponíveis internamente para diagnósticos.
/// </summary>
public static class DataMasker
{
    public const string Hidden = "[valor oculto]";
    private const string Stars = "******";
    private const string Ipv6Mask = "****:****:****:****:****:****:****:****";

    private static readonly Regex UserFolder = new(@"(?<=[\\/]Users[\\/])[^\\/]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> SharedProfiles =
        new(StringComparer.OrdinalIgnoreCase) { "Public", "Default", "Default User", "All Users" };

    /// <summary>Faixas privadas mantêm só os dois primeiros octetos (192.168.***.***); IPs públicos ficam totalmente ocultos.</summary>
    public static string MaskIPv4(string? value)
    {
        if (!IPAddress.TryParse(value, out var ip)) return Hidden;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return Ipv6Mask;
        var b = ip.GetAddressBytes();
        var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
        return isPrivate ? $"{b[0]}.{b[1]}.***.***" : "***.***.***.***";
    }

    public static string MaskIPv6(string? value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? Ipv6Mask : Hidden;

    public static string MaskAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out var ip)) return Hidden;
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? Ipv6Mask : MaskIPv4(value);
    }

    public static string MaskMac(string? value)
    {
        var hex = new string((value ?? "").Where(Uri.IsHexDigit).ToArray());
        return hex.Length == 12 ? "**:**:**:**:**:**" : Hidden;
    }

    /// <summary>Oculta nomes (computador, usuário) sem revelar tamanho nem caracteres.</summary>
    public static string MaskName(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Stars;

    /// <summary>Oculta a pasta do perfil em caminhos como C:\Users\nome\... e, opcionalmente, o nome do usuário em qualquer posição.</summary>
    public static string MaskPath(string? path, string? userName = null)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var result = UserFolder.Replace(path, m => SharedProfiles.Contains(m.Value) ? m.Value : Stars);
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3)
            result = result.Replace(userName, Stars, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public static string MaskText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= 2 ? new string('*', value.Length) : $"{value[0]}{new string('*', value.Length - 2)}{value[^1]}";
    }
}
