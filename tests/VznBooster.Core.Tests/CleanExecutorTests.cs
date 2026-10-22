using VznBooster.Core.Cleaning;
using VznBooster.Core.Logging;
using Xunit;

namespace VznBooster.Core.Tests;

public class CleanExecutorTests
{
    private sealed class CleanLog : IOperationLog
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
        Path.Combine(t.Dir, "local"), Path.Combine(t.Dir, "usertemp"), Path.Combine(t.Dir, "windows"));

    private static CleanExecutor Executor(CleanEnvironment env, CleanLog log, bool browserRunning = false)
        => new(env, log, _ => browserRunning);

    [Fact]
    public async Task Sem_confirmacao_nada_e_apagado()
    {
        using var t = new TempDir(); var env = Env(t); var log = new CleanLog();
        Write(Path.Combine(env.UserTemp, "a.tmp"), 10);
        var r = await Executor(env, log).CleanAsync(new[] { "temp.user" }, false);
        Assert.False(r.Executed);
        Assert.True(File.Exists(Path.Combine(env.UserTemp, "a.tmp")));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Categoria_desconhecida_ou_lista_vazia_lanca_sem_apagar()
    {
        using var t = new TempDir(); var env = Env(t);
        Write(Path.Combine(env.UserTemp, "a.tmp"), 10);
        var ex = Executor(env, new CleanLog());
        await Assert.ThrowsAsync<ArgumentException>(() => ex.CleanAsync(new[] { "temp.user", "c:\\windows" }, true));
        await Assert.ThrowsAsync<ArgumentException>(() => ex.CleanAsync(Array.Empty<string>(), true));
        Assert.True(File.Exists(Path.Combine(env.UserTemp, "a.tmp")));
    }

    [Fact]
    public async Task Apaga_somente_a_categoria_escolhida_e_mantem_a_raiz()
    {
        using var t = new TempDir(); var env = Env(t); var log = new CleanLog();
        Write(Path.Combine(env.UserTemp, "a.tmp"), 10);
        Write(Path.Combine(env.UserTemp, "sub", "b.tmp"), 20);
        Write(Path.Combine(env.UserTemp, "sub", "deep", "c.tmp"), 30);
        Write(Path.Combine(t.Dir, "usertemp-vizinha", "keep.txt"), 5);          // fora da raiz (prefixo parecido)
        Write(Path.Combine(env.WindowsDir, "Temp", "w.tmp"), 7);                // outra categoria, não selecionada
        Write(Path.Combine(env.LocalAppData, "pessoal.txt"), 9);

        var r = await Executor(env, log).CleanAsync(new[] { "temp.user" }, true);

        var res = r.Results.Single();
        Assert.True(r.Executed);
        Assert.Equal(CleanStatus.Cleaned, res.Status);
        Assert.Equal(3, res.FilesDeleted);
        Assert.Equal(60, res.BytesDeleted);
        Assert.Equal(2, res.DirsRemoved);                                       // sub e sub\deep
        Assert.True(Directory.Exists(env.UserTemp));
        Assert.Empty(Directory.GetFileSystemEntries(env.UserTemp));
        Assert.True(File.Exists(Path.Combine(t.Dir, "usertemp-vizinha", "keep.txt")));
        Assert.True(File.Exists(Path.Combine(env.WindowsDir, "Temp", "w.tmp")));
        Assert.True(File.Exists(Path.Combine(env.LocalAppData, "pessoal.txt")));

        var entry = log.Entries.Single();
        Assert.Equal("cleaner.temp.user", entry.Operation);
        Assert.Equal(OperationResult.Success, entry.Result);
    }

    [Fact]
    public async Task Navegador_aberto_pula_a_categoria_sem_apagar()
    {
        using var t = new TempDir(); var env = Env(t); var log = new CleanLog();
        var cache = Path.Combine(env.LocalAppData, "Google", "Chrome", "User Data", "Default", "Cache", "a.bin");
        Write(cache, 10);
        var r = await Executor(env, log, browserRunning: true).CleanAsync(new[] { "browser.chrome.Default" }, true);
        Assert.Equal(CleanStatus.AppRunning, r.Results.Single().Status);
        Assert.True(File.Exists(cache));
        Assert.Equal(OperationResult.Refused, log.Entries.Single().Result);
    }

    [Fact]
    public async Task Navegador_fechado_limpa_so_o_cache_e_preserva_dados_pessoais()
    {
        using var t = new TempDir(); var env = Env(t); var log = new CleanLog();
        var profile = Path.Combine(env.LocalAppData, "Google", "Chrome", "User Data", "Default");
        Write(Path.Combine(profile, "Cache", "a.bin"), 10);
        Write(Path.Combine(profile, "Code Cache", "js", "b.bin"), 10);
        Write(Path.Combine(profile, "Cookies"), 3);
        Write(Path.Combine(profile, "Login Data"), 3);
        Write(Path.Combine(profile, "History"), 3);

        var r = await Executor(env, log).CleanAsync(new[] { "browser.chrome.Default" }, true);

        Assert.Equal(2, r.Results.Single().FilesDeleted);
        Assert.False(File.Exists(Path.Combine(profile, "Cache", "a.bin")));
        Assert.True(File.Exists(Path.Combine(profile, "Cookies")));
        Assert.True(File.Exists(Path.Combine(profile, "Login Data")));
        Assert.True(File.Exists(Path.Combine(profile, "History")));
    }

    [Fact]
    public async Task Pasta_inexistente_e_reportada()
    {
        using var t = new TempDir(); var env = Env(t);
        var r = await Executor(env, new CleanLog()).CleanAsync(new[] { "cache.shaders" }, true);
        Assert.Equal(CleanStatus.NotFound, r.Results.Single().Status);
    }

    [Fact]
    public async Task Cancelamento_previo_nao_apaga_nada()
    {
        using var t = new TempDir(); var env = Env(t);
        Write(Path.Combine(env.UserTemp, "a.tmp"), 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await Executor(env, new CleanLog()).CleanAsync(new[] { "temp.user" }, true, null, cts.Token);
        Assert.True(r.Cancelled);
        Assert.Empty(r.Results);
        Assert.True(File.Exists(Path.Combine(env.UserTemp, "a.tmp")));
    }

    [Fact]
    public async Task Arquivo_em_uso_e_ignorado_nao_e_falha()
    {
        if (!OperatingSystem.IsWindows()) return;   // o bloqueio de exclusão por abertura exclusiva é comportamento do Windows
        using var t = new TempDir(); var env = Env(t);
        var locked = Path.Combine(env.UserTemp, "locked.tmp");
        Write(locked, 10);
        Write(Path.Combine(env.UserTemp, "free.tmp"), 20);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = await Executor(env, new CleanLog()).CleanAsync(new[] { "temp.user" }, true);
            var res = r.Results.Single();
            Assert.Equal(CleanStatus.Cleaned, res.Status);
            Assert.Equal(1, res.FilesDeleted);
            Assert.Equal(1, res.FilesSkipped);
            Assert.Equal(20, res.BytesDeleted);
        }
        Assert.True(File.Exists(locked));
    }
}
