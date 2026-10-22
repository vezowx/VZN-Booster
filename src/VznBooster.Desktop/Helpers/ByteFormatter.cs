using VznBooster.Core.Formatting;

namespace VznBooster.Desktop.Helpers;

public static class ByteFormatter
{
    public static string Format(long bytes) => ByteSize.Format(bytes);
}
