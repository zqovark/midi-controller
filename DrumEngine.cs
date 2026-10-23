using System.Diagnostics;

namespace XMidi;

internal enum VelocityCurve { Linear, Soft, Accented }
internal enum RepeatDivision { Quarter, Eighth, Sixteenth, ThirtySecond, EighthTriplet, SixteenthTriplet, ThirtySecondTriplet }

// A tempo source supplies speed only. It does not imply transport/grid phase synchronization.
internal interface ITempoSource { double Bpm { get; } }
internal sealed class ManualTempoSource : ITempoSource { public double Bpm { get; set; } = 120; }

internal static class DrumTiming
{
    public static byte Velocity(byte lt, int normal, int minimum, VelocityCurve curve)
    {
        double t = lt / 255.0;
        double response = curve switch
        {
            VelocityCurve.Soft => t * t,
            VelocityCurve.Accented => 1 - (1 - t) * (1 - t),
            _ => t
        };
        return (byte)Math.Clamp((int)Math.Round(normal + (minimum - normal) * response), 1, 127);
    }

    public static double Interval(double bpm, RepeatDivision division)
    {
        if (!double.IsFinite(bpm) || bpm <= 0) throw new ArgumentOutOfRangeException(nameof(bpm));
        double beats = division switch
        {
            RepeatDivision.Quarter => 1, RepeatDivision.Eighth => .5,
            RepeatDivision.Sixteenth => .25, RepeatDivision.ThirtySecond => .125,
            RepeatDivision.EighthTriplet => 1.0 / 3,
            RepeatDivision.SixteenthTriplet => 1.0 / 6,
            RepeatDivision.ThirtySecondTriplet => 1.0 / 12,
            _ => throw new ArgumentOutOfRangeException(nameof(division))
        };
        return 60 / bpm * beats;
    }
}

// All MIDI note ownership and deadlines share one lock, including input cancellation.
// The worker never waits on WPF. Tick is also driven by a fake clock in deterministic tests.
internal sealed class DrumEngine : IDisposable
{
    private sealed class Voice(byte note, bool repeating, double next)
    {
        public byte Note = note;
        public bool Repeating = repeating;
        public double Next = next;
    }
    private readonly object gate = new();
    private readonly Dictionary<int, Voice> voices = [];
    private readonly Dictionary<int, byte> heldPads = [];
    private readonly HashSet<byte> sounding = [];
    private readonly Dictionary<byte, int> notePads = [];
    public event Action<int, byte, bool>? NotePlayed;
    private readonly Action<byte, byte, byte> send;
    private readonly Func<double> now;
    private readonly ITempoSource tempo;
    private readonly AutoResetEvent wake = new(false);
    private readonly bool background;
    private Thread? worker;
    private bool disposed, rb;
    private byte lt;
    private AppSettings settings = new();
    public event Action<Exception>? Failed;

    public DrumEngine(Action<byte, byte, byte> send, ITempoSource tempo, bool background = true, Func<double>? clock = null)
    {
        this.send = send; this.tempo = tempo; this.background = background;
        now = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }
    public bool IsRepeating { get { lock (gate) return voices.Values.Any(v => v.Repeating); } }
    public byte CurrentVelocity { get { lock (gate) return Velocity(); } }
    private byte Velocity() => DrumTiming.Velocity(lt, settings.DefaultVelocity, settings.MinimumVelocity, settings.VelocityCurve);

    public void Configure(AppSettings value)
    {
        value.Validate();
        lock (gate)
        {
            if (disposed) return;
            settings = value.Copy();
            if (tempo is ManualTempoSource manual) manual.Bpm = settings.RepeatBpm;
            if (!settings.NoteRepeatEnabled) StopRepeats();
            // A speed/division change starts a new interval, without a burst of missed hits.
            foreach (var v in voices.Values.Where(v => v.Repeating)) v.Next = now() + Interval();
            wake.Set();
        }
    }
    private double Interval() => DrumTiming.Interval(tempo.Bpm, settings.RepeatDivision);
    public void Trigger(byte value) { lock (gate) lt = value; }

