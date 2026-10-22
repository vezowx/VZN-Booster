using System.Globalization;
using Microsoft.Win32;

namespace VznBooster.Core.Tweaker;

/// <summary>Acesso a valores do HKCU. Existe como interface para que o motor seja testado sem tocar no registro real.</summary>
public interface IRegistryAccessor
{
    /// <summary>Lança NotSupportedException se o valor existir com um tipo que não sabemos salvar e restaurar com fidelidade.</summary>
    ValueSnapshot Read(string keyPath, string valueName);
    void Write(string keyPath, string valueName, RegKind kind, string value);
    void Delete(string keyPath, string valueName);
}

public sealed class CurrentUserRegistry : IRegistryAccessor
{
    private static void CheckPath(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath) || keyPath.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase) || keyPath.Contains(".."))
            throw new ArgumentException("Caminho de registro inválido.", nameof(keyPath));
    }

    public ValueSnapshot Read(string keyPath, string valueName)
    {
        CheckPath(keyPath);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O registro só existe no Windows.");
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        var value = key?.GetValue(valueName, null);
        if (key is null || value is null) return ValueSnapshot.Absent;

        var kind = key.GetValueKind(valueName);
        return kind switch
        {
            RegistryValueKind.DWord => new ValueSnapshot(true, RegKind.DWord, Convert.ToString(value, CultureInfo.InvariantCulture)),
            RegistryValueKind.String => new ValueSnapshot(true, RegKind.String, (string)value),
            _ => throw new NotSupportedException($"Tipo de valor de registro não suportado: {kind}.")
        };
    }

    public void Write(string keyPath, string valueName, RegKind kind, string value)
    {
        CheckPath(keyPath);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O registro só existe no Windows.");
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
                        ?? throw new IOException("Não foi possível abrir a chave de registro para escrita.");
        if (kind == RegKind.DWord)
            key.SetValue(valueName, int.Parse(value, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        else
            key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void Delete(string keyPath, string valueName)
    {
        CheckPath(keyPath);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O registro só existe no Windows.");
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
