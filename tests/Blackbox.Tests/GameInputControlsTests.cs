using Blackbox.Domain;

namespace Blackbox.Tests;

public sealed class GameInputControlsTests
{
    [Fact]
    public void Validate_rejects_conflicting_enabled_bindings()
    {
        var controls = new GameInputControls
        {
            Bindings =
            [
                Binding(GameInputAction.AddMarker, "Ctrl+Shift+F6"),
                Binding(GameInputAction.ProtectRecentFootage, "ctrl+shift+f6")
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(controls.Validate);

        Assert.Contains("multiple actions", exception.Message);
        Assert.Single(controls.GetConflicts());
    }

    [Fact]
    public void Validate_allows_the_same_input_on_different_devices()
    {
        var controls = new GameInputControls
        {
            Bindings =
            [
                Binding(GameInputAction.AddMarker, "X"),
                Binding(
                    GameInputAction.SaveInstantClip,
                    "X",
                    GameInputDevice.Controller)
            ]
        };

        controls.Validate();

        Assert.Empty(controls.GetConflicts());
    }

    [Fact]
    public void Validate_requires_opt_in_for_recording_control()
    {
        var controls = new GameInputControls
        {
            Bindings =
            [
                Binding(GameInputAction.ToggleRecording, "Ctrl+Shift+F10")
            ]
        };

        Assert.Throws<InvalidOperationException>(controls.Validate);
    }

    [Fact]
    public void Validate_accepts_bounded_hold_bindings()
    {
        var controls = new GameInputControls
        {
            Bindings =
            [
                Binding(GameInputAction.SaveInstantClip, "GamepadView") with
                {
                    Device = GameInputDevice.Controller,
                    Trigger = GameInputTrigger.Hold,
                    HoldMilliseconds = 750
                }
            ]
        };

        controls.Validate();
    }

    private static GameInputBinding Binding(
        GameInputAction action,
        string input,
        GameInputDevice device = GameInputDevice.Keyboard) => new()
        {
            Action = action,
            Device = device,
            Input = input
        };
}
