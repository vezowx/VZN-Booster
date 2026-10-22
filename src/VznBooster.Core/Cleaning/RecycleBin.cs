using System.Runtime.InteropServices;

namespace VznBooster.Core.Cleaning;

public sealed record RecycleBinInfo(long Items, long Bytes);

public interface IRecycleBin
{
    /// <summary>Null se a consulta não for possível.</summary>
    RecycleBinInfo? Query();

    /// <summary>Esvazia a Lixeira de todas as unidades. Retorna o HRESULT (0 = sucesso). Não é reversível.</summary>
    int Empty();
}

#pragma warning disable CS0649 // campos preenchidos pelo código nativo
/// <summary>Lixeira via shell32 (SHQueryRecycleBin / SHEmptyRecycleBin), sem confirmação do shell: a confirmação é feita pelo aplicativo.</summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    private const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;
    private const int ENotImpl = unchecked((int)0x80004001);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);

    public RecycleBinInfo? Query()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? new RecycleBinInfo(info.i64NumItems, info.i64Size) : null;
    }

    public int Empty() =>
        OperatingSystem.IsWindows() ? SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound) : ENotImpl;
}
#pragma warning restore CS0649
