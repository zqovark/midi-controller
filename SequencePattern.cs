using System.IO;
using System.Text.Json;

namespace XMidi;

internal enum SequenceQuantize { Off, Quarter, Eighth, Sixteenth, ThirtySecond, EighthTriplet, SixteenthTriplet }
internal sealed class SequenceNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long OriginalTick { get; set; }
    public long OffsetTicks { get; set; }
    public long Duration { get; set; } = SequenceTiming.Ppq / 8;
    public int Velocity { get; set; } = 110;
    public SequenceNote Copy() => (SequenceNote)MemberwiseClone();
}
internal sealed class SequenceTrack
{
    // Index in the existing two Drum Mode banks, not a separate MIDI mapping.
    public int Instrument { get; set; }
    public List<SequenceNote> Events { get; set; } = [];
    public SequenceTrack Copy() => new() { Instrument = Instrument, Events = Events.Select(n => n.Copy()).ToList() };
}
internal sealed class SequencePattern
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Padrão 1";
    public double Bpm { get; set; } = 120;
    public int Ppq { get; set; } = SequenceTiming.Ppq;
    public int Numerator { get; set; } = 4;
    public int Denominator { get; set; } = 4;
    public int Bars { get; set; } = 1;
    public int StepsPerBar { get; set; } = 16;
    public SequenceQuantize Quantize { get; set; }
    public int QuantizeStrength { get; set; } = 100;
    public List<SequenceTrack> Tracks { get; set; } = Enumerable.Range(0, 8).Select(i => new SequenceTrack { Instrument = i }).ToList();
    public long BarTicks => Ppq * 4L * Numerator / Denominator;
    public long Length => BarTicks * Bars;
    public SequencePattern Copy() => new()
    {
        Id = Id, Name = Name, Bpm = Bpm, Ppq = Ppq, Numerator = Numerator, Denominator = Denominator,
        Bars = Bars, StepsPerBar = StepsPerBar, Quantize = Quantize, QuantizeStrength = QuantizeStrength,
        Tracks = Tracks.Select(t => t.Copy()).ToList()
    };
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || !double.IsFinite(Bpm) || Bpm is < 20 or > 300
            || Ppq != SequenceTiming.Ppq || Numerator is < 1 or > 12 || Denominator is not (2 or 4 or 8 or 16)
            || Bars is < 1 or > 16 || StepsPerBar is not (16 or 32 or 64) || !Enum.IsDefined(Quantize) || QuantizeStrength is < 0 or > 100
            || Tracks is not { Count: 8 } || Tracks.Any(t => t is null || t.Events is null)
            || !Tracks.Select(t => t.Instrument).Order().SequenceEqual(Enumerable.Range(0, 8)))
            throw new InvalidDataException("Padrão inválido: oito instrumentos, PPQ 960, BPM 20–300, 1–16 compassos e 16/32/64 passos por compasso.");
        var notes = Tracks.SelectMany(t => t.Events).ToArray();
        if (notes.Length > 20000 || notes.Any(n => n is null || n.Id == Guid.Empty || n.OriginalTick < 0 || n.OriginalTick >= Length
            || n.OffsetTicks < -Length || n.OffsetTicks > Length || n.Duration < 1 || n.Duration > Length * 4 || n.Velocity is < 1 or > 127)
            || notes.Select(n => n.Id).Distinct().Count() != notes.Length)
            throw new InvalidDataException("Notas inválidas: posições dentro do loop, velocity 1–127 e duração até quatro loops.");
    }
}
internal static class SequenceTiming
{
    public const int Ppq = 960;
    public static double SecondsToTicks(double seconds, double bpm) => seconds * bpm * Ppq / 60;
    public static double TicksToSeconds(double ticks, double bpm) => ticks * 60 / (bpm * Ppq);
    public static double Wrap(double value, long length) => (value % length + length) % length;
    public static long Division(SequenceQuantize division) => division switch
    {
        SequenceQuantize.Quarter => Ppq, SequenceQuantize.Eighth => Ppq / 2,
        SequenceQuantize.Sixteenth => Ppq / 4, SequenceQuantize.ThirtySecond => Ppq / 8,
        SequenceQuantize.EighthTriplet => Ppq / 3, SequenceQuantize.SixteenthTriplet => Ppq / 6, _ => 0
    };
    public static long QuantizedTick(SequenceNote note, SequencePattern pattern)
    {
        double tick = Wrap(note.OriginalTick + note.OffsetTicks, pattern.Length);
        long division = Division(pattern.Quantize);
        if (division != 0) tick += (Math.Round(tick / division, MidpointRounding.AwayFromZero) * division - tick) * pattern.QuantizeStrength / 100;
        return (long)Wrap(Math.Round(tick), pattern.Length);
    }
    public static long PlayTick(SequenceNote note, SequencePattern pattern, int swing)
    {
        long tick = QuantizedTick(note, pattern);
        // One shared swing control: delay the offbeat sixteenths, independent of event origin.
        if (tick / (Ppq / 4) % 2 == 1) tick += (long)Math.Round(Ppq / 8.0 * Math.Clamp(swing, 0, 100) / 100);
        return (long)Wrap(tick, pattern.Length);
    }
}
internal sealed class SequenceLibrary
{
    public int Version { get; set; } = 1;
    public List<SequencePattern> Patterns { get; set; } = [new()];
    public Guid SelectedId { get; set; }
    public void Validate()
    {
        if (Version != 1 || Patterns is not { Count: >= 1 and <= 128 } || Patterns.Any(p => p is null)
            || Patterns.Select(p => p.Id).Distinct().Count() != Patterns.Count)
            throw new InvalidDataException("Biblioteca de padrões inválida.");
        foreach (var p in Patterns) p.Validate();
        if (!Patterns.Any(p => p.Id == SelectedId)) throw new InvalidDataException("Padrão selecionado ausente.");
    }
}
internal sealed class SequenceStore(string path)
{
    private static readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private readonly SemaphoreSlim saveLock = new(1, 1);
    public SequenceLibrary Load()
    {
        if (!File.Exists(path)) { var library = new SequenceLibrary(); library.SelectedId = library.Patterns[0].Id; return library; }
        var libraryRead = Read<SequenceLibrary>(path); libraryRead.Validate(); return libraryRead;
    }
    public static T Read<T>(string file)
    {
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("JSON limitado a 8 MiB.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(file)) ?? throw new InvalidDataException("JSON vazio.");
    }
    public async Task SaveAsync(SequenceLibrary library)
    {
        library.Validate(); string content = JsonSerializer.Serialize(library, json);
        await saveLock.WaitAsync().ConfigureAwait(false);
        try { await WriteAsync(path, content).ConfigureAwait(false); }
        finally { saveLock.Release(); }
    }
    public static Task SavePatternAsync(string file, SequencePattern pattern)
    { pattern.Validate(); return WriteAsync(file, JsonSerializer.Serialize(pattern, json)); }
    private static async Task WriteAsync(string file, string content)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(content) > 8 * 1024 * 1024)
            throw new InvalidDataException("JSON excede 8 MiB. Exporte os padrões individualmente antes de reduzir a biblioteca.");
        string full = Path.GetFullPath(file); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + $".{Guid.NewGuid():N}.tmp";
        try { await File.WriteAllTextAsync(temp, content).ConfigureAwait(false); File.Move(temp, full, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
