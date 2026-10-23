namespace XMidi;

internal static class ChordGenerator
{
    public static int[] Scale(ScaleKind kind) => kind switch
    {
        ScaleKind.Major => [0, 2, 4, 5, 7, 9, 11],
        ScaleKind.NaturalMinor => [0, 2, 3, 5, 7, 8, 10],
        ScaleKind.HarmonicMinor => [0, 2, 3, 5, 7, 8, 11],
        ScaleKind.MajorPentatonic => [0, 2, 4, 7, 9],
        ScaleKind.MinorPentatonic => [0, 3, 5, 7, 10],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static int[] Intervals(ChordKind kind) => kind switch
    {
        ChordKind.Major => [0, 4, 7], ChordKind.Minor => [0, 3, 7],
        ChordKind.Sus2 => [0, 2, 7], ChordKind.Sus4 => [0, 5, 7],
        ChordKind.DominantSeventh => [0, 4, 7, 10], ChordKind.MajorSeventh => [0, 4, 7, 11],
        ChordKind.MinorSeventh => [0, 3, 7, 10], ChordKind.MinorNinth => [0, 3, 7, 10, 14],
        ChordKind.MajorNinth => [0, 4, 7, 11, 14],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static byte[] Generate(ChordSettings settings, int pad, IReadOnlyList<byte>? previous = null)
    {
        settings.Validate();
        var p = settings.Pads[pad];
        if (p.Notes is { } custom) return custom.Distinct().Order().Select(n => (byte)n).ToArray();
        int[] scale = Scale(settings.Scale);
        int step = p.Degree - 1;
        int root = (settings.Octave + 1 + p.OctaveShift) * 12 + settings.Key + scale[step % scale.Length] + step / scale.Length * 12;
        int[] notes = Intervals(p.Kind).Select(n => root + n).ToArray();
        int inversion = settings.Voicing switch { ChordVoicing.FirstInversion => 1, ChordVoicing.SecondInversion => 2, _ => 0 };
        for (int i = 0; i < inversion; i++) notes[i] += 12;
        if (settings.Voicing == ChordVoicing.Open) for (int i = 1; i < notes.Length; i += 2) notes[i] += 12;
        Array.Sort(notes);
        if (settings.VoiceLeading && previous is { Count: > 0 })
        {
            // Keep the chosen voicing; choose the nearest whole-octave register to the preceding chord.
            double center = previous.Average(n => (double)n);
            int shift = new[] { -24, -12, 0, 12, 24 }.Where(s => notes.All(n => n + s is >= 0 and <= 127))
                .OrderBy(s => Math.Abs(notes.Average() + s - center)).FirstOrDefault();
            notes = notes.Select(n => n + shift).ToArray();
        }
        while (notes.Max() > 127) notes = notes.Select(n => n - 12).ToArray();
        while (notes.Min() < 0) notes = notes.Select(n => n + 12).ToArray();
        return notes.Distinct().Order().Select(n => (byte)n).ToArray();
    }
    public static string NoteName(int note) => $"{new[] { "C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B" }[note % 12]}{note / 12 - 1}";
    public static string Describe(IEnumerable<byte> notes) => string.Join(" · ", notes.Select(n => $"{NoteName(n)} ({n})"));
}

internal static class MusicalPresets
{
    public static readonly string[] Chords = ["Jazzy Boom Bap", "Soulful", "Lo-Fi Chill", "Neo Soul"];
    public static readonly string[] Samples = ["Jazz Chops", "Soul Chops", "Lo-Fi Texture", "Classic Boom Bap"];
    public static ChordSettings Chord(int index)
    {
        var s = new ChordSettings { Preset = Chords[index] };
        s.Key = index switch { 1 => 5, 2 => 9, 3 => 2, _ => 0 };
        s.Scale = index is 1 or 3 ? ScaleKind.Major : ScaleKind.NaturalMinor;
        s.Voicing = index is 1 or 3 ? ChordVoicing.Open : ChordVoicing.Root;
        s.VoiceLeading = index is 1 or 3;
        int[] progression = index switch
        {
            1 => [1, 6, 2, 5, 4, 3, 2, 5], 2 => [1, 6, 3, 7, 4, 1, 5, 1],
            3 => [2, 5, 1, 6, 4, 3, 2, 5], _ => [1, 4, 2, 5, 6, 2, 5, 1]
        };
        s.Pads = ChordSettings.CreatePads(progression, ChordKind.MinorSeventh);
        for (int i = 0; i < 8; i++)
            s.Pads[i].Kind = index switch
            {
                1 => i % 2 == 0 ? ChordKind.MajorSeventh : ChordKind.MinorSeventh,
                2 => i % 2 == 0 ? ChordKind.MinorNinth : ChordKind.MinorSeventh,
                3 => i % 2 == 0 ? ChordKind.MinorNinth : ChordKind.MajorNinth,
                _ => i % 2 == 0 ? ChordKind.MinorSeventh : ChordKind.MinorNinth
            };
        s.Kind = s.Pads[0].Kind;
        return s;
    }
    public static void Sample(SampleSettings s, int index)
    {
        s.Preset = Samples[index]; s.SliceCount = 8;
        s.Method = index is 0 or 1 ? ChopMethod.Transients : ChopMethod.Equal;
        s.Sensitivity = index == 0 ? .65 : .45;
        s.Playback = index == 2 ? SlicePlayback.Gate : SlicePlayback.OneShot;
        s.Reverse = index == 2; s.Transpose = index switch { 1 => 2, 2 => -5, _ => 0 };
        s.Regions = SampleSettings.EqualRegions(8);
        s.Pads = Enumerable.Range(0, 8).Select(i => new SlicePad { Region = i, Note = 60 + i }).ToArray();
        // Explicit preset application resets sample mappings, without touching Drum/Chord banks.
        // Presets never load plugins or audio in FL Studio.
    }
}
