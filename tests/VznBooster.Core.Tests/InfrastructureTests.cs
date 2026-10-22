using VznBooster.Core.Backup;
using VznBooster.Core.Commands;
using VznBooster.Core.Logging;
using Xunit;

namespace VznBooster.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "vzn-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
}

public class LogRedactorTests
{
    [Fact]
    public void Oculta_ip_mac_e_segredos()
    {
        var r = LogRedactor.Redact("ip 192.168.0.25 mac aa:bb:cc:dd:ee:ff senha=abc123 token: xyz", out var changed);
        Assert.True(changed);
        Assert.DoesNotContain("0.25", r);
        Assert.DoesNotContain("aa:bb", r);
        Assert.DoesNotContain("abc123", r);
        Assert.DoesNotContain("xyz", r);
    }

    [Fact]
    public void Nao_altera_numero_de_versao()
    {
        var r = LogRedactor.Redact("Windows 10.0.19045.1", out var changed);
        Assert.False(changed);
        Assert.Equal("Windows 10.0.19045.1", r);
    }
}

public class JsonlOperationLogTests
{
    private static OperationLogEntry Sample(string? stdout) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, "Rede", "net.info", "ipconfig.exe", new[] { "/all" },
        0, stdout, null, TimeSpan.FromMilliseconds(12), OperationResult.Success, false, null, false);

    [Fact]
    public async Task Grava_le_e_redige_antes_do_disco()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Dir, "logs", "ops.jsonl");
        var log = new JsonlOperationLog(path);
        await log.WriteAsync(Sample("gateway 192.168.0.1 senha=abc123"));
        await log.WriteAsync(Sample("ok"));

        var all = await log.ReadAllAsync();
        Assert.Equal(2, all.Count);
        Assert.True(all[0].Redacted);
        Assert.DoesNotContain("abc123", all[0].StdOut);
        Assert.Equal(OperationResult.Success, all[0].Result);
        Assert.DoesNotContain("abc123", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Ignora_linha_corrompida()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Dir, "ops.jsonl");
        var log = new JsonlOperationLog(path);
        await log.WriteAsync(Sample("ok"));
        await File.AppendAllTextAsync(path, "{lixo" + Environment.NewLine);
        Assert.Single(await log.ReadAllAsync());
    }
}

