using VznBooster.Core.Cleaning;
using VznBooster.Core.Logging;
using Xunit;

namespace VznBooster.Core.Tests;

public class CleanerExpansionTests
{
    private sealed class FakeBin : IRecycleBin
    {
        public RecycleBinInfo? Info;
        public int EmptyResult;
        public int EmptyCalls;
        public RecycleBinInfo? Query() => Info;
        public int Empty()
        {
            EmptyCalls++;
            if (EmptyResult == 0) Info = new RecycleBinInfo(0, 0);
            return EmptyResult;
        }
    }

    private sealed class ListLog : IOperationLog
    {
        public List<OperationLogEntry> Entries { get; } = new();
        public Task WriteAsync(OperationLogEntry e, CancellationToken ct = default) { Entries.Add(e); return Task.CompletedTask; }
        public Task<IReadOnlyList<OperationLogEntry>> ReadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OperationLogEntry>>(Entries);
    }

    private static void Write(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    private static CleanEnvironment Env(TempDir t) => new(
        Path.Combine(t.Dir, "local"), Path.Combine(t.Dir, "usertemp"), Path.Combine(t.Dir, "windows"), Path.Combine(t.Dir, "programdata"));

    private static CleanExecutor Executor(CleanEnvironment env, ListLog log, FakeBin? bin = null, Func<string, bool>? running = null)
        => new(env, log, running ?? (_ => false), bin ?? new FakeBin());

    // ---------- catálogo ----------

    [Fact]
    public void Toda_categoria_pertence_a_um_grupo_conhecido_e_ids_sao_unicos()
    {
        using var t = new TempDir();
        var cats = CleanCategoryCatalog.Build(Env(t));
        Assert.All(cats, c => Assert.Contains(c.Group, CleanGroups.Order));
        Assert.Equal(cats.Count, cats.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Categorias_moderadas_e_avancadas_tem_aviso_e_nunca_vem_marcadas()
    {
        using var t = new TempDir();
        var cats = CleanCategoryCatalog.Build(Env(t));
        var risky = cats.Where(c => c.Risk != CleanRisk.Low).ToList();
        Assert.NotEmpty(risky);
        Assert.All(risky, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Warning));
            Assert.False(c.SelectedByDefault);
        });
    }

    [Fact]
    public void Prefetch_e_avancado_so_pf_e_com_aviso_sem_promessa_de_desempenho()
    {
        using var t = new TempDir();
        var p = CleanCategoryCatalog.Build(Env(t)).Single(c => c.Id == "adv.prefetch");
        Assert.Equal(CleanRisk.Advanced, p.Risk);
        Assert.Equal(CleanGroups.Advanced, p.Group);
        Assert.Equal("*.pf", p.FilePattern);
        Assert.False(p.Recursive);
        Assert.False(p.SelectedByDefault);
        Assert.Contains("NÃO é recomendada", p.Warning);
        Assert.Contains("Não há promessa", p.Warning);
    }

    [Fact]
    public void Cada_perfil_de_navegador_vira_categoria_propria()
    {
        using var t = new TempDir();
        var env = Env(t);
        var edge = Path.Combine(env.LocalAppData, "Microsoft", "Edge", "User Data");
        Write(Path.Combine(edge, "Default", "Cache", "a.bin"), 1);
        Write(Path.Combine(edge, "Profile 2", "GPUCache", "b.bin"), 1);
        var ids = CleanCategoryCatalog.Build(env).Select(c => c.Id).ToList();
        Assert.Contains("browser.edge.Default", ids);
        Assert.Contains("browser.edge.Profile 2", ids);
    }

