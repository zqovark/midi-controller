using System.Diagnostics;

namespace XMidi;

internal sealed record SequenceStatus(bool Playing, bool Recording, bool CountIn, int CountInBeats, double AbsoluteTick, double LoopTick,
    int Bar, long Revision, int[] ActiveTracks);

internal sealed class SequenceEngine : IDisposable
{
    private sealed record Scheduled(double Tick, Guid NoteId, Guid Token, int Track, byte Pitch, byte Velocity, long Duration, int Channel, bool Off);
    private sealed record Active(Guid NoteId, int Track, byte Pitch, int Channel, double Start, double End);
    private sealed record Capture(Guid NoteId, int Track, double Start);
    private sealed class History { public readonly List<SequencePattern> Undo = []; public readonly Stack<SequencePattern> Redo = new(); }
    private readonly object gate = new();
    private readonly Action<byte, byte, byte> send;
    private readonly Func<double> now;
    private readonly bool background;
    private readonly AutoResetEvent wake = new(false);
    private readonly PriorityQueue<Scheduled, (double Tick, int Order, int Velocity, long Serial)> queue = new();
    private readonly Dictionary<Guid, Active> active = [];
    private readonly Dictionary<int, Capture> captures = [];
    private readonly Dictionary<Guid, long> firstCycle = [];
    private readonly Dictionary<Guid, History> histories = [];
    private SequenceLibrary library;
    private SequencePattern pattern;
    private SequencePattern? recordBefore;
    private byte[] pitches = [36, 38, 42, 46, 37, 54, 45, 49];
    private Thread? worker;
    private bool disposed, playing, recording, overdub = true;
    private double anchorSeconds, anchorTick, recordFrom;
    private long nextCycle, serial, revision, nextClick;
    private int swing;
    private bool metronome;
    private int metroChannel = 10, metroNote = 76, metroVelocity = 90;
    public event Action<Exception>? Failed;
    public SequenceEngine(Action<byte, byte, byte> send, bool background = true, Func<double>? clock = null)
    {
        this.send = send; this.background = background;
        now = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        library = new(); pattern = library.Patterns[0]; library.SelectedId = pattern.Id;
    }
    public bool IsPlaying { get { lock (gate) return playing; } }
    public bool IsRecording { get { lock (gate) return recording; } }
    public bool Overdub
    {
        get { lock (gate) return overdub; }
        set { lock (gate) { if (!value) EndRecording(); overdub = value; } }
    }
    public int Swing
    {
        get { lock (gate) return swing; }
        set { lock (gate) { swing = Math.Clamp(value, 0, 100); RebuildFuture(Position(now())); revision++; wake.Set(); } }
    }
    public SequencePattern Snapshot() { lock (gate) return pattern.Copy(); }
    public SequenceLibrary LibrarySnapshot()
    { lock (gate) return new() { Version = 1, SelectedId = pattern.Id, Patterns = library.Patterns.Select(p => p.Copy()).ToList() }; }
    public SequenceStatus Status()
    {
        lock (gate)
        {
            double tick = Position(now());
            bool preparing = playing && tick < (recording ? recordFrom : 0);
            int remaining = preparing ? (int)Math.Ceiling(((recording ? recordFrom : 0) - tick) / (SequenceTiming.Ppq * 4.0 / pattern.Denominator)) : 0;
            return new(playing, recording, preparing, remaining, tick,
                SequenceTiming.Wrap(Math.Max(0, tick), pattern.Length), (int)(SequenceTiming.Wrap(Math.Max(0, tick), pattern.Length) / pattern.BarTicks) + 1,
                revision, active.Values.Where(v => v.Track >= 0).Select(v => v.Track).Distinct().ToArray());
        }
    }
    private double Position(double time) => playing ? anchorTick + SequenceTiming.SecondsToTicks(time - anchorSeconds, pattern.Bpm) : anchorTick;
    private History GetHistory()
    { if (!histories.TryGetValue(pattern.Id, out var history)) histories[pattern.Id] = history = new(); return history; }
    private void PushUndo(SequencePattern before)
    {
        var h = GetHistory(); h.Undo.Add(before); if (h.Undo.Count > 32) h.Undo.RemoveAt(0); h.Redo.Clear();
    }
    private void Assign(SequencePattern value)
    { library.Patterns[library.Patterns.FindIndex(p => p.Id == pattern.Id)] = value; pattern = value; library.SelectedId = value.Id; }
    public void Edit(Action<SequencePattern> change)
    {
        lock (gate)
        {
            EndRecording();
            var before = pattern.Copy(); var edited = pattern.Copy(); change(edited); edited.Validate();
            if (edited.Id != pattern.Id) throw new InvalidOperationException("A edição deve preservar o identificador do padrão.");
            double position = Position(now());
            if (edited.Length != pattern.Length) { Stop(); position = 0; }
            PushUndo(before); Assign(edited);
            anchorTick = position; anchorSeconds = now();
            // Removed/edited events cannot retain an old note-off or keep sounding.
            foreach (var pair in active.ToArray())
            {
                var oldNote = before.Tracks.SelectMany(t => t.Events).FirstOrDefault(n => n.Id == pair.Value.NoteId);
                var newNote = pattern.Tracks.SelectMany(t => t.Events).FirstOrDefault(n => n.Id == pair.Value.NoteId);
                if (pair.Value.Track >= 0 && (oldNote is null || newNote is null || oldNote.OriginalTick != newNote.OriginalTick
                    || oldNote.OffsetTicks != newNote.OffsetTicks || oldNote.Duration != newNote.Duration || oldNote.Velocity != newNote.Velocity)) Release(pair.Key);
            }
            RebuildFuture(position); revision++; wake.Set();
        }
    }
    public void Undo()
    {
        lock (gate)
        {
            EndRecording(); var h = GetHistory(); if (h.Undo.Count == 0) return;
            Stop(); h.Redo.Push(pattern.Copy()); var value = h.Undo[^1]; h.Undo.RemoveAt(h.Undo.Count - 1); Assign(value.Copy()); revision++;
        }
    }
    public void Redo()
    {
        lock (gate)
        {
            EndRecording(); var h = GetHistory(); if (h.Redo.Count == 0) return;
            Stop(); h.Undo.Add(pattern.Copy()); Assign(h.Redo.Pop().Copy()); revision++;
        }
    }
    public void SetMappings(PadMapping[][] banks)
    {
        byte[] mapping = banks.SelectMany(b => b).Select(p => checked((byte)p.Note)).ToArray();
        if (mapping.Length != 8 || mapping.Any(n => n > 127)) throw new ArgumentException("Use os oito mapeamentos Drum.");
        lock (gate)
        {
            if (pitches.SequenceEqual(mapping)) return;
            EndRecording(); ClearActive(); pitches = mapping; RebuildFuture(Position(now())); revision++; wake.Set();
        }
    }
    public void LoadLibrary(SequenceLibrary value)
    {
        value.Validate();
        lock (gate)
        {
            Stop(); library = new() { SelectedId = value.SelectedId, Patterns = value.Patterns.Select(p => p.Copy()).ToList() };
            pattern = library.Patterns.Single(p => p.Id == library.SelectedId); histories.Clear(); revision++;
        }
    }
    public void Select(Guid id)
    {
        lock (gate) { var selected = library.Patterns.Single(p => p.Id == id); Stop(); pattern = selected; library.SelectedId = id; revision++; }
    }
    public void Create(bool duplicate = false)
    {
        lock (gate)
        {
            if (library.Patterns.Count >= 128) throw new InvalidOperationException("Limite de 128 padrões.");
            Stop(); var added = duplicate ? pattern.Copy() : new SequencePattern(); added.Id = Guid.NewGuid();
            added.Name = duplicate ? (pattern.Name.Length > 70 ? pattern.Name[..70] : pattern.Name) + " (cópia)" : $"Padrão {library.Patterns.Count + 1}";
            foreach (var note in added.Tracks.SelectMany(t => t.Events)) note.Id = Guid.NewGuid();
            library.Patterns.Add(added); pattern = added; library.SelectedId = added.Id; revision++;
        }
    }
    public void Import(SequencePattern value)
    {
        value.Validate();
        lock (gate)
        {
            if (library.Patterns.Count >= 128) throw new InvalidOperationException("Limite de 128 padrões.");
            Stop(); var copy = value.Copy(); copy.Id = Guid.NewGuid(); library.Patterns.Add(copy); pattern = copy; library.SelectedId = copy.Id; revision++;
        }
    }
    public void Delete()
    {
        lock (gate)
        {
            Stop(); histories.Remove(pattern.Id); library.Patterns.Remove(pattern);
            if (library.Patterns.Count == 0) library.Patterns.Add(new()); pattern = library.Patterns[0]; library.SelectedId = pattern.Id; revision++;
        }
    }
    public void Start(bool countIn = false)
    {
        lock (gate)
        {
            if (disposed || playing) return;
            firstCycle.Clear();
            playing = true; anchorSeconds = now(); anchorTick = countIn ? -pattern.BarTicks : 0;
            RebuildFuture(anchorTick - .0001); nextClick = (long)anchorTick; revision++;
            if (background && worker is null) { worker = new Thread(Run) { IsBackground = true, Name = "X-MIDI PPQ sequencer" }; worker.Start(); }
            wake.Set();
        }
    }
    public void BeginRecording(bool countIn)
    {
        lock (gate)
        {
            if (!overdub) throw new InvalidOperationException("Ative Overdub para gravar sem substituir eventos.");
            if (recording) return;
            bool wasPlaying = playing; Start(countIn);
            recordFrom = wasPlaying ? Position(now()) + (countIn ? pattern.BarTicks : 0) : 0;
            recordBefore = pattern.Copy(); recording = true; revision++; wake.Set();
        }
    }
    public void EndRecording()
    {
        lock (gate)
        {
            if (!recording) return;
            double at = Position(now()); foreach (int track in captures.Keys.ToArray()) FinishCapture(track, at);
            recording = false;
            if (recordBefore is { } before && !SameNotes(before, pattern)) PushUndo(before);
            recordBefore = null; revision++;
        }
    }
    private static bool SameNotes(SequencePattern a, SequencePattern b) => a.Bpm == b.Bpm && a.Quantize == b.Quantize
        && a.QuantizeStrength == b.QuantizeStrength && a.Tracks.Zip(b.Tracks).All(pair => pair.First.Events.Count == pair.Second.Events.Count
        && pair.First.Events.Zip(pair.Second.Events).All(n => n.First.Id == n.Second.Id && n.First.Duration == n.Second.Duration));
    public void CaptureDrum(int track, byte velocity, bool on)
    {
        lock (gate)
        {
            double at = Position(now());
            if (!recording || !playing || at < recordFrom || at < 0) return;
            if (!on) { FinishCapture(track, at); return; }
            if (track is < 0 or > 7 || velocity is 0 or > 127 || pattern.Tracks.Sum(t => t.Events.Count) >= 20000) return;
            FinishCapture(track, at);
            var note = new SequenceNote { OriginalTick = (long)SequenceTiming.Wrap(Math.Round(at), pattern.Length), Velocity = velocity, Duration = 1 };
            pattern.Tracks.Single(t => t.Instrument == track).Events.Add(note); captures[track] = new(note.Id, track, at);
            firstCycle[note.Id] = (long)Math.Floor(at / pattern.Length) + 1;
            revision++;
            // Add only this hit to already queued future cycles. Rebuilding the whole queue here
            // could discard another instrument's due note when input wins the scheduler lock.
            for (long cycle = firstCycle[note.Id]; cycle < nextCycle; cycle++)
                Enqueue(new(cycle * (double)pattern.Length + SequenceTiming.PlayTick(note, pattern, swing),
                    note.Id, Guid.NewGuid(), track, pitches[track], (byte)note.Velocity, note.Duration, 0, false));
            wake.Set();
        }
    }
    private void FinishCapture(int track, double at)
    {
        if (!captures.Remove(track, out var capture)) return;
        var note = pattern.Tracks.Single(t => t.Instrument == track).Events.FirstOrDefault(n => n.Id == capture.NoteId);
        if (note is null) return;
        note.Duration = Math.Clamp((long)Math.Round(at - capture.Start), 1, pattern.Length * 4);
        revision++;
    }
    public void SetQuantization(SequenceQuantize division, int strength)
    {
        if (!Enum.IsDefined(division) || strength is < 0 or > 100) throw new ArgumentException("Quantização: divisão válida e força 0–100%.");
        lock (gate)
        {
            if (pattern.Quantize == division && pattern.QuantizeStrength == strength) return;
            if (!recording) PushUndo(pattern.Copy());
            pattern.Quantize = division; pattern.QuantizeStrength = strength;
            RebuildFuture(Position(now())); revision++; wake.Set();
        }
    }

