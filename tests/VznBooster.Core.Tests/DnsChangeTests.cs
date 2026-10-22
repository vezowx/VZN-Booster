using System.Text.RegularExpressions;
using VznBooster.Core.Backup;
using VznBooster.Core.Commands;
using VznBooster.Core.Dns;
using VznBooster.Core.Logging;
using VznBooster.Core.Networking;
using Xunit;

namespace VznBooster.Core.Tests;

public class DnsOperationsTests
{
    [Fact]
    public void Monta_comando_somente_com_indice_e_ips_canonicos()
    {
        var op = DnsOperations.Set(7, new[] { "1.1.1.1", "1.0.0.1" }).Operation;
        Assert.Equal("-Command", op.Arguments[2]);
        Assert.Contains("-InterfaceIndex 7", op.Arguments[3]);
        Assert.Contains("('1.1.1.1','1.0.0.1')", op.Arguments[3]);
        Assert.True(op.RequiresAdmin);
    }

    [Theory]
    [InlineData("1.1.1.1; calc")]
    [InlineData("1.1.1.1' ; calc ; '")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("não-é-ip")]
    [InlineData("")]
    public void Rejeita_enderecos_invalidos(string bad)
        => Assert.Throws<ArgumentException>(() => DnsOperations.Set(7, new[] { bad }));

    [Fact]
    public void Rejeita_indice_e_quantidade_invalidos()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DnsOperations.Set(0, new[] { "1.1.1.1" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DnsOperations.Reset(-1));
        Assert.Throws<ArgumentException>(() => DnsOperations.Set(7, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => DnsOperations.Set(7, new[] { "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9" }));
    }

    [Fact]
    public void Reset_usa_apenas_o_indice()
        => Assert.Contains("-InterfaceIndex 7 -ResetServerAddresses", DnsOperations.Reset(7).Operation.Arguments[3]);
}

public class DnsChangeServiceTests
{
    private const string AdapterId = "{11111111-2222-3333-4444-555555555555}";

    private sealed class FakeAdapters : IAdapterSource
    {
        public NetworkAdapterInfo Adapter;
        public FakeAdapters(NetworkAdapterInfo a) => Adapter = a;
        public IReadOnlyList<NetworkAdapterInfo> ReadAll() => new[] { Adapter };
    }

    /// <summary>Simula o efeito do cmdlet sobre o estado do adaptador, para testar o fluxo completo.</summary>
    private sealed class StateRunner : ICommandRunner
    {
        private readonly FakeAdapters _state;
        public List<string> Commands { get; } = new();
        public int ExitCode;
        public bool NoEffect;
        public StateRunner(FakeAdapters state) => _state = state;

        public Task<CommandResult> RunAsync(string f, IReadOnlyList<string> a, TimeSpan t, CancellationToken ct)
        {
            var cmd = a[a.Count - 1];
            Commands.Add(cmd);
            if (ExitCode == 0 && !NoEffect)
            {
                if (cmd.Contains("-ResetServerAddresses"))
                    _state.Adapter = _state.Adapter with
                    { DnsServers = new[] { "192.168.0.1" }, DnsSource = DnsConfigSource.Automatic, StaticDnsServers = Array.Empty<string>() };
                else
                {
                    var servers = Regex.Matches(cmd, "'([^']+)'").Select(m => m.Groups[1].Value).ToArray();
                    _state.Adapter = _state.Adapter with
                    { DnsServers = servers, DnsSource = DnsConfigSource.Static, StaticDnsServers = servers };
                }
            }
            return Task.FromResult(new CommandResult(ExitCode, "", ExitCode == 0 ? "" : "falha simulada", TimeSpan.FromMilliseconds(3), false));
        }
    }

    private sealed class ListLog : IOperationLog
    {
        public List<OperationLogEntry> Entries { get; } = new();
        public Task WriteAsync(OperationLogEntry e, CancellationToken ct = default) { Entries.Add(e); return Task.CompletedTask; }
        public Task<IReadOnlyList<OperationLogEntry>> ReadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OperationLogEntry>>(Entries);
    }

