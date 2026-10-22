using VznBooster.Core.Logging;
using VznBooster.Core.Monitoring;
using Xunit;

namespace VznBooster.Core.Tests;

public class ProcessControlTests
{
    private sealed class FakeController : IProcessController
    {
        public Dictionary<int, ProcessSnapshot> Processes { get; } = new();
        public HashSet<int> Running { get; } = new();
        public bool CloseSucceeds = true;
        public bool ExitsAfterClose = true;
        public bool KillSucceeds = true;
        public List<string> Calls { get; } = new();

        public ProcessSnapshot? Find(int pid) => Processes.TryGetValue(pid, out var p) ? p : null;
        public bool IsRunning(int pid) => Running.Contains(pid);
        public bool RequestClose(int pid)
        {
            Calls.Add($"close:{pid}");
            if (CloseSucceeds && ExitsAfterClose) Running.Remove(pid);
            return CloseSucceeds;
        }
        public bool Kill(int pid)
        {
            Calls.Add($"kill:{pid}");
            if (KillSucceeds) Running.Remove(pid);
            return KillSucceeds;
        }
    }

    private sealed class ListLog : IOperationLog
    {
        public List<OperationLogEntry> Entries { get; } = new();
        public Task WriteAsync(OperationLogEntry e, CancellationToken ct = default) { Entries.Add(e); return Task.CompletedTask; }
        public Task<IReadOnlyList<OperationLogEntry>> ReadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OperationLogEntry>>(Entries);
    }

    private const int Own = 9999;

    private static (ProcessControlService Svc, FakeController Ctl, ListLog Log) Make(params ProcessSnapshot[] procs)
    {
        var ctl = new FakeController();
        foreach (var p in procs) { ctl.Processes[p.Pid] = p; ctl.Running.Add(p.Pid); }
        var log = new ListLog();
        return (new ProcessControlService(ctl, log, Own, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(10)), ctl, log);
    }

    private static ProcessSnapshot App(int pid = 100, string name = "bloco") => new(pid, name, 1000, null, 1, true);

    [Theory]
    [InlineData(4, "System", 0, false)]
    [InlineData(500, "csrss", 0, false)]
    [InlineData(900, "svchost", 0, false)]          // serviço (sessão 0)
    [InlineData(1200, "explorer", 1, true)]         // shell
    [InlineData(1300, "MsMpEng", 1, false)]         // antivírus do Windows
    [InlineData(Own, "VznBooster", 1, true)]        // o próprio aplicativo
    public void Processos_protegidos_nao_podem_ser_encerrados(int pid, string name, int session, bool window)
        => Assert.NotNull(ProcessControlService.ProtectionReason(new ProcessSnapshot(pid, name, 1, null, session, window), Own));

    [Fact]
    public void Aplicativo_comum_e_permitido()
        => Assert.Null(ProcessControlService.ProtectionReason(App(), Own));

    [Fact]
    public async Task Sem_confirmacao_nada_acontece()
    {
        var (svc, ctl, log) = Make(App());
        var r = await svc.CloseAsync(100, userConfirmed: false);
        Assert.Equal(ProcessActionKind.Refused, r.Kind);
        Assert.Empty(ctl.Calls);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Processo_protegido_e_recusado_e_registrado_sem_tocar_nele()
    {
        var (svc, ctl, log) = Make(new ProcessSnapshot(500, "csrss", 1, null, 0, false));
        var r = await svc.CloseAsync(500, true, force: true);
        Assert.Equal(ProcessActionKind.Refused, r.Kind);
        Assert.Empty(ctl.Calls);
        Assert.Equal(OperationResult.Refused, log.Entries.Single().Result);
    }

    [Fact]
    public async Task Processo_inexistente_e_reportado()
    {
        var (svc, ctl, _) = Make();
        Assert.Equal(ProcessActionKind.NotFound, (await svc.CloseAsync(123, true)).Kind);
        Assert.Empty(ctl.Calls);
    }

    [Fact]
    public async Task Fechamento_normal_funciona_sem_forcar()
    {
        var (svc, ctl, log) = Make(App());
        var r = await svc.CloseAsync(100, true);
        Assert.Equal(ProcessActionKind.Success, r.Kind);
        Assert.Equal(new[] { "close:100" }, ctl.Calls.ToArray());
        var e = log.Entries.Single();
        Assert.Equal("process.close:bloco", e.Operation);
        Assert.Equal(OperationResult.Success, e.Result);
    }

    [Fact]
    public async Task Se_o_processo_nao_fecha_nada_e_forcado_automaticamente()
    {
        var (svc, ctl, log) = Make(App());
        ctl.ExitsAfterClose = false;                       // pede para salvar algo e continua aberto
        var r = await svc.CloseAsync(100, true);
        Assert.Equal(ProcessActionKind.StillRunning, r.Kind);
        Assert.DoesNotContain(ctl.Calls, c => c.StartsWith("kill"));
        Assert.Equal(OperationResult.Failed, log.Entries.Single().Result);
    }

    [Fact]
    public async Task Processo_sem_janela_orienta_sobre_o_encerramento_forcado()
    {
        var (svc, ctl, _) = Make(new ProcessSnapshot(100, "segundoplano", 1, null, 1, false));
        ctl.CloseSucceeds = false;
        var r = await svc.CloseAsync(100, true);
        Assert.Equal(ProcessActionKind.StillRunning, r.Kind);
        Assert.Contains("forçado", r.Message);
        Assert.DoesNotContain(ctl.Calls, c => c.StartsWith("kill"));
    }

    [Fact]
    public async Task Encerramento_forcado_so_quando_pedido_e_verifica_o_resultado()
    {
        var (svc, ctl, log) = Make(App());
        var ok = await svc.CloseAsync(100, true, force: true);
        Assert.Equal(ProcessActionKind.Success, ok.Kind);
        Assert.Equal(new[] { "kill:100" }, ctl.Calls.ToArray());
        Assert.Equal("process.kill:bloco", log.Entries.Single().Operation);

        var (svc2, ctl2, log2) = Make(App(101, "teimoso"));
        ctl2.KillSucceeds = false;
        var fail = await svc2.CloseAsync(101, true, force: true);
        Assert.Equal(ProcessActionKind.Failed, fail.Kind);
        Assert.Equal(OperationResult.Failed, log2.Entries.Single().Result);
    }
}
