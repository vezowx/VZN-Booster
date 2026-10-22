using System.Text;

namespace VznBooster.Core.Dns;

public sealed record DnsResponse(bool Valid, int RCode, int AnswerCount, string? Error);

/// <summary>Formato mínimo de consulta/resposta DNS sobre UDP (registro A, classe IN). Funções puras, testáveis sem rede.</summary>
public static class DnsWire
{
    public static byte[] BuildQuery(string name, ushort id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.TrimEnd('.');
        var labels = trimmed.Split('.');
        if (trimmed.Length > 253 || labels.Any(l => l.Length is 0 or > 63 || !l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("Nome de domínio inválido.", nameof(name));

        var bytes = new List<byte> { (byte)(id >> 8), (byte)(id & 0xFF), 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };   // RD=1, QDCOUNT=1
        foreach (var label in labels)
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        bytes.AddRange(new byte[] { 0, 1, 0, 1 });                                                                        // QTYPE=A, QCLASS=IN
        return bytes.ToArray();
    }

    public static DnsResponse Parse(ReadOnlySpan<byte> data, ushort expectedId)
    {
        if (data.Length < 12) return new DnsResponse(false, -1, 0, "Resposta curta demais.");
        var id = (ushort)((data[0] << 8) | data[1]);
        if (id != expectedId) return new DnsResponse(false, -1, 0, "Identificador diferente do esperado.");
        var flags = (data[2] << 8) | data[3];
        if ((flags & 0x8000) == 0) return new DnsResponse(false, -1, 0, "O pacote não é uma resposta.");
        return new DnsResponse(true, flags & 0xF, (data[6] << 8) | data[7], null);
    }
}
