using VznBooster.Core.Commands;
using VznBooster.Core.Logging;
using VznBooster.Core.Restore;
using Xunit;

namespace VznBooster.Core.Tests;

public class RestorePointServiceTests
{
    private sealed class ScriptRunner : ICommandRunner
    {
        public List<string> Commands { get; } = new();
        public Queue<string> Counts { get; } = new();
        public int CountExit;
        public CommandResult CreateResult = new(0, "", "", TimeSpan.Zero, false);

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            var cmd = arguments[arguments.Count - 1];
            Commands.Add(cmd);
            if (cmd.Contains("Checkpoint-Computer")) return Task.FromResult(CreateResult);
            var count = Counts.Count > 0 ? Counts.Dequeue() : "0";
            return Task.FromResult(new CommandResult(CountExit, count + "\r\n", CountExit == 0 ? "" : "erro", TimeSpan.Zero, false));
        }
    }

    private sealed class ListLog : IOperationLog
    {
        public List<OperationLogEntry> Entries { get; } = new();
        public Task WriteAsync(OperationLogEntry e, CancellationToken ct = default) { Entries.Add(e); return Task.CompletedTask; }
        public Task<IReadOnlyList<OperationLogEntry>> ReadAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OperationLogEntry>>(Entries);
    }

    private static RestorePointService Service(ScriptRunner runner, ListLog log, bool elevated = true)
        => new(new SystemOperationExecutor(runner, log, () => elevated));

    [Fact]
    public async Task Sem_confirmacao_nada_e_executado()
    {
        var runner = new ScriptRunner();
        var o = await Service(runner, new ListLog()).CreateAsync(false);
        Assert.False(o.Success);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Sem_administrador_recusa_e_orienta()
    {
        var runner = new ScriptRunner();
        var log = new ListLog();
        var o = await Service(runner, log, elevated: false).CreateAsync(true);
        Assert.False(o.Success);
        Assert.Contains("administrador", o.Message);
        Assert.Empty(runner.Commands);
        Assert.All(log.Entries, e => Assert.Equal(OperationResult.Refused, e.Result));
    }

    [Fact]
    public async Task Cria_e_verifica_pela_contagem_de_pontos()
    {
        var runner = new ScriptRunner();
        runner.Counts.Enqueue("2");
        runner.Counts.Enqueue("3");
        var o = await Service(runner, new ListLog()).CreateAsync(true);
        Assert.True(o.Success);
        Assert.Equal(3, runner.Commands.Count);                       // contar, criar, contar
        Assert.Contains("Checkpoint-Computer", runner.Commands[1]);
    }

    [Fact]
    public async Task Comando_ok_mas_sem_novo_ponto_nao_e_sucesso()
    {
        var runner = new ScriptRunner();
        runner.Counts.Enqueue("2");
        runner.Counts.Enqueue("2");
        var o = await Service(runner, new ListLog()).CreateAsync(true);
        Assert.False(o.Success);
        Assert.Contains("24 horas", o.Message);
    }

    [Fact]
    public async Task Falha_do_windows_ao_criar_e_reportada_com_o_motivo()
    {
        var runner = new ScriptRunner { CreateResult = new CommandResult(1, "", "System Restore is disabled", TimeSpan.Zero, false) };
        runner.Counts.Enqueue("0");
        var o = await Service(runner, new ListLog()).CreateAsync(true);
        Assert.False(o.Success);
        Assert.Contains("System Restore is disabled", o.Message);
    }

    [Fact]
    public async Task Se_nao_consegue_contar_nao_tenta_criar()
    {
        var runner = new ScriptRunner { CountExit = 1 };
        var o = await Service(runner, new ListLog()).CreateAsync(true);
        Assert.False(o.Success);
        Assert.DoesNotContain(runner.Commands, c => c.Contains("Checkpoint-Computer"));
    }
}