    [Fact]
    public void Relatorios_de_erro_usam_pastas_do_usuario_e_do_sistema()
    {
        using var t = new TempDir();
        var env = Env(t);
        var cats = CleanCategoryCatalog.Build(env);
        Assert.Equal(3, cats.Single(c => c.Id == "diag.wer.user").Roots.Count);
        Assert.Equal(2, cats.Single(c => c.Id == "diag.wer.system").Roots.Count);
        Assert.True(cats.Single(c => c.Id == "diag.wer.system").NeedsAdmin);

        var semProgramData = CleanCategoryCatalog.Build(new CleanEnvironment(env.LocalAppData, env.UserTemp, env.WindowsDir));
        Assert.Empty(semProgramData.Single(c => c.Id == "diag.wer.system").Roots);
    }

    // ---------- Prefetch ----------

    [Fact]
    public async Task Varredura_do_prefetch_conta_somente_pf()
    {
        using var t = new TempDir();
        var env = Env(t);
        var dir = Path.Combine(env.WindowsDir, "Prefetch");
        Write(Path.Combine(dir, "A.EXE-111.pf"), 10);
        Write(Path.Combine(dir, "B.EXE-222.pf"), 20);
        Write(Path.Combine(dir, "Layout.ini"), 5);
        Write(Path.Combine(dir, "sub", "C.pf"), 7);                 // não recursivo

        var cat = CleanCategoryCatalog.Build(env).Single(c => c.Id == "adv.prefetch");
        var r = (await new CleanScanner(new FakeBin()).ScanAsync(new[] { cat })).Single();
        Assert.Equal(2, r.ItemCount);
        Assert.Equal(30, r.TotalBytes);
    }

    [Fact]
    public async Task Prefetch_sem_reconhecimento_avancado_e_recusado_e_nada_e_apagado()
    {
        using var t = new TempDir();
        var env = Env(t);
        var file = Path.Combine(env.WindowsDir, "Prefetch", "A.EXE-111.pf");
        Write(file, 10);
        var log = new ListLog();

        var r = await Executor(env, log).CleanAsync(new[] { "adv.prefetch" }, true);

        Assert.Equal(CleanStatus.Refused, r.Results.Single().Status);
        Assert.True(File.Exists(file));
        Assert.Equal(OperationResult.Refused, log.Entries.Single().Result);
    }

    [Fact]
    public async Task Prefetch_reconhecido_apaga_so_pf_e_registra_os_nomes()
    {
        using var t = new TempDir();
        var env = Env(t);
        var dir = Path.Combine(env.WindowsDir, "Prefetch");
        Write(Path.Combine(dir, "A.EXE-111.pf"), 10);
        Write(Path.Combine(dir, "Layout.ini"), 5);
        Write(Path.Combine(env.WindowsDir, "Temp", "outro.tmp"), 3);
        var log = new ListLog();

        var r = await Executor(env, log).CleanAsync(new[] { "adv.prefetch" }, true, null, default, new[] { "adv.prefetch" });

        var res = r.Results.Single();
        Assert.Equal(CleanStatus.Cleaned, res.Status);
        Assert.Equal(1, res.FilesDeleted);
        Assert.False(File.Exists(Path.Combine(dir, "A.EXE-111.pf")));
        Assert.True(File.Exists(Path.Combine(dir, "Layout.ini")));
        Assert.True(File.Exists(Path.Combine(env.WindowsDir, "Temp", "outro.tmp")));
        Assert.Contains("A.EXE-111.pf", log.Entries.Single().StdOut);
    }

    // ---------- Lixeira ----------

    [Fact]
    public async Task Varredura_da_lixeira_usa_a_consulta_da_api()
    {
        using var t = new TempDir();
        var cat = CleanCategoryCatalog.Build(Env(t)).Single(c => c.Id == "sys.recyclebin");
        var r = (await new CleanScanner(new FakeBin { Info = new RecycleBinInfo(12, 3456) }).ScanAsync(new[] { cat })).Single();
        Assert.True(r.RootsFound);
        Assert.Equal(12, r.ItemCount);
        Assert.Equal(3456, r.TotalBytes);

        var indisponivel = (await new CleanScanner(new FakeBin { Info = null }).ScanAsync(new[] { cat })).Single();
        Assert.False(indisponivel.RootsFound);
    }

