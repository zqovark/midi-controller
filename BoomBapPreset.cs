using Vortice.XInput;

namespace XMidi;

internal sealed class BoomBapPreset : IDisposable
{
    private readonly MidiOutput output;
    private readonly ManualTempoSource tempo = new();
    private readonly DrumEngine drums;
    public byte CurrentVelocity => drums.CurrentVelocity;
    public bool IsRepeating => drums.IsRepeating;
    public event Action<Exception>? RepeatFailed;
    private readonly HashSet<GamepadButtons> downButtons = [];
    private readonly int[] lastCc = [-1, -1];
    private readonly long[] lastCcTime = new long[2];
    public AppSettings Settings { get; private set; }
    public int ActiveBank { get; private set; }
    public event Action? Changed;
    public event Action<string>? CommandSent;

    public BoomBapPreset(MidiOutput output, AppSettings? settings = null, bool background = true, Func<double>? clock = null)
    {
        this.output = output;
        Settings = settings ?? new();
        Settings.Validate();
        tempo.Bpm = Settings.RepeatBpm;
        drums = new(output.Send, tempo, background, clock);
        drums.Configure(Settings);
        drums.Failed += ex => RepeatFailed?.Invoke(ex);
    }

    // Mantém o mapeamento original do preset Classic como referência.
    public static byte? NoteFor(GamepadButtons button) => button switch
    {
        GamepadButtons.A => 36, GamepadButtons.B => 38,
        GamepadButtons.X => 42, GamepadButtons.Y => 46, _ => null
    };
    public void Button(GamepadButtons button, bool pressed)
    {
        if (!pressed)
        {
            downButtons.Remove(button);
            if (button == GamepadButtons.RightShoulder) drums.Shoulder(false);
            int releasedPad = PadIndex(button);
            if (releasedPad >= 0) drums.Pad(releasedPad, 0, false);
            return;
        }
        if (!downButtons.Add(button)) return;
        if (button == GamepadButtons.RightShoulder) { drums.Shoulder(true); return; }
        switch (button)
        {
            case GamepadButtons.DPadDown: ToggleBank(); return;
            case GamepadButtons.DPadUp: PlayPause(); return;
            case GamepadButtons.DPadLeft: SetSwing(Settings.SwingPercent - Settings.SwingStep); return;
            case GamepadButtons.DPadRight: SetSwing(Settings.SwingPercent + Settings.SwingStep); return;
        }
        int pad = PadIndex(button);
        if (!output.IsOpen || pad < 0) return;
        byte note = (byte)Settings.Banks[ActiveBank][pad].Note;
        drums.Shoulder(downButtons.Contains(GamepadButtons.RightShoulder));
        drums.Pad(pad, note, true);
    }

    private static int PadIndex(GamepadButtons button) => button switch
    {
        GamepadButtons.A => 0, GamepadButtons.B => 1,
        GamepadButtons.X => 2, GamepadButtons.Y => 3, _ => -1
    };

    public void ToggleBank()
    {
        ReleaseNotes();
        ActiveBank = 1 - ActiveBank;
        Changed?.Invoke();
    }

    public void ApplySettings(AppSettings settings)
    {
        settings.Validate();
        ReleaseNotes();
        Settings = settings;
        ConfigureDrums();
        SyncSwing();
        Changed?.Invoke();
    }

    public void PlayPause()
    {
        if (!output.IsOpen) { CommandSent?.Invoke("Play/Pause não enviado: selecione uma porta MIDI."); return; }
        output.Send(0xBF, 102, 127);
        CommandSent?.Invoke("Play/Pause enviado ao FL · requer script X-MIDI.");
    }

    public void SetSwing(int percent)
    {
        int value = Math.Clamp(percent, 0, 100);
        if (value == Settings.SwingPercent) return;
        Settings.SwingPercent = value;
        SyncSwing();
        Changed?.Invoke();
    }

    public void SyncSwing()
    {
        if (!output.IsOpen) { CommandSent?.Invoke("Swing salvo localmente; selecione uma porta MIDI para enviar."); return; }
        output.Send(0xBF, 103, (byte)Math.Round(Settings.SwingPercent * 127.0 / 100));
        CommandSent?.Invoke($"Swing global {Settings.SwingPercent}% enviado · requer script X-MIDI.");
    }

    private void ReleaseNotes() => drums.Stop();
    public void SetTrigger(byte lt) => drums.Trigger(lt);
    public void ConfigureDrums()
    {
        Settings.Validate();
        drums.Configure(Settings);
    }
    public void Dispose() => drums.Dispose();
    public void Analog(Gamepad gamepad)
    {
        SetTrigger(gamepad.LeftTrigger);
        if (!output.IsOpen) return;
        SendCc(0, 74, gamepad.LeftThumbX, Gamepad.LeftThumbDeadZone);
        SendCc(1, 91, gamepad.RightThumbX, Gamepad.RightThumbDeadZone);
    }
    private void SendCc(int index, byte cc, short raw, int deadZone)
    {
        int percent = ControllerReader.ThumbPercent(raw, deadZone);
        int value = (int)Math.Round((percent + 100) * 127.0 / 200);
        long now = Environment.TickCount64;
        if (value == lastCc[index] || now - lastCcTime[index] < 35) return;
        output.Send(0xB0, cc, (byte)value);
        lastCc[index] = value;
        lastCcTime[index] = now;
    }
    public void Silence()
    {
        ReleaseNotes();
        if (output.IsOpen) output.Send(0xB0, 123, 0);
        Reset();
    }
    public void Reset()
    {
        drums.Stop();
        downButtons.Clear();
        Array.Fill(lastCc, -1);
        Array.Clear(lastCcTime);
    }
}
