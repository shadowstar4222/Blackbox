using Blackbox.Domain;
using Blackbox.Infrastructure;

namespace Blackbox.Recording;

public interface IRecordingGuardianRepairer
{
    Task RepairAsync(
        RecordingGuardianRepairAction action,
        GameCaptureTarget? target,
        CancellationToken cancellationToken = default);
}

public sealed class RecordingGuardianRepairer(IObsController obsController) : IRecordingGuardianRepairer
{
    public Task RepairAsync(
        RecordingGuardianRepairAction action,
        GameCaptureTarget? target,
        CancellationToken cancellationToken = default) =>
        action switch
        {
            RecordingGuardianRepairAction.RefreshCapture when target is not null =>
                obsController.RefreshGameCaptureAsync(target, cancellationToken),
            RecordingGuardianRepairAction.RestartRecordingOutput =>
                obsController.StartRecordingAsync(cancellationToken),
            RecordingGuardianRepairAction.None => Task.CompletedTask,
            _ => throw new InvalidOperationException("The requested guardian repair needs an active capture target.")
        };
}
