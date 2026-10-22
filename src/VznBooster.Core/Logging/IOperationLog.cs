namespace VznBooster.Core.Logging;

public interface IOperationLog
{
    Task WriteAsync(OperationLogEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<OperationLogEntry>> ReadAllAsync(CancellationToken ct = default);
}
