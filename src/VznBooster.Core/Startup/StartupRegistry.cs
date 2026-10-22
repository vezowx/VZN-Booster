using System.Security;
using Microsoft.Win32;

namespace VznBooster.Core.Startup;

public enum StartupHive { CurrentUser, LocalMachine }

/// <summary>Acesso ao registro de inicialização. Leitura em HKCU e HKLM; escrita SOMENTE em HKCU (HKLM exigiria administrador e não é suportado).</summary>
public interface IStartupRegistry
{
    IReadOnlyList<string> ValueNames(StartupHive hive, string path);
    string? GetString(StartupHive hive, string path, string name);
    byte[]? GetBinary(StartupHive hive, string path, string name);
    void SetBinary(string path, string name, byte[] data);
    void DeleteValue(string path, string name);
}

public sealed class WindowsStartupRegistry : IStartupRegistry
{
    private static void CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase) || path.Contains(".."))
            throw new ArgumentException("Caminho de registro inválido.", nameof(path));
    }

    private static RegistryKey? Open(StartupHive hive, string path, bool writable = false)
    {
        CheckPath(path);
        if (!OperatingSystem.IsWindows()) return null;
        var root = hive == StartupHive.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;
        return root.OpenSubKey(path, writable);
    }

    public IReadOnlyList<string> ValueNames(StartupHive hive, string path)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            using var key = Open(hive, path);
            return key?.GetValueNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return Array.Empty<string>(); }
    }

    public string? GetString(StartupHive hive, string path, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Open(hive, path);
            return key?.GetValue(name, null) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return null; }
    }

    public byte[]? GetBinary(StartupHive hive, string path, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Open(hive, path);
            return key?.GetValue(name, null) as byte[];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { return null; }
    }

    public void SetBinary(string path, string name, byte[] data)
    {
        CheckPath(path);
        ArgumentNullException.ThrowIfNull(data);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O registro só existe no Windows.");
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
                        ?? throw new IOException("Não foi possível abrir a chave de registro para escrita.");
        key.SetValue(name, data, RegistryValueKind.Binary);
    }

    public void DeleteValue(string path, string name)
    {
        CheckPath(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O registro só existe no Windows.");
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
