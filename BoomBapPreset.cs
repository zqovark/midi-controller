using Vortice.XInput;

namespace XMidi;

internal sealed class BoomBapPreset : IDisposable
{
    private readonly MidiOutput output;
    private readonly ManualTempoSource tempo = new();
    private readonly DrumEngine drums;
    public SequenceEngine Sequencer { get; }
    public bool MidiReady => output.IsOpen;
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
        var ownership = new MidiNoteOwnership(output.Send);
        Sequencer = new((s, n, v) => ownership.Send(1, s, n, v), background, clock);
        Sequencer.SetMappings(Settings.Banks);
        Sequencer.Swing = Settings.SwingPercent;
        Sequencer.Failed += ex => RepeatFailed?.Invoke(ex);
        drums = new((s, n, v) => ownership.Send(0, s, n, v), tempo, background, clock);
        drums.NotePlayed += (pad, velocity, on) => Sequencer.CaptureDrum(ActiveBank * 4 + pad, velocity, on);
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
            case GamepadButtons.DPadDown: ToggleBank(); return;
            case GamepadButtons.DPadUp: PlayPause(); return;
            case GamepadButtons.DPadLeft: SetSwing(Settings.SwingPercent - Settings.SwingStep); return;
            case GamepadButtons.DPadRight: SetSwing(Settings.SwingPercent + Settings.SwingStep); return;
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
    {
        ReleaseNotes();
        banks[(int)Settings.Mode] = 1 - ActiveBank;
        Changed?.Invoke();
    }

    public void SetMode(MusicalMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (Settings.Mode == mode) return;
        Sequencer.Stop();
        ReleaseNotes();
        Settings.Mode = mode;
        previousChord = [];
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
        Sequencer.Stop();
        ReleaseNotes();
        Settings = settings;
        Sequencer.SetMappings(Settings.Banks);
        ConfigureDrums();
        SyncSwing();
        Changed?.Invoke();
    }

    public void PlayPause()
    {
        if (Sequencer.IsPlaying) Sequencer.Stop();
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
        Sequencer.Swing = Settings.SwingPercent;
        if (Sequencer.IsPlaying)
        { CommandSent?.Invoke($"Swing local {Settings.SwingPercent}% · não envia swing ao FL durante o sequenciador."); return; }
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
        try { Sequencer.Dispose(); }
        finally { try { drums.Dispose(); } finally { voices.Dispose(); } }
    }
    public void Analog(Gamepad gamepad)
    {
        SetTrigger(gamepad.LeftTrigger);
        if (!output.IsOpen) return;
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
        int value = (int)Math.Round((percent + 100) * 127.0 / 200);
        long now = Environment.TickCount64;
        if (value == lastCc[index] || now - lastCcTime[index] < 35) return;
        output.Send(0xB0, cc, (byte)value);
        lastCc[index] = value;
        lastCcTime[index] = now;
    }
    public void Silence()
    {
        Sequencer.Stop();
        ReleaseNotes();
        if (output.IsOpen) output.Send(0xB0, 123, 0);
        Reset();
    }
    public void Reset()
    {
        Sequencer.Stop();
        ReleaseNotes();
        previousChord = [];
        downButtons.Clear();
        Array.Fill(lastCc, -1);
        Array.Clear(lastCcTime);
    }
}
