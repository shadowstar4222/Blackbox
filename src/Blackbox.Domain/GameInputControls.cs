namespace Blackbox.Domain;

public enum GameInputAction
{
    AddMarker,
    ProtectRecentFootage,
    SaveInstantClip,
    SwitchCapture,
    ToggleMicrophoneMute,
    ToggleRecording
}

public enum GameInputDevice
{
    Keyboard,
    Controller
}

public enum GameInputTrigger
{
    Press,
    Hold
}

public sealed record GameInputBinding
{
    public required GameInputAction Action { get; init; }
    public required GameInputDevice Device { get; init; }
    public required string Input { get; init; }
    public GameInputTrigger Trigger { get; init; }
    public int HoldMilliseconds { get; init; } = 500;
    public bool IsEnabled { get; init; } = true;
    public bool DestructiveActionEnabled { get; init; }

    public void Validate()
    {
        if (!Enum.IsDefined(Action) ||
            !Enum.IsDefined(Device) ||
            !Enum.IsDefined(Trigger) ||
            string.IsNullOrWhiteSpace(Input))
        {
            throw new InvalidOperationException("A game input binding is invalid.");
        }

        if (Trigger == GameInputTrigger.Hold && HoldMilliseconds is < 250 or > 5000)
        {
            throw new InvalidOperationException("Input hold duration must be between 250 ms and 5 seconds.");
        }

        if (IsEnabled &&
            Action == GameInputAction.ToggleRecording &&
            !DestructiveActionEnabled)
        {
            throw new InvalidOperationException(
                "Recording-control shortcuts require explicit destructive-action opt-in.");
        }
    }
}

public sealed record GameInputControls
{
    public IReadOnlyList<GameInputBinding> Bindings { get; init; } = [];

    public IReadOnlyList<GameInputConflict> GetConflicts() =>
        Bindings
            .Where(static binding => binding.IsEnabled)
            .GroupBy(
                static binding => new
                {
                    binding.Device,
                    Input = binding.Input.Trim().ToUpperInvariant(),
                    binding.Trigger
                })
            .Where(static group => group.Count() > 1)
            .Select(group => new GameInputConflict(
                group.Key.Device,
                group.Key.Input,
                group.Key.Trigger,
                group.Select(static binding => binding.Action).Distinct().ToArray()))
            .ToArray();

    public void Validate()
    {
        if (Bindings is null)
        {
            throw new InvalidOperationException("Game input bindings are required.");
        }

        foreach (var binding in Bindings)
        {
            binding.Validate();
        }

        var conflict = GetConflicts().FirstOrDefault();
        if (conflict is not null)
        {
            throw new InvalidOperationException(
                $"{conflict.Device} input '{conflict.Input}' is assigned to multiple actions.");
        }
    }
}

public sealed record GameInputConflict(
    GameInputDevice Device,
    string Input,
    GameInputTrigger Trigger,
    IReadOnlyList<GameInputAction> Actions);
