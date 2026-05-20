using Eden.UwpPorting.Core;
using Windows.Gaming.Input;

namespace Eden.UwpPorting.Input;

public interface IInputProvider
{
    void Poll();
}

public sealed class XboxInputProvider : IInputProvider
{
    public void Poll()
    {
        var gamepad = Gamepad.Gamepads.FirstOrDefault();
        if (gamepad is null)
        {
            RuntimeSharedState.Input.Buttons = 0;
            return;
        }

        GamepadReading r = gamepad.GetCurrentReading();

        ulong buttons = 0;
        if (r.Buttons.HasFlag(GamepadButtons.B)) buttons |= 1UL << 0;  // A (Switch)
        if (r.Buttons.HasFlag(GamepadButtons.A)) buttons |= 1UL << 1;  // B
        if (r.Buttons.HasFlag(GamepadButtons.Y)) buttons |= 1UL << 2;  // X
        if (r.Buttons.HasFlag(GamepadButtons.X)) buttons |= 1UL << 3;  // Y
        if (r.Buttons.HasFlag(GamepadButtons.LeftShoulder)) buttons |= 1UL << 4;
        if (r.Buttons.HasFlag(GamepadButtons.RightShoulder)) buttons |= 1UL << 5;
        if (r.Buttons.HasFlag(GamepadButtons.Menu)) buttons |= 1UL << 6; // Plus
        if (r.Buttons.HasFlag(GamepadButtons.View)) buttons |= 1UL << 7; // Minus

        RuntimeSharedState.Input.Buttons = buttons;
        RuntimeSharedState.Input.Lx = (float)r.LeftThumbstickX;
        RuntimeSharedState.Input.Ly = (float)r.LeftThumbstickY;
        RuntimeSharedState.Input.Rx = (float)r.RightThumbstickX;
        RuntimeSharedState.Input.Ry = (float)r.RightThumbstickY;
    }
}
