namespace VznBooster.Core.Commands;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration, bool TimedOut);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}
