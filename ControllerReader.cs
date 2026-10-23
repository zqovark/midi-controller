using Vortice.XInput;

namespace XMidi;

// Console e interface compartilham a leitura existente do XInput.
internal sealed class ControllerReader
{
    private readonly bool[] connected = new bool[4];
    private readonly GamepadButtons[] previousButtons = new GamepadButtons[4];
    private readonly (int X, int Y)[,] previousAnalog = new (int X, int Y)[4, 4];
    private readonly long[,] lastAnalogPrint = new long[4, 4];
    private readonly GamepadButtons[] buttons = Enum.GetValues<GamepadButtons>()
        .Where(button => button != GamepadButtons.None && button != GamepadButtons.Guide).ToArray();
    public event Action<uint, bool>? ConnectionChanged;
    public event Action<uint, GamepadButtons, bool>? ButtonChanged;
    public event Action<uint, int, int, int>? AnalogChanged;
    public event Action<uint, bool, Gamepad>? Sampled;

    public void Poll()
    {
        for (uint index = 0; index < connected.Length; index++)
        {
            bool isConnected = XInput.GetState(index, out var state);
            if (isConnected != connected[index])
            {
                connected[index] = isConnected;
                ConnectionChanged?.Invoke(index, isConnected);
            }
            var currentButtons = isConnected ? state.Gamepad.Buttons : GamepadButtons.None;
            var changedButtons = currentButtons ^ previousButtons[index];
            foreach (var button in buttons)
                if ((changedButtons & button) != 0)
                    ButtonChanged?.Invoke(index, button, (currentButtons & button) != 0);
            previousButtons[index] = currentButtons;
            var gamepad = isConnected ? state.Gamepad : default;
            Sampled?.Invoke(index, isConnected, gamepad);
            ReportAnalog(index, 0, ThumbPercent(gamepad.LeftThumbX, Gamepad.LeftThumbDeadZone), ThumbPercent(gamepad.LeftThumbY, Gamepad.LeftThumbDeadZone));
            ReportAnalog(index, 1, ThumbPercent(gamepad.RightThumbX, Gamepad.RightThumbDeadZone), ThumbPercent(gamepad.RightThumbY, Gamepad.RightThumbDeadZone));
            ReportAnalog(index, 2, (int)Math.Round(gamepad.LeftTrigger * 100.0 / byte.MaxValue));
            ReportAnalog(index, 3, (int)Math.Round(gamepad.RightTrigger * 100.0 / byte.MaxValue));
        }
    }

    private void ReportAnalog(uint controller, int input, int x, int y = 0)
    {
        long now = Environment.TickCount64;
        if (previousAnalog[controller, input] == (x, y) || now - lastAnalogPrint[controller, input] < 100) return;
        AnalogChanged?.Invoke(controller, input, x, y);
        previousAnalog[controller, input] = (x, y);
        lastAnalogPrint[controller, input] = now;
    }

    public static int ThumbPercent(short value, int deadZone) => Math.Abs((int)value) <= deadZone
        ? 0 : (int)Math.Round(value * 100.0 / (value < 0 ? 32768 : 32767));
}
