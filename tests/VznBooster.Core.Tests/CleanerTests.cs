using VznBooster.Core.Cleaning;
using Xunit;

namespace VznBooster.Core.Tests;

public class CleanerTests
{
    private static void Write(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    private static CleanEnvironment Env(TempDir t) => new(
        Path.Combine(t.Dir, "local"), Path.Combine(t.Dir, "usertemp"), Path.Combine(t.Dir, "windows"));

    [Fact]
    public void Catalogo_sempre_traz_categorias_basicas_e_nenhuma_perigosa()
    {
        using var t = new TempDir();
        var ids = CleanCategoryCatalog.Build(Env(t)).Select(c => c.Id).ToList();
        Assert.Contains("temp.user", ids);
        Assert.Contains("temp.windows", ids);
        Assert.Contains("adv.prefetch", ids);                                       // existe, mas como categoria avançada (ver CleanerExpansionTests)
        Assert.DoesNotContain(ids, i => i.Contains("log", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Somente_temp_do_usuario_vem_marcada_por_padrao()
    {
        using var t = new TempDir();
        var selected = CleanCategoryCatalog.Build(Env(t)).Where(c => c.SelectedByDefault).Select(c => c.Id).ToList();
        Assert.Equal(new[] { "temp.user" }, selected);
    }

    [Fact]
    public void Detecta_apenas_perfis_reais_de_navegadores()
    {
        using var t = new TempDir();
        var env = Env(t);
        var chrome = Path.Combine(env.LocalAppData, "Google", "Chrome", "User Data");
        Write(Path.Combine(chrome, "Default", "Cache", "a.bin"), 1);
        Write(Path.Combine(chrome, "Profile 1", "Code Cache", "js", "b.bin"), 1);
        Write(Path.Combine(chrome, "Profile 1", "Cookies", "c.bin"), 1);          // não é pasta de cache
        Write(Path.Combine(chrome, "System Profile", "Cache", "d.bin"), 1);       // não é perfil de usuário
        Write(Path.Combine(env.LocalAppData, "Mozilla", "Firefox", "Profiles", "abc.default", "cache2", "e.bin"), 1);

        var cats = CleanCategoryCatalog.Build(env);
        var def = cats.Single(x => x.Id == "browser.chrome.Default");
        var p1 = cats.Single(x => x.Id == "browser.chrome.Profile 1");
        Assert.Single(def.Roots);
        Assert.Single(p1.Roots);                                                   // só "Code Cache"; "Cookies" não é cache
        Assert.DoesNotContain(cats, x => x.Id.Contains("System Profile"));
        Assert.True(def.NeedsAppClosed);
        Assert.Contains("Chrome", def.AppHint);
        Assert.DoesNotContain(cats, x => x.Id.StartsWith("browser.edge"));
        Assert.Contains(cats, x => x.Id == "browser.firefox.abc.default");
    }

    [Fact]
    public async Task Varredura_conta_arquivos_e_bytes_sem_apagar_nada()
    {
        using var t = new TempDir();
        var env = Env(t);
        Write(Path.Combine(env.UserTemp, "a.tmp"), 10);
        Write(Path.Combine(env.UserTemp, "sub", "b.tmp"), 20);
        Write(Path.Combine(env.UserTemp, "sub", "deep", "c.tmp"), 30);

        var cat = CleanCategoryCatalog.Build(env).Single(c => c.Id == "temp.user");
        var r = (await new CleanScanner().ScanAsync(new[] { cat })).Single();
        Assert.True(r.RootsFound);
        Assert.Equal(3, r.ItemCount);
        Assert.Equal(60, r.TotalBytes);
        Assert.Equal(3, r.Samples.Count);
        Assert.True(File.Exists(Path.Combine(env.UserTemp, "a.tmp")));
    }

    [Fact]
    public async Task Thumbcache_respeita_padrao_e_nao_recursao()
    {
        using var t = new TempDir();
        var env = Env(t);
        var dir = Path.Combine(env.LocalAppData, "Microsoft", "Windows", "Explorer");
        Write(Path.Combine(dir, "thumbcache_256.db"), 100);
        Write(Path.Combine(dir, "iconcache_256.db"), 50);
        Write(Path.Combine(dir, "sub", "thumbcache_16.db"), 5);

        var cat = CleanCategoryCatalog.Build(env).Single(c => c.Id == "cache.thumbnails");
        var r = (await new CleanScanner().ScanAsync(new[] { cat })).Single();
        Assert.Equal(1, r.ItemCount);
        Assert.Equal(100, r.TotalBytes);
    }

    [Fact]
    public async Task Pasta_inexistente_e_reportada_como_nao_encontrada()
    {
        using var t = new TempDir();
        var cat = CleanCategoryCatalog.Build(Env(t)).Single(c => c.Id == "cache.shaders");
        var r = (await new CleanScanner().ScanAsync(new[] { cat })).Single();
        Assert.False(r.RootsFound);
        Assert.Equal(0, r.ItemCount);
    }

    [Fact]
    public async Task Amostras_sao_limitadas()
    {
        using var t = new TempDir();
        var env = Env(t);
        for (var i = 0; i < 30; i++) Write(Path.Combine(env.UserTemp, $"f{i}.tmp"), 1);
        var cat = CleanCategoryCatalog.Build(env).Single(c => c.Id == "temp.user");
        var r = (await new CleanScanner().ScanAsync(new[] { cat })).Single();
        Assert.Equal(30, r.ItemCount);
        Assert.Equal(20, r.Samples.Count);
    }

    [Fact]
    public async Task Cancelamento_interrompe_a_varredura()
    {
        using var t = new TempDir();
        var env = Env(t);
        Write(Path.Combine(env.UserTemp, "a.tmp"), 1);
        var cat = CleanCategoryCatalog.Build(env).Single(c => c.Id == "temp.user");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CleanScanner().ScanAsync(new[] { cat }, null, cts.Token));
    }

    [Fact]
    public void Raiz_de_unidade_nao_vira_categoria_utilizavel()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var cats = CleanCategoryCatalog.Build(new CleanEnvironment(Path.Combine(Path.GetTempPath(), "x-local"), root, root));
        Assert.Empty(cats.Single(c => c.Id == "temp.user").Roots);
    }
}
