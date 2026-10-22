using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PWFAuth;

namespace VznBooster.Desktop.Services;

public sealed class LicenseService : IDisposable
{
    private readonly string _keyFile;
    public PwfClient Client { get; }

    public LicenseService(string baseDir)
    {
        _keyFile = Path.Combine(baseDir, "license.dat");

        // Segredo vem do build (-p:PwfAppSecret=...). No seu PC, para debug, cai na variável de ambiente.
        var secret = typeof(LicenseService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PwfAppSecret")?.Value;
        if (string.IsNullOrWhiteSpace(secret))
            secret = Environment.GetEnvironmentVariable("PWF_APP_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("App Secret do PWF Auth não configurado neste build.");

        Client = new PwfClient(secret);
    }

    /// <summary>Faz login com a chave. confirmMove pergunta ao usuário se pode mover a licença para este PC.</summary>
    public async Task<(bool Ok, string Message)> LoginAsync(string key, Func<string, bool> confirmMove)
    {
        var login = await Client.LoginAsync(key);

        if (!login.Success &&
            (login.ErrorCode == PwfErrorCodes.HwidMismatch || login.ErrorCode == PwfErrorCodes.DeviceLimit) &&
            confirmMove(login.Message))
        {
            var reset = await Client.ResetHardwareIdAsync(key, "Trocou de PC");
            if (!reset.Success) return (false, reset.Message);
            login = await Client.LoginAsync(key);
        }

        if (!login.Success) return (false, login.Message);

        SaveKey(key);
        Client.StartHeartbeat(); // kill switch: nunca pule isso
        return (true, login.Message);
    }

    public string? LoadSavedKey()
    {
        try
        {
            if (!File.Exists(_keyFile)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_keyFile), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; } // arquivo corrompido ou de outro usuário: pede a chave de novo
    }

    private void SaveKey(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_keyFile)!);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_keyFile, bytes);
    }

    public void Dispose() => Client.Dispose();
}