namespace VznBooster.Core.Logging;

public enum OperationResult { Success, Failed, Cancelled, Refused }

public sealed record OperationLogEntry(
    Guid Id, DateTimeOffset TimestampUtc, string Category, string Operation,
    string? Command, IReadOnlyList<string> Arguments, int? ExitCode,
    string? StdOut, string? StdErr, TimeSpan Duration, OperationResult Result,
    bool RestartRequired, string? BackupId, bool Redacted);