    private sealed class Rig : IDisposable
    {
        public DnsChangeService Svc { get; }
        public FakeAdapters State { get; }
        public StateRunner Runner { get; }
        public BackupStore Backups { get; }
        public ListLog Log { get; }
        private readonly TempDir _dir = new();

        public Rig(NetworkAdapterInfo adapter, bool elevated)
        {
            State = new FakeAdapters(adapter);
            Runner = new StateRunner(State);
            Log = new ListLog();
            Backups = new BackupStore(_dir.Dir);
            Svc = new DnsChangeService(new SystemOperationExecutor(Runner, Log, () => elevated), Backups, State);
        }

        public void Dispose() => _dir.Dispose();
    }

    private static NetworkAdapterInfo Adapter(DnsConfigSource src, params string[] statics) => new(
        AdapterId, "Ethernet", "Adaptador de teste", "Ethernet", "Up", 1000,
        statics.Length > 0 ? statics : new[] { "192.168.0.1" }, new[] { "192.168.0.1" },
        7, src, statics);

    [Fact]
    public void Plano_mostra_atual_novo_e_avisos()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Static, "192.168.0.1"), true);
        var plan = r.Svc.Plan(AdapterId, "quad9");
        Assert.Equal("Manual: 192.168.0.1", plan.CurrentDescription);
        Assert.Equal(new[] { "9.9.9.9", "149.112.112.112" }, plan.NewServers.ToArray());
        Assert.Contains(plan.Warnings, w => w.Contains("filtra"));
        Assert.Empty(r.Runner.Commands);

        var cf = r.Svc.Plan(AdapterId, "cloudflare");
        Assert.DoesNotContain(cf.Warnings, w => w.Contains("filtra"));
    }

    [Fact]
    public void Plano_rejeita_adaptador_perfil_e_estado_invalidos()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Automatic), true);
        Assert.Throws<ArgumentException>(() => r.Svc.Plan("{00000000-0000-0000-0000-000000000000}", "google"));
        Assert.Throws<ArgumentException>(() => r.Svc.Plan(AdapterId, "inexistente"));
        r.State.Adapter = r.State.Adapter with { Status = "Down" };
        Assert.Throws<InvalidOperationException>(() => r.Svc.Plan(AdapterId, "google"));
    }

    [Fact]
    public async Task Sem_confirmacao_nada_acontece_e_nenhum_backup_e_criado()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Automatic), true);
        var o = await r.Svc.ApplyAsync(r.Svc.Plan(AdapterId, "cloudflare"), false);
        Assert.False(o.Success);
        Assert.Empty(r.Runner.Commands);
        Assert.Empty(await r.Backups.ListAsync());
    }

    [Fact]
    public async Task Modo_indeterminado_e_recusado_sem_backup()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Unknown), true);
        var plan = r.Svc.Plan(AdapterId, "cloudflare");
        Assert.Contains(plan.Warnings, w => w.Contains("recusada"));
        var o = await r.Svc.ApplyAsync(plan, true);
        Assert.False(o.Success);
        Assert.Empty(r.Runner.Commands);
        Assert.Empty(await r.Backups.ListAsync());
    }

    [Fact]
    public async Task Sem_elevacao_a_alteracao_e_recusada_e_o_backup_marca_falha()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Automatic), elevated: false);
        var o = await r.Svc.ApplyAsync(r.Svc.Plan(AdapterId, "cloudflare"), true);
        Assert.False(o.Success);
        Assert.Contains("administrador", o.Message);
        Assert.Empty(r.Runner.Commands);
        var b = (await r.Backups.ListAsync()).Single();
        Assert.StartsWith("Falhou", b.Status);
    }

    [Fact]
    public async Task Aplica_verifica_registra_e_guarda_backup()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Automatic), true);
        var o = await r.Svc.ApplyAsync(r.Svc.Plan(AdapterId, "cloudflare"), true);
        Assert.True(o.Success);
        Assert.True(o.Verified);
        Assert.Contains("-InterfaceIndex 7", r.Runner.Commands.Single());
        Assert.Equal("dns.set", r.Log.Entries.Single().Operation);

        var b = (await r.Backups.ListAsync()).Single();
        Assert.Equal(o.BackupId, b.Id);
        Assert.Equal("Automatic", b.Before["source"]);
        Assert.Equal("1.1.1.1,1.0.0.1", b.After!["servers"]);
        Assert.Equal("Aplicado e verificado", b.Status);
    }

    [Fact]
    public async Task Falha_do_comando_nao_e_declarada_como_sucesso()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Automatic), true);
        r.Runner.ExitCode = 1;
        var o = await r.Svc.ApplyAsync(r.Svc.Plan(AdapterId, "cloudflare"), true);
        Assert.False(o.Success);
        Assert.Equal(DnsConfigSource.Automatic, r.State.Adapter.DnsSource);
        Assert.Equal("Falhou; estado final não confirmado", (await r.Backups.ListAsync()).Single().Status);
    }

    [Fact]
    public async Task Comando_ok_sem_efeito_observado_nao_e_marcado_como_verificado()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Automatic), true);
        r.Runner.NoEffect = true;
        var o = await r.Svc.ApplyAsync(r.Svc.Plan(AdapterId, "cloudflare"), true);
        Assert.True(o.Success);
        Assert.False(o.Verified);
        Assert.Equal("Aplicado, verificação falhou", (await r.Backups.ListAsync()).Single().Status);
    }

    [Fact]
    public async Task Restaura_dns_automatico()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Automatic), true);
        var applied = await r.Svc.ApplyAsync(r.Svc.Plan(AdapterId, "cloudflare"), true);
        var o = await r.Svc.RestoreAsync(applied.BackupId!, true);
        Assert.True(o.Success);
        Assert.True(o.Verified);
        Assert.Equal(DnsConfigSource.Automatic, r.State.Adapter.DnsSource);
        Assert.Contains("-ResetServerAddresses", r.Runner.Commands[1]);
        Assert.Equal("Restaurado e verificado", (await r.Backups.GetAsync(applied.BackupId!))!.Status);
    }

    [Fact]
    public async Task Restaura_dns_manual_anterior()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Static, "192.168.0.1", "192.168.0.2"), true);
        var applied = await r.Svc.ApplyAsync(r.Svc.Plan(AdapterId, "cloudflare"), true);
        Assert.Equal(new[] { "1.1.1.1", "1.0.0.1" }, r.State.Adapter.DnsServers.ToArray());

        var o = await r.Svc.RestoreAsync(applied.BackupId!, true);
        Assert.True(o.Success);
        Assert.True(o.Verified);
        Assert.Equal(new[] { "192.168.0.1", "192.168.0.2" }, r.State.Adapter.DnsServers.ToArray());
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("00000000000000000000000000000000")]
    public async Task Restauracao_de_backup_inexistente_ou_malformado_falha(string id)
    {
        using var r = new Rig(Adapter(DnsConfigSource.Automatic), true);
        var o = await r.Svc.RestoreAsync(id, true);
        Assert.False(o.Success);
        Assert.Empty(r.Runner.Commands);
    }

    [Fact]
    public async Task Restauracao_exige_confirmacao()
    {
        using var r = new Rig(Adapter(DnsConfigSource.Automatic), true);
        var applied = await r.Svc.ApplyAsync(r.Svc.Plan(AdapterId, "cloudflare"), true);
        var before = r.Runner.Commands.Count;
        var o = await r.Svc.RestoreAsync(applied.BackupId!, false);
        Assert.False(o.Success);
        Assert.Equal(before, r.Runner.Commands.Count);
    }
}
