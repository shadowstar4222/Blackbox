namespace Blackbox.Domain;

public sealed record DiagnosticLogEntry(
    DateTimeOffset Timestamp,
    DiagnosticCategory Category,
    DiagnosticSeverity Severity,
    string Message,
    string SourceFile);
