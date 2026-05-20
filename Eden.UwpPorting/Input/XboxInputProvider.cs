using Windows.Gaming.Input;

namespace Eden.UwpPorting.Input;

public interface IInputProvider
{
    void Poll();
}

public sealed class XboxInputProvider : IInputProvider
{
    private GamepadReading _last;

    public void Poll()
    {
        var gamepad = Gamepad.Gamepads.FirstOrDefault();
        if (gamepad is null) return;

        _last = gamepad.GetCurrentReading();

        // Mapeamento base para HID do Switch:
        // A/B invertidos conforme layout Nintendo.
        bool a = _last.Buttons.HasFlag(GamepadButtons.B);
        bool b = _last.Buttons.HasFlag(GamepadButtons.A);
        bool x = _last.Buttons.HasFlag(GamepadButtons.Y);
        bool y = _last.Buttons.HasFlag(GamepadButtons.X);

        _ = (a, b, x, y);
    }
}
