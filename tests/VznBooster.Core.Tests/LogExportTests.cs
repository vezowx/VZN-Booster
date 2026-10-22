using System.Text.Json;
using VznBooster.Core.Logging;
using Xunit;

namespace VznBooster.Core.Tests;

public class LogFilterAndExportTests
{
    private static OperationLogEntry E(string category, string op, OperationResult result, DateTimeOffset when,
        string? stdout = null, string? stderr = null, string[]? args = null, string? command = null) =>
        new(Guid.NewGuid(), when, category, op, command, args ?? Array.Empty<string>(), null, stdout, stderr,
            TimeSpan.FromMilliseconds(12), result, false, null, false);

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static List<OperationLogEntry> Sample() => new()
    {
        E("Cleaner", "cleaner.temp.user", OperationResult.Success, Now.AddHours(-1), "removidos=3"),
        E("Rede", "net.dns-flush", OperationResult.Refused, Now.AddDays(-2), null, "Requer privilégios de administrador"),
        E("Tweaker", "tweak.apply:ui.transparency", OperationResult.Failed, Now.AddDays(-10), "falha", null, new[] { "HKCU\\Software\\X\\Y = 0" }),
        E("Rede", "net.info", OperationResult.Success, Now.AddDays(-40), "ok"),
    };

    [Fact]
    public void Filtra_por_resultado_categoria_periodo_e_texto_combinados()
    {
        var all = Sample();
        Assert.Equal(2, new LogFilter(Result: OperationResult.Success).Apply(all).Count());
        Assert.Equal(2, new LogFilter(Category: "rede").Apply(all).Count());
        Assert.Equal(2, new LogFilter(SinceUtc: Now.AddDays(-7)).Apply(all).Count());
        Assert.Single(new LogFilter(Category: "Rede", Result: OperationResult.Success).Apply(all));
        Assert.Single(new LogFilter(Text: "administrador").Apply(all));          // busca no erro
        Assert.Single(new LogFilter(Text: "ui.transparency").Apply(all));        // busca na operação
        Assert.Single(new LogFilter(Text: "Software\\X").Apply(all));            // busca nos parâmetros
        Assert.Single(new LogFilter(Text: "recusada").Apply(all));              // resultado em português
        Assert.Equal(4, new LogFilter().Apply(all).Count());
    }

    [Fact]
    public void Exportacao_oculta_usuario_e_computador_por_padrao_e_informa_a_ocultacao()
    {
        var e = E("Cleaner", "cleaner.x", OperationResult.Success, Now, @"Pasta C:\Users\vezowx\AppData\Temp no PC DESKTOP-ABC123",
            args: new[] { @"C:\Users\vezowx\x.tmp" });

        var text = LogExporter.ToText(new[] { e }, Now, includePersonal: false, userName: "vezowx", machineName: "DESKTOP-ABC123");

        Assert.DoesNotContain("vezowx", text);
        Assert.DoesNotContain("DESKTOP-ABC123", text);
        Assert.Contains("ocultados", text);
        Assert.Contains("[dados sensíveis ocultados neste registro]", text);
    }

    [Fact]
    public void Incluir_dados_pessoais_e_explicito_e_informado()
    {
        var e = E("Cleaner", "cleaner.x", OperationResult.Success, Now, @"C:\Users\vezowx\a.tmp");
        var text = LogExporter.ToText(new[] { e }, Now, includePersonal: true, userName: "vezowx", machineName: "PC");
        Assert.Contains("vezowx", text);
        Assert.Contains("INCLUÍDOS", text);
    }

    [Fact]
    public void Exportacao_tambem_oculta_segredos_e_ip_que_escaparam_da_gravacao()
    {
        var e = E("Rede", "x", OperationResult.Success, Now, "gateway 192.168.0.1 senha=abc123");
        var text = LogExporter.ToText(new[] { e }, Now, false);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("192.168.0.1", text);
    }

    [Fact]
    public void Json_e_valido_traz_metadados_e_nao_vaza_dados_pessoais()
    {
        var e = E("Cleaner", "cleaner.x", OperationResult.Failed, Now, @"C:\Users\vezowx\a.tmp", "erro", new[] { @"D:\vezowx\b.tmp" });
        var json = LogExporter.ToJson(new[] { e, e }, Now, includePersonal: false, userName: "vezowx", machineName: "PC-TESTE");

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("personalDataIncluded").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Equal("Failed", doc.RootElement.GetProperty("entries")[0].GetProperty("Result").GetString());
        Assert.DoesNotContain("vezowx", json);
    }

    [Fact]
    public void Sanitize_nao_altera_o_original_e_nome_curto_nao_e_trocado()
    {
        var e = E("A", "b", OperationResult.Success, Now, @"C:\ab\arquivo", null, new[] { "x" });
        var s = LogExporter.Sanitize(e, false, "ab", "pc");
        Assert.Equal(@"C:\ab\arquivo", s.StdOut);                 // nomes com menos de 3 caracteres não são substituídos (evita trocar texto comum)
        Assert.False(s.Redacted);
        Assert.Same(e, LogExporter.Sanitize(e, true, "ab", "pc"));
    }
}
