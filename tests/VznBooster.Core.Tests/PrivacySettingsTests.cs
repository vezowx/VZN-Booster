using VznBooster.Core.Logging;
using VznBooster.Core.Privacy;
using VznBooster.Core.Settings;
using Xunit;

namespace VznBooster.Core.Tests;

public class DataMaskerPrivacyTests
{
    [Fact]
    public void Ipv6_e_mascarado_por_completo()
    {
        Assert.Equal("****:****:****:****:****:****:****:****", DataMasker.MaskIPv6("2804:14c:1234::1"));
        Assert.Equal("****:****:****:****:****:****:****:****", DataMasker.MaskAddress("fe80::1%12"));
        Assert.Equal("[valor oculto]", DataMasker.MaskIPv6("192.168.0.1"));
    }

    [Fact]
    public void Caminho_oculta_pasta_do_usuario_mas_preserva_perfis_compartilhados()
    {
        Assert.Equal(@"C:\Users\******\AppData\Local\VznBooster", DataMasker.MaskPath(@"C:\Users\vezowx\AppData\Local\VznBooster"));
        Assert.Equal(@"C:\Users\Public\x.txt", DataMasker.MaskPath(@"C:\Users\Public\x.txt"));
        Assert.Equal(@"D:\backup\******\f.txt", DataMasker.MaskPath(@"D:\backup\vezowx\f.txt", "vezowx"));
        Assert.Equal(@"D:\ab\f.txt", DataMasker.MaskPath(@"D:\ab\f.txt", "ab"));   // nome curto demais: não arrisca trocar texto comum
        Assert.Equal(string.Empty, DataMasker.MaskPath(null));
    }

    [Fact]
    public void Nomes_nao_revelam_tamanho_nem_caracteres()
    {
        Assert.Equal("******", DataMasker.MaskName("DESKTOP-ABC123"));
        Assert.Equal("******", DataMasker.MaskName("vz"));
        Assert.Equal(string.Empty, DataMasker.MaskName(""));
    }
}

public class LogRedactorIpv6Tests
{
    [Fact]
    public void Oculta_ipv6_comprimido_e_completo()
    {
        var r = LogRedactor.Redact("addr fe80::a1b2:c3d4:e5f6:1234%12 e 2001:0db8:85a3:0000:0000:8a2e:0370:7334 fim", out var changed);
        Assert.True(changed);
        Assert.DoesNotContain("a1b2", r);
        Assert.DoesNotContain("8a2e", r);
    }

    [Fact]
    public void Nao_altera_horario_nem_chamada_estatica()
    {
        var text = "[System.Net.Dns]::GetHostAddresses às 12:34:56";
        Assert.Equal(text, LogRedactor.Redact(text, out var changed));
        Assert.False(changed);
    }
}

public class AppSettingsTests
{
    [Fact]
    public void Padroes_priorizam_privacidade()
    {
        var d = AppSettings.Default;
        Assert.True(d.HideSensitiveData);
        Assert.Equal(RevealMode.AutoHideAfterDelay, d.RevealMode);
        Assert.False(d.ReduceAnimations);
    }

    [Fact]
    public void Normalize_limita_valores()
    {
        var n = (AppSettings.Default with { RevealSeconds = 9999, MonitorIntervalSeconds = 0 }).Normalize();
        Assert.Equal(300, n.RevealSeconds);
        Assert.Equal(1, n.MonitorIntervalSeconds);
        Assert.Equal(5, (AppSettings.Default with { RevealSeconds = 1 }).Normalize().RevealSeconds);
    }

    [Fact]
    public void Salva_e_carrega()
    {
        using var t = new TempDir();
        var store = new SettingsStore(Path.Combine(t.Dir, "sub", "settings.json"));
        var s = AppSettings.Default with { HideSensitiveData = false, RevealMode = RevealMode.KeepForSession, RevealSeconds = 45, ReduceAnimations = true };
        store.Save(s);
        var back = store.Load();
        Assert.Equal(s, back);
        Assert.Null(store.LastLoadWarning);
    }

    [Fact]
    public void Arquivo_ausente_usa_padroes_sem_aviso()
    {
        using var t = new TempDir();
        var store = new SettingsStore(Path.Combine(t.Dir, "nao-existe.json"));
        Assert.Equal(AppSettings.Default, store.Load());
        Assert.Null(store.LastLoadWarning);
    }

    [Fact]
    public void Arquivo_corrompido_usa_padroes_e_avisa()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Dir);
        var path = Path.Combine(t.Dir, "settings.json");
        File.WriteAllText(path, "{ isto nao e json");
        var store = new SettingsStore(path);
        Assert.Equal(AppSettings.Default, store.Load());
        Assert.NotNull(store.LastLoadWarning);
    }

    [Fact]
    public void Arquivo_incompleto_nunca_desliga_a_privacidade()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Dir);
        var path = Path.Combine(t.Dir, "settings.json");
        File.WriteAllText(path, "{}");
        var s = new SettingsStore(path).Load();
        Assert.True(s.HideSensitiveData);
        Assert.Equal(30, s.RevealSeconds);
    }

    [Fact]
    public void Valor_invalido_de_enum_cai_nos_padroes()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Dir);
        var path = Path.Combine(t.Dir, "settings.json");
        File.WriteAllText(path, "{\"RevealMode\":\"Inexistente\"}");
        var store = new SettingsStore(path);
        Assert.Equal(AppSettings.Default, store.Load());
        Assert.NotNull(store.LastLoadWarning);
    }
}
