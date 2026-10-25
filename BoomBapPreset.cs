using Vortice.XInput;

namespace XMidi;

internal sealed class BoomBapPreset : IDisposable
{
    private readonly MidiOutput output;
    private readonly ManualTempoSource tempo = new();
    private readonly DrumEngine drums;
    private readonly MidiVoices voices;
    private readonly int[] banks = new int[3];
    private byte[] previousChord = [];
    public int[] ActivePads => voices.ActivePads;
    public byte[] ActiveNotes => voices.ActiveNotes;
    public byte CurrentVelocity => drums.CurrentVelocity;
    public bool IsRepeating => drums.IsRepeating;
    public event Action<Exception>? RepeatFailed;
    private readonly HashSet<GamepadButtons> downButtons = [];
    private readonly int[] lastCc = [-1, -1, -1, -1];
    private readonly long[] lastCcTime = new long[4];
    private bool rt;
    public bool EffectsPrepared { get; private set; }
    public bool MusicalInputsHeld => rt || downButtons.Any(b => PadIndex(b) >= 0 || b == GamepadButtons.LeftShoulder || b == GamepadButtons.RightShoulder);
    public AppSettings Settings { get; private set; }
    public int ActiveBank => banks[(int)Settings.Mode];
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
        voices = new(output.Send, background, clock);
        voices.Failed += ex => RepeatFailed?.Invoke(ex);
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
            if (button == GamepadButtons.LeftShoulder) drums.Pad(4, 0, false);
            int releasedPad = PadIndex(button);
            if (releasedPad >= 0)
            {
                if (Settings.Mode == MusicalMode.Drum) drums.Pad(releasedPad, 0, false);
                else if (Settings.Mode == MusicalMode.Chord || Settings.Samples.Playback == SlicePlayback.Gate) voices.Release(releasedPad);
            }
            return;
        }
        if (!downButtons.Add(button)) return;
        if (button == GamepadButtons.RightShoulder)
        { if (Settings.Mode == MusicalMode.Drum) drums.Shoulder(true); return; }
        switch (button)
        {
            case GamepadButtons.DPadLeft: SelectBank(0); return;
            case GamepadButtons.DPadRight: SelectBank(1); return;
            case GamepadButtons.DPadUp: ChangeDivision(1); return;
            case GamepadButtons.DPadDown: ChangeDivision(-1); return;
            case GamepadButtons.LeftShoulder:
                if (Settings.Mode == MusicalMode.Drum && output.IsOpen) drums.Pad(4, (byte)Settings.Banks[0][0].Note, true);
                return;
        }
        int pad = PadIndex(button);
        if (!output.IsOpen || pad < 0) return;
        if (Settings.Mode == MusicalMode.Chord)
        {
            var notes = ChordGenerator.Generate(Settings.Chords, ActiveBank * 4 + pad, previousChord);
            voices.Play(pad, notes, CurrentVelocity); previousChord = notes; return;
        }
        if (Settings.Mode == MusicalMode.Sample)
        {
            var sample = Settings.Samples;
            var mapping = sample.Pads[ActiveBank * 4 + pad];
            if (mapping.Region < 0) return;
            double duration = sample.Duration(mapping.Region);
            if (duration <= 0) { CommandSent?.Invoke("Selecione um WAV e exporte os slices antes de tocar."); return; }
            voices.Play(pad, [(byte)mapping.Note], CurrentVelocity, sample.Playback == SlicePlayback.OneShot ? duration : null);
            return;
        }
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
        => SelectBank(1 - ActiveBank);

    public void SelectBank(int bank)
    {
        if (bank == ActiveBank) return;
        if (MusicalInputsHeld) { CommandSent?.Invoke("Solte pads, LB, RT e RB antes de trocar banco."); return; }
        ReleaseNotes();
        banks[(int)Settings.Mode] = Math.Clamp(bank, 0, 1);
        Changed?.Invoke();
    }

    public void ChangeDivision(int delta)
    {
        if (Settings.Mode != MusicalMode.Drum || downButtons.Contains(GamepadButtons.RightShoulder)) return;
        Settings.RepeatDivision = (RepeatDivision)Math.Clamp((int)Settings.RepeatDivision + delta, 0, 6);
        ConfigureDrums(); Changed?.Invoke();
    }

    public void RightTrigger(byte value)
    {
        bool pressed = rt ? value > 70 : value >= 100;
        if (pressed == rt) return;
        rt = pressed;
        if (Settings.Mode == MusicalMode.Drum && output.IsOpen)
            drums.Pad(5, (byte)Settings.Banks[0][1].Note, pressed);
    }

    public void SetFullLevel(bool enabled)
    { Settings.FullLevel = enabled; ConfigureDrums(); Changed?.Invoke(); }

    public void SetMode(MusicalMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (Settings.Mode == mode) return;
        ReleaseNotes();
        Settings.Mode = mode;
        previousChord = [];
        EffectsPrepared = false;
        Array.Fill(lastCc, -1); Array.Clear(lastCcTime);
        Changed?.Invoke();
    }
    public string PadDescription(int pad)
    {
        int index = ActiveBank * 4 + pad;
        return Settings.Mode switch
        {
            MusicalMode.Sample => Settings.Samples.Pads[index].Region < 0 ? "Sem região"
                : $"Slice {Settings.Samples.Pads[index].Region + 1} · {Settings.Samples.Pads[index].Note}",
            MusicalMode.Chord => $"{Settings.Chords.Pads[index].Name} · {ChordGenerator.Describe(ChordGenerator.Generate(Settings.Chords, index))}",
            _ => $"{Settings.Banks[ActiveBank][pad].Name} · {Settings.Banks[ActiveBank][pad].Note}"
        };
    }

    public void ApplyModeSettings(ChordSettings? chords = null, SampleSettings? samples = null)
    {
        chords?.Validate(); samples?.Validate();
        if (chords is not null && Settings.Mode == MusicalMode.Chord || samples is not null && Settings.Mode == MusicalMode.Sample)
        { ReleaseNotes(); previousChord = []; }
        if (chords is not null) Settings.Chords = chords.Copy();
        if (samples is not null) Settings.Samples = samples.Copy();
        Array.Fill(lastCc, -1); Array.Clear(lastCcTime);
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
        CommandSent?.Invoke("Use Menu: transporte requer retorno confirmado do FL.");
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

    private void ReleaseNotes()
    {
        try { drums.Stop(); }
        finally { voices.Stop(); }
    }
    public void SetTrigger(byte lt) => drums.Trigger(lt);
    public void ConfigureDrums()
    {
        Settings.Validate();
        drums.Configure(Settings);
    }
    public void Dispose()
    {
        try { drums.Dispose(); }
        finally { voices.Dispose(); }
    }
    public void Analog(Gamepad gamepad)
    {
        SetTrigger(gamepad.LeftTrigger);
        if (!output.IsOpen || !EffectsPrepared) return;
        if (Settings.Mode == MusicalMode.Sample)
        {
            for (int i = 0; i < Settings.Samples.Effects.Length; i++)
            {
                var effect = Settings.Samples.Effects[i];
                if (effect.Enabled) SendCc(i, (byte)effect.Cc, effect.Stick == 0 ? gamepad.LeftThumbX : gamepad.RightThumbX,
                    effect.Stick == 0 ? Gamepad.LeftThumbDeadZone : Gamepad.RightThumbDeadZone);
            }
            return;
        }
        SendCc(0, 74, gamepad.LeftThumbX, Gamepad.LeftThumbDeadZone);
        SendCc(1, 91, gamepad.RightThumbX, Gamepad.RightThumbDeadZone);
    }
    private void SendCc(int index, byte cc, short raw, int deadZone)
    {
        int percent = ControllerReader.ThumbPercent(raw, deadZone);
        if (percent == 0 || lastCc[index] < 0) return;
        int value = Math.Clamp(lastCc[index] + (int)Math.Round(percent / 20.0), 0, 127);
        long now = Environment.TickCount64;
        if (value == lastCc[index] || now - lastCcTime[index] < 35) return;
        output.Send(0xB0, cc, (byte)value);
        lastCc[index] = value;
        lastCcTime[index] = now;
    }
    // Explicit preparation establishes a known value; never performed on connect or mode change.
    public void PrepareEffects()
    {
        if (!output.IsOpen) return;
        Array.Fill(lastCc, 64);
        if (Settings.Mode == MusicalMode.Sample)
        {
            foreach (var e in Settings.Samples.Effects.Where(e => e.Enabled)) output.Send(0xB0, (byte)e.Cc, 64);
        }
        else { output.Send(0xB0, 74, 64); output.Send(0xB0, 91, 64); }
        EffectsPrepared = true;
        CommandSent?.Invoke("Efeitos preparados em 64; sticks X ajustam relativamente. Centro mantém valor.");
    }
    public void Silence()
    {
        ReleaseNotes();
        if (output.IsOpen) output.Send(0xB0, 123, 0);
        if (output.IsOpen) output.Send(0xB0, 120, 0);
        Reset();
    }
    public void Reset()
    {
        ReleaseNotes();
        previousChord = [];
        downButtons.Clear();
        rt = false; EffectsPrepared = false;
        drums.Trigger(0);
        Array.Fill(lastCc, -1);
        Array.Clear(lastCcTime);
    }
}