    public void SetBpm(double value)
    {
        if (!double.IsFinite(value) || value is < 20 or > 300) throw new ArgumentException("BPM: 20–300.");
        lock (gate)
        {
            double position = Position(now()); anchorTick = position; anchorSeconds = now();
            pattern.Bpm = value; revision++; wake.Set();
        }
    }

    public void ConfigureMetronome(bool enabled, int channel, int note, int velocity)
    {
        if (channel is < 1 or > 16 || note is < 0 or > 127 || velocity is < 1 or > 127) throw new ArgumentException("Metrônomo: canal 1–16, nota 0–127 e velocity 1–127.");
        lock (gate) { metronome = enabled; metroChannel = channel; metroNote = note; metroVelocity = velocity; wake.Set(); }
    }
    public void Stop()
    {
        lock (gate)
        {
            if (disposed) return;
            EndRecording(); playing = false; anchorTick = 0; queue.Clear(); firstCycle.Clear(); ClearActive(); revision++; wake.Set();
        }
    }
    private void Enqueue(Scheduled item) => queue.Enqueue(item, (item.Tick, item.Off ? 0 : 1, -item.Velocity, serial++));
    private void RebuildFuture(double after)
    {
        queue.Clear();
        if (!playing) return;
        foreach (var (token, voice) in active) Enqueue(new(voice.End, voice.NoteId, token, voice.Track, voice.Pitch, 0, 0, voice.Channel, true));
        nextCycle = Math.Max(0, (long)Math.Floor(after / pattern.Length));
        AppendCycle(nextCycle++, after); AppendCycle(nextCycle++, after);
    }
    private void AppendCycle(long cycle, double after)
    {
        foreach (var track in pattern.Tracks)
            foreach (var note in track.Events)
            {
                if (firstCycle.TryGetValue(note.Id, out long first) && cycle < first) continue;
                double tick = cycle * (double)pattern.Length + SequenceTiming.PlayTick(note, pattern, swing);
                if (tick > after) Enqueue(new(tick, note.Id, Guid.NewGuid(), track.Instrument, pitches[track.Instrument], (byte)note.Velocity, note.Duration, 0, false));
            }
    }
    private void StartVoice(Scheduled item)
    {
        if (item.Track >= 0)
        {
            var note = pattern.Tracks.Single(t => t.Instrument == item.Track).Events.FirstOrDefault(n => n.Id == item.NoteId);
            if (note is null) return;
            item = item with { Duration = note.Duration }; // A recorded release can arrive after its future On was queued.
        }
        bool alreadySounding = active.Values.Any(v => v.Pitch == item.Pitch && v.Channel == item.Channel);
        active[item.Token] = new(item.NoteId, item.Track, item.Pitch, item.Channel, item.Tick, item.Tick + item.Duration);
        if (!alreadySounding) send((byte)(0x90 | item.Channel), item.Pitch, item.Velocity);
        Enqueue(item with { Tick = item.Tick + item.Duration, Off = true, Velocity = 0 });
    }
    private void Release(Guid token)
    {
        if (!active.TryGetValue(token, out var voice)) return;
        if (!active.Any(v => v.Key != token && v.Value.Pitch == voice.Pitch && v.Value.Channel == voice.Channel))
            send((byte)(0x80 | voice.Channel), voice.Pitch, 0);
        active.Remove(token);
    }
    private void ClearActive()
    {
        Exception? error = null;
        foreach (Guid token in active.Keys.ToArray()) try { Release(token); } catch (Exception ex) { error ??= ex; }
        if (error is not null) throw error;
    }
    public double Process(double time)
    {
        lock (gate)
        {
            if (!playing || disposed) return double.PositiveInfinity;
            double at = Position(time);
            // Skip ancient cycles after a suspended process instead of emitting a burst of stale music.
            nextCycle = Math.Max(nextCycle, (long)Math.Floor(at / pattern.Length) - 1);
            while (nextCycle * pattern.Length <= at + pattern.Length) AppendCycle(nextCycle++, at - SequenceTiming.Ppq / 4.0);
            long beat = SequenceTiming.Ppq * 4L / pattern.Denominator;
            if (nextClick < at - beat) nextClick = (long)Math.Ceiling(at / beat) * beat;
            while (nextClick <= at)
            {
                if (metronome) Enqueue(new(nextClick, Guid.Empty, Guid.NewGuid(), -1, (byte)metroNote,
                    (byte)(SequenceTiming.Wrap(nextClick, pattern.BarTicks) == 0 ? Math.Min(127, metroVelocity + 15) : metroVelocity), SequenceTiming.Ppq / 32, metroChannel - 1, false));
                nextClick += beat;
            }
            while (queue.TryPeek(out _, out var priority) && priority.Tick <= at)
            {
                var item = queue.Dequeue();
                if (item.Off) Release(item.Token);
                else if (item.Tick >= at - SequenceTiming.Ppq / 4.0) StartVoice(item);
            }
            double next = queue.TryPeek(out _, out var due) ? due.Tick : double.PositiveInfinity;
            return Math.Min(next, Math.Min(nextClick, nextCycle * pattern.Length));
        }
    }
    private void Run()
    {
        while (true)
        {
            lock (gate) if (disposed) return;
            try
            {
                double next = Process(now());
                if (double.IsPositiveInfinity(next)) { wake.WaitOne(); continue; }
                while (true)
                {
                    double remaining;
                    lock (gate) { if (disposed) return; remaining = SequenceTiming.TicksToSeconds(next - Position(now()), pattern.Bpm); }
                    if (remaining <= 0 || wake.WaitOne(0)) break;
                    if (remaining > .002) { if (wake.WaitOne(Math.Max(1, (int)(remaining * 1000) - 1))) break; }
                    else Thread.SpinWait(64);
                }
            }
            catch (Exception ex)
            {
                try { Stop(); } catch (Exception) { lock (gate) playing = false; }
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
        worker?.Join(); wake.Dispose(); if (error is not null) throw error;
    }
}