public class SystemOperationExecutorTests
{
    private sealed class FakeRunner : ICommandRunner
    {
        public int Calls;
        public CommandResult Result = new(0, "ok", "", TimeSpan.FromMilliseconds(5), false);
        public Task<CommandResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken ct)
        { Calls++; return Task.FromResult(Result); }
    }

    private sealed class MemoryLog : IOperationLog
    {
        public List<OperationLogEntry> Entries { get; } = new();
        public Task WriteAsync(OperationLogEntry e, CancellationToken ct = default) { Entries.Add(e); return Task.CompletedTask; }
        public Task<IReadOnlyList<OperationLogEntry>> ReadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OperationLogEntry>>(Entries);
    }

    [Fact]
    public async Task Somente_leitura_roda_sem_confirmacao()
    {
        var runner = new FakeRunner(); var log = new MemoryLog();
        var e = await new SystemOperationExecutor(runner, log, () => false).ExecuteAsync("net.info", false);
        Assert.Equal(OperationResult.Success, e.Result);
        Assert.Equal(1, runner.Calls);
        Assert.Single(log.Entries);
    }

    [Fact]
    public async Task Alteracao_sem_confirmacao_e_recusada_e_registrada()
    {
        var runner = new FakeRunner(); var log = new MemoryLog();
        var e = await new SystemOperationExecutor(runner, log, () => true).ExecuteAsync("net.dns-flush", false);
        Assert.Equal(OperationResult.Refused, e.Result);
        Assert.Equal(0, runner.Calls);
        Assert.Single(log.Entries);
    }

    [Fact]
    public async Task Sem_elevacao_e_recusada()
    {
        var runner = new FakeRunner(); var log = new MemoryLog();
        var e = await new SystemOperationExecutor(runner, log, () => false).ExecuteAsync("net.dns-flush", true);
        Assert.Equal(OperationResult.Refused, e.Result);
        Assert.Contains("administrador", e.StdErr);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Sucesso_com_reinicio_marca_restart()
    {
        var runner = new FakeRunner(); var log = new MemoryLog();
        var e = await new SystemOperationExecutor(runner, log, () => true).ExecuteAsync("net.winsock-reset", true);
        Assert.Equal(OperationResult.Success, e.Result);
        Assert.True(e.RestartRequired);
        Assert.Equal(0, e.ExitCode);
    }

    [Fact]
    public async Task Codigo_de_saida_diferente_de_zero_e_falha()
    {
        var runner = new FakeRunner { Result = new CommandResult(1, "", "erro", TimeSpan.Zero, false) };
        var e = await new SystemOperationExecutor(runner, new MemoryLog(), () => true).ExecuteAsync("net.dns-flush", true);
        Assert.Equal(OperationResult.Failed, e.Result);
        Assert.False(e.RestartRequired);
    }

    [Fact]
    public async Task Timeout_e_falha()
    {
        var runner = new FakeRunner { Result = new CommandResult(-1, "", "", TimeSpan.FromSeconds(30), true) };
        var e = await new SystemOperationExecutor(runner, new MemoryLog(), () => true).ExecuteAsync("net.dns-flush", true);
        Assert.Equal(OperationResult.Failed, e.Result);
        Assert.Contains("tempo limite", e.StdErr);
    }

    [Fact]
    public async Task Operacao_fora_do_catalogo_lanca()
        => await Assert.ThrowsAsync<ArgumentException>(() =>
            new SystemOperationExecutor(new FakeRunner(), new MemoryLog(), () => true).ExecuteAsync("format.c", true));

    [Fact]
    public void Catalogo_e_seguro()
    {
        Assert.Equal(OperationCatalog.All.Count, OperationCatalog.All.Select(o => o.Id).Distinct().Count());
        foreach (var o in OperationCatalog.All)
        {
            // Nome relativo (resolvido em System32 pelo executor), sem '..' e sem caminho absoluto.
            Assert.False(Path.IsPathRooted(o.FileName));
            Assert.DoesNotContain("..", o.FileName);
            Assert.DoesNotContain('/', o.FileName);
            if (o.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                // Comando fixo e constante; nunca passa por cmd.exe nem recebe texto do usuário.
                Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-Command" }, o.Arguments.Take(3).ToArray());
            }
            else
            {
                Assert.All(o.Arguments, a => Assert.DoesNotContain(a, c => "&|<>^%".Contains(c)));
            }
            Assert.False(string.IsNullOrWhiteSpace(o.Warning));
        }
    }
}

public class BackupStoreTests
{
    [Fact]
    public async Task Cria_completa_e_le()
    {
        using var t = new TempDir();
        var store = new BackupStore(t.Dir);
        var rec = await store.CreateAsync("dns.apply", new Dictionary<string, string> { ["Ethernet"] = "dhcp" }, "restaurar DNS anterior");
        Assert.Equal("Criado", rec.Status);

        await store.CompleteAsync(rec.Id, new Dictionary<string, string> { ["Ethernet"] = "1.1.1.1" }, "Aplicado");
        var back = await store.GetAsync(rec.Id);
        Assert.NotNull(back);
        Assert.Equal("dhcp", back!.Before["Ethernet"]);
        Assert.Equal("1.1.1.1", back.After!["Ethernet"]);
        Assert.Equal("Aplicado", back.Status);
        Assert.Equal(BackupStore.CurrentFormatVersion, back.FormatVersion);
    }

    [Fact]
    public async Task Id_invalido_nao_acessa_fora_da_pasta()
    {
        using var t = new TempDir();
        var store = new BackupStore(t.Dir);
        Assert.Null(await store.GetAsync("..\\..\\x"));
        Assert.Null(await store.GetAsync("../x"));
        Assert.Null(await store.GetAsync(new string('a', 32)));
    }

    [Fact]
    public async Task Lista_do_mais_novo_para_o_mais_antigo()
    {
        using var t = new TempDir();
        var store = new BackupStore(t.Dir);
        var a = await store.CreateAsync("op1", new Dictionary<string, string>(), "m");
        await Task.Delay(20);
        var b = await store.CreateAsync("op2", new Dictionary<string, string>(), "m");
        var list = await store.ListAsync();
        Assert.Equal(new[] { b.Id, a.Id }, list.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Completar_backup_inexistente_lanca()
    {
        using var t = new TempDir();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            new BackupStore(t.Dir).CompleteAsync(new string('b', 32), new Dictionary<string, string>(), "x"));
    }
}
