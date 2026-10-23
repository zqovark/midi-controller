using System.Diagnostics;

namespace XMidi;

// Shared-note ownership for chords and slices, using the existing MIDI output delegate.
internal sealed class MidiVoices : IDisposable
{
    private sealed record Voice(byte[] Notes, double? EndsAt);
    private readonly Dictionary<int, Voice> voices = [];
    private readonly HashSet<byte> sounding = [];
    private readonly object gate = new();
    private readonly Action<byte, byte, byte> send;
    private readonly Func<double> now;
    private readonly bool background;
    private readonly AutoResetEvent wake = new(false);
    private Thread? worker;
    private bool disposed;
    public event Action<Exception>? Failed;
    public MidiVoices(Action<byte, byte, byte> send, bool background = true, Func<double>? clock = null)
    {
        this.send = send; this.background = background;
        now = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }
    public int[] ActivePads { get { lock (gate) return voices.Keys.ToArray(); } }
    public byte[] ActiveNotes { get { lock (gate) return sounding.Order().ToArray(); } }
    public void Play(int pad, byte[] notes, byte velocity, double? duration = null)
    {
        lock (gate)
        {
            if (disposed) return;
            Release(pad);
            var unique = notes.Distinct().ToArray();
            voices[pad] = new(unique, duration is { } d ? now() + Math.Max(.001, d) : null);
            foreach (byte note in unique)
                if (sounding.Add(note)) send(0x90, note, (byte)Math.Clamp((int)velocity, 1, 127));
            if (duration is not null && background && worker is null)
            {
                worker = new Thread(Run) { IsBackground = true, Name = "X-MIDI slice note release" }; worker.Start();
            }
            wake.Set();
        }
    }
    public void Release(int pad)
    {
        lock (gate)
        {
            if (disposed) return;
            if (voices.Remove(pad, out var voice))
                foreach (byte note in voice.Notes)
                    if (!voices.Values.Any(v => v.Notes.Contains(note))) NoteOff(note);
            wake.Set();
        }
    }
    private void NoteOff(byte note)
    {
        if (!sounding.Contains(note)) return;
        send(0x80, note, 0); sounding.Remove(note);
    }
    public void Stop()
    {
        lock (gate)
        {
            if (disposed) return;
            voices.Clear();
            Exception? failure = null;
            foreach (byte note in sounding.ToArray()) try { NoteOff(note); } catch (Exception ex) { failure ??= ex; }
            wake.Set();
            if (failure is not null) throw failure;
        }
    }
    public double Tick(double time)
    {
        lock (gate)
        {
            if (disposed) return double.PositiveInfinity;
            foreach (int pad in voices.Where(v => v.Value.EndsAt <= time).Select(v => v.Key).ToArray()) Release(pad);
            return voices.Values.Where(v => v.EndsAt.HasValue).Select(v => v.EndsAt!.Value).DefaultIfEmpty(double.PositiveInfinity).Min();
        }
    }
    private void Run()
    {
        while (true)
        {
            lock (gate) if (disposed) return;
            try
            {
                double next = Tick(now());
                if (double.IsPositiveInfinity(next)) wake.WaitOne();
                else wake.WaitOne(Math.Max(1, (int)Math.Ceiling((next - now()) * 1000)));
            }
            catch (Exception ex)
            {
                try { Stop(); } catch (Exception) { }
                Failed?.Invoke(ex);
            }
        }
    }
    public void Dispose()
    {
        Exception? error = null;
        lock (gate)
        {
            if (disposed) return;
            try { Stop(); } catch (Exception ex) { error = ex; }
            finally { disposed = true; wake.Set(); }
        }
        worker?.Join(); wake.Dispose();
        if (error is not null) throw error;
    }
}