    [Fact]
    public async Task Esvaziar_a_lixeira_mede_o_resultado()
    {
        using var t = new TempDir();
        var bin = new FakeBin { Info = new RecycleBinInfo(5, 1000) };
        var log = new ListLog();
        var r = await Executor(Env(t), log, bin).CleanAsync(new[] { "sys.recyclebin" }, true);
        var res = r.Results.Single();
        Assert.Equal(CleanStatus.Cleaned, res.Status);
        Assert.Equal(5, res.FilesDeleted);
        Assert.Equal(1000, res.BytesDeleted);
        Assert.Equal(1, bin.EmptyCalls);
        Assert.Equal(OperationResult.Success, log.Entries.Single().Result);
    }

    [Fact]
    public async Task Lixeira_ja_vazia_nao_chama_a_api_de_exclusao()
    {
        using var t = new TempDir();
        var bin = new FakeBin { Info = new RecycleBinInfo(0, 0) };
        var r = await Executor(Env(t), new ListLog(), bin).CleanAsync(new[] { "sys.recyclebin" }, true);
        Assert.Equal(CleanStatus.Cleaned, r.Results.Single().Status);
        Assert.Equal(0, bin.EmptyCalls);
    }

    [Fact]
    public async Task Falha_da_api_com_itens_restantes_nao_e_declarada_como_sucesso()
    {
        using var t = new TempDir();
        var bin = new FakeBin { Info = new RecycleBinInfo(5, 1000), EmptyResult = unchecked((int)0x80004005) };
        var log = new ListLog();
        var r = await Executor(Env(t), log, bin).CleanAsync(new[] { "sys.recyclebin" }, true);
        Assert.Equal(CleanStatus.Failed, r.Results.Single().Status);
        Assert.Equal(OperationResult.Failed, log.Entries.Single().Result);
    }

    [Fact]
    public async Task Consulta_indisponivel_da_lixeira_falha_sem_chamar_a_exclusao()
    {
        using var t = new TempDir();
        var bin = new FakeBin { Info = null };
        var r = await Executor(Env(t), new ListLog(), bin).CleanAsync(new[] { "sys.recyclebin" }, true);
        Assert.Equal(CleanStatus.Failed, r.Results.Single().Status);
        Assert.Equal(0, bin.EmptyCalls);
    }

    // ---------- navegadores: processo pelo ID, inclusive Firefox com ponto no nome do perfil ----------

    [Fact]
    public async Task Firefox_aberto_pula_o_perfil_mesmo_com_ponto_no_nome()
    {
        using var t = new TempDir();
        var env = Env(t);
        var cache = Path.Combine(env.LocalAppData, "Mozilla", "Firefox", "Profiles", "abc.default-release", "cache2", "e.bin");
        Write(cache, 4);
        var chromeCache = Path.Combine(env.LocalAppData, "Google", "Chrome", "User Data", "Default", "Cache", "a.bin");
        Write(chromeCache, 4);

        var r = await Executor(env, new ListLog(), running: name => name == "firefox")
            .CleanAsync(new[] { "browser.firefox.abc.default-release", "browser.chrome.Default" }, true);

        Assert.Equal(CleanStatus.AppRunning, r.Results[0].Status);
        Assert.True(File.Exists(cache));
        Assert.Equal(CleanStatus.Cleaned, r.Results[1].Status);   // Chrome fechado: limpa normalmente
        Assert.False(File.Exists(chromeCache));
    }

    [Fact]
    public async Task Selecao_de_um_perfil_nao_toca_nos_outros()
    {
        using var t = new TempDir();
        var env = Env(t);
        var data = Path.Combine(env.LocalAppData, "Google", "Chrome", "User Data");
        var a = Path.Combine(data, "Default", "Cache", "a.bin");
        var b = Path.Combine(data, "Profile 1", "Cache", "b.bin");
        Write(a, 1); Write(b, 1);

        await Executor(env, new ListLog()).CleanAsync(new[] { "browser.chrome.Profile 1" }, true);

        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
    }
}
