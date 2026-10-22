using System.Globalization;

namespace VznBooster.Core.Formatting;

public static class ByteSize
{
    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : string.Format(provider ?? CultureInfo.CurrentCulture, "{0:0.##} {1}", v, units[i]);
    }
}