    public void Shoulder(bool pressed)
    {
        lock (gate)
        {
            if (disposed || rb == pressed) return;
            rb = pressed;
            if (!pressed) StopRepeats();
            else if (settings.NoteRepeatEnabled)
            {
                foreach (var (pad, note) in heldPads)
                {
                    if (!voices.TryGetValue(pad, out var v))
                    {
                        v = new(note, true, now() + Interval());
                        voices.Add(pad, v);
                        NoteOn(note);
                    }
                    v.Repeating = true; v.Next = now() + Interval();
                }
                StartWorker();
            }
            wake.Set();
        }
    }
    public void Pad(int pad, byte note, bool pressed)
    {
        lock (gate)
        {
            if (disposed) return;
            if (!pressed)
            {
                heldPads.Remove(pad);
                if (voices.Remove(pad, out var released)) ReleaseIfUnused(released.Note);
            }
            else if (!voices.ContainsKey(pad))
            {
                heldPads[pad] = note;
                bool repeat = rb && settings.NoteRepeatEnabled;
                voices.Add(pad, new(note, repeat, now() + Interval()));
                NoteOn(note);
                if (repeat) StartWorker();
            }
            wake.Set();
        }
    }
    private void NoteOn(byte note)
    {
        if (sounding.Contains(note)) return;
        // Retain ownership even if the driver/event sink fails after delivering Note On.
        sounding.Add(note);
        int pad = voices.First(v => v.Value.Note == note).Key;
        notePads[note] = pad;
        byte velocity = Velocity();
        send(0x90, note, velocity);
        NotePlayed?.Invoke(pad, velocity, true);
    }
    private void NoteOff(byte note)
    {
        if (!sounding.Contains(note)) return;
        send(0x80, note, 0);
        sounding.Remove(note);
        if (notePads.Remove(note, out int pad)) NotePlayed?.Invoke(pad, 0, false);
    }
    private void ReleaseIfUnused(byte note)
    {
        if (!voices.Values.Any(v => v.Note == note)) NoteOff(note);
    }
    private void StopRepeats()
    {
        foreach (int key in voices.Where(v => v.Value.Repeating).Select(v => v.Key).ToArray())
        {
            byte note = voices[key].Note;
            voices.Remove(key);
            ReleaseIfUnused(note);
        }
    }
    public void Stop()
    {
        lock (gate)
        {
            if (disposed) return;
            voices.Clear(); heldPads.Clear(); rb = false;
            // Try every note even if one driver send fails; retain failed notes for a retry.
            Exception? failure = null;
            foreach (byte note in sounding.ToArray())
                try { NoteOff(note); } catch (Exception ex) { failure ??= ex; }
            wake.Set();
            if (failure is not null) throw failure;
        }
    }
    public double Tick(double time)
    {
        lock (gate)
        {
            if (disposed) return double.PositiveInfinity;
            var due = voices.Values.Where(v => v.Repeating && v.Next <= time).ToArray();
            foreach (byte note in due.Select(v => v.Note).Distinct())
            {
                NoteOff(note);
                NoteOn(note);
                // Duplicate mappings share one MIDI voice and one retrigger deadline.
                foreach (var v in voices.Values.Where(v => v.Repeating && v.Note == note))
                {
                    double interval = Interval();
                    v.Next += (Math.Floor(Math.Max(0, time - v.Next) / interval) + 1) * interval;
                }
            }
            return voices.Values.Where(v => v.Repeating).Select(v => v.Next).DefaultIfEmpty(double.PositiveInfinity).Min();
        }
    }
    private void StartWorker()
    {
        if (!background || worker is not null) return;
        worker = new Thread(Run) { IsBackground = true, Name = "X-MIDI Note Repeat" };
        worker.Start();
    }
    private void Run()
    {
        while (true)
        {
            lock (gate) if (disposed) return;
            try
            {
                double next = Tick(now());
                if (double.IsPositiveInfinity(next)) { wake.WaitOne(); continue; }
                double remaining = next - now();
                if (remaining > .002) wake.WaitOne(Math.Max(1, (int)(remaining * 1000) - 1));
                else if (remaining > 0) { if (!wake.WaitOne(0)) Thread.SpinWait(64); }
            }
            catch (Exception ex)
            {
                lock (gate) { voices.Clear(); rb = false; }
                try { Stop(); } catch (Exception) { /* The UI closes the failed MIDI port. */ }
                Failed?.Invoke(ex);
                wake.WaitOne();
            }
        }
    }
    public void Dispose()
    {
        Exception? failure = null;
        lock (gate)
        {
            if (disposed) return;
            try { Stop(); } catch (Exception ex) { failure = ex; }
            finally { disposed = true; wake.Set(); }
        }
        worker?.Join();
        wake.Dispose();
        if (failure is not null) throw failure;
    }
}
