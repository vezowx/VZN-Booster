using System.ComponentModel;
using System.Diagnostics;
using VznBooster.Core.Logging;

namespace VznBooster.Core.Monitoring;

public enum ProcessActionKind { Success, Refused, NotFound, Failed, StillRunning }

public sealed record ProcessActionResult(ProcessActionKind Kind, string Message);

public interface IProcessController
{
    ProcessSnapshot? Find(int pid);
    bool IsRunning(int pid);
    /// <summary>Pede o fechamento normal (como clicar no X). Retorna false se não houver janela principal ou o pedido falhar.</summary>
    bool RequestClose(int pid);
    /// <summary>Encerramento forçado. Retorna false se o acesso for negado ou o processo já tiver terminado.</summary>
    bool Kill(int pid);
}

public sealed class SystemProcessController : IProcessController
{
    public ProcessSnapshot? Find(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var hasWindow = false;
            try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            return new ProcessSnapshot(p.Id, p.ProcessName, p.WorkingSet64, null, p.SessionId, hasWindow);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    public bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    public bool RequestClose(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.CloseMainWindow();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    public bool Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: false);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return false; }
    }
}

/// <summary>
/// Encerramento CONTROLADO de um processo, sempre por ação explícita do usuário. Primeiro o fechamento normal; o encerramento forçado só existe
/// como segundo passo, também confirmado. Processos do sistema, serviços (sessão 0), de segurança, o shell e o próprio aplicativo são protegidos.
/// Cada tentativa é registrada nos logs. O encerramento não é reversível e dados não salvos podem ser perdidos.
/// </summary>
public sealed class ProcessControlService
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "winlogon", "csrss", "wininit", "services", "lsass", "smss", "fontdrvhost",
        "MsMpEng", "NisSrv", "MpDefenderCoreService", "SecurityHealthService", "SecurityHealthSystray", "SgrmBroker"
    };

    private readonly IProcessController _controller;
    private readonly IOperationLog _log;
    private readonly int _ownPid;
    private readonly TimeSpan _graceTimeout;
    private readonly TimeSpan _poll;

    public ProcessControlService(IProcessController controller, IOperationLog log, int ownPid,
        TimeSpan? graceTimeout = null, TimeSpan? poll = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _ownPid = ownPid;
        _graceTimeout = graceTimeout ?? TimeSpan.FromSeconds(5);
        _poll = poll ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>Motivo pelo qual o processo não pode ser encerrado por este aplicativo; null se for permitido.</summary>
    public static string? ProtectionReason(ProcessSnapshot p, int ownPid)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.Pid == ownPid) return "É o próprio VZN Booster.";
        var kind = ProcessClassifier.Classify(p);
        if (kind == ProcessKind.System) return "É um processo do sistema do Windows.";
        if (kind == ProcessKind.Service) return "É um serviço ou processo da sessão do sistema.";
        var name = p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Name[..^4] : p.Name;
        if (ProtectedNames.Contains(name)) return "É um componente essencial do Windows ou de segurança.";
        return null;
    }

    public async Task<ProcessActionResult> CloseAsync(int pid, bool userConfirmed, bool force = false, CancellationToken ct = default)
    {
        if (!userConfirmed) return new(ProcessActionKind.Refused, "Confirmação explícita ausente; nada foi encerrado.");

        var p = _controller.Find(pid);
        if (p is null) return new(ProcessActionKind.NotFound, "O processo não existe mais.");
        if (ProtectionReason(p, _ownPid) is { } reason)
        {
            var refusal = $"Este processo não pode ser encerrado pelo VZN Booster. {reason}";
            await LogAsync(force, p, OperationResult.Refused, refusal).ConfigureAwait(false);
            return new(ProcessActionKind.Refused, refusal);
        }

        var sw = Stopwatch.StartNew();
        if (force)
        {
            var killed = _controller.Kill(pid);
            var gone = killed && !_controller.IsRunning(pid);
            var msg = gone ? "Processo encerrado à força. Dados não salvos podem ter sido perdidos."
                           : "Não foi possível encerrar o processo à força (acesso negado ou ele continua em execução).";
            await LogAsync(true, p, gone ? OperationResult.Success : OperationResult.Failed, msg).ConfigureAwait(false);
            return new(gone ? ProcessActionKind.Success : ProcessActionKind.Failed, msg);
        }

        if (!_controller.RequestClose(pid))
        {
            var msg = "O processo não tem janela que possa ser fechada normalmente. Se for necessário, use o encerramento forçado (pode perder dados).";
            await LogAsync(false, p, OperationResult.Failed, msg).ConfigureAwait(false);
            return new(ProcessActionKind.StillRunning, msg);
        }

        while (sw.Elapsed < _graceTimeout)
        {
            ct.ThrowIfCancellationRequested();
            if (!_controller.IsRunning(pid))
            {
                const string done = "O processo foi fechado normalmente.";
                await LogAsync(false, p, OperationResult.Success, done).ConfigureAwait(false);
                return new(ProcessActionKind.Success, done);
            }
            await Task.Delay(_poll, ct).ConfigureAwait(false);
        }

        const string still = "O processo recebeu o pedido de fechamento, mas continua em execução (pode estar pedindo para salvar algo). Nada foi forçado.";
        await LogAsync(false, p, OperationResult.Failed, still).ConfigureAwait(false);
        return new(ProcessActionKind.StillRunning, still);
    }

    private Task LogAsync(bool force, ProcessSnapshot p, OperationResult result, string summary) =>
        _log.WriteAsync(new OperationLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "Desempenho",
            $"process.{(force ? "kill" : "close")}:{p.Name}", null, new[] { $"PID {p.Pid}" }, null, summary, null,
            TimeSpan.Zero, result, false, null, false), CancellationToken.None);
}
