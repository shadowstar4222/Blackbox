namespace Blackbox.Domain;

public enum ExportStage
{
    Preparing,
    Exporting,
    Finalizing,
    Complete
}

public sealed record ExportProgress(ExportStage Stage, string Message, double? Percent = null);
