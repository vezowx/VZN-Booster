using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using VznBooster.Core.Commands;

namespace VznBooster.Desktop.Services;

/// <summary>
/// Privilégios do processo atual. O aplicativo inicia SEM administrador; a elevação é uma escolha explícita do usuário
/// (reiniciar como administrador), mostrando antes o aviso do Windows (UAC). Um auxiliar elevado só para a operação
/// (sem elevar a interface inteira) ainda não existe.
/// </summary>
public sealed class ElevationService
{
    private const int ErrorCancelled = 1223;

    public bool IsElevated { get; } = ProcessElevation.IsProcessElevated();

    public bool TryRestartAsAdministrator(out string message)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            message = "Não foi possível identificar o executável atual para reiniciar.";
            return false;
        }

        var arguments = string.Empty;
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Executando por "dotnet run": o processo é o host do .NET; reabrir exige o caminho da DLL do aplicativo.
            var dll = Environment.GetCommandLineArgs().FirstOrDefault();
            if (dll is null || !dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                message = "Para elevar durante o desenvolvimento, abra o terminal como administrador e rode o aplicativo de novo.";
                return false;
            }
            arguments = $"\"{dll}\"";
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            message = string.Empty;
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            message = "A elevação foi cancelada no aviso do Windows (UAC). O aplicativo continua sem privilégios de administrador.";
            return false;
        }
        catch (Win32Exception ex)
        {
            message = $"Não foi possível reiniciar como administrador (código {ex.NativeErrorCode}).";
            return false;
        }
    }
}
