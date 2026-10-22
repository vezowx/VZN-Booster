using System.Security.Principal;

namespace VznBooster.Core.Commands;

public static class ProcessElevation
{
    public static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
