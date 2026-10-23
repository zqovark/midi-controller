using System.IO;
using System.Text.Json;

namespace XMidi;

internal enum MusicalMode { Drum, Sample, Chord }
internal enum ScaleKind { Major, NaturalMinor, HarmonicMinor, MajorPentatonic, MinorPentatonic }
internal enum ChordKind { Major, Minor, Sus2, Sus4, DominantSeventh, MajorSeventh, MinorSeventh, MinorNinth, MajorNinth }
internal enum ChordVoicing { Root, FirstInversion, SecondInversion, Open }
internal enum ChopMethod { Equal, Manual, Transients }
internal enum SlicePlayback { OneShot, Gate }

internal sealed class ChordPad
{
    public string Name { get; set; } = "Acorde";
    public int Degree { get; set; } = 1;
    public int OctaveShift { get; set; }
    public ChordKind Kind { get; set; } = ChordKind.MinorSeventh;
    public int[]? Notes { get; set; }
}
internal sealed class ChordSettings
{
    public string Preset { get; set; } = "Jazzy Boom Bap";
    public int Key { get; set; }
    public ScaleKind Scale { get; set; } = ScaleKind.NaturalMinor;
    public int Octave { get; set; } = 3;
    public ChordKind Kind { get; set; } = ChordKind.MinorSeventh;
    public ChordVoicing Voicing { get; set; }
    public bool VoiceLeading { get; set; }
    public ChordPad[] Pads { get; set; } = CreatePads([1, 4, 2, 5, 6, 2, 5, 1], ChordKind.MinorSeventh);
    public static ChordPad[] CreatePads(int[] degrees, ChordKind kind) => degrees.Select((d, i) =>
        new ChordPad { Name = $"Acorde {i + 1}", Degree = d, Kind = kind }).ToArray();
    public ChordSettings Copy() => JsonSerializer.Deserialize<ChordSettings>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        if (Key is < 0 or > 11 || Octave is < 1 or > 6 || !Enum.IsDefined(Scale) || !Enum.IsDefined(Kind)
            || !Enum.IsDefined(Voicing) || Pads is not { Length: 8 } || Pads.Any(p => p is null
                || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 40 || p.Degree is < 1 or > 7
                || p.OctaveShift is < -2 or > 2 || !Enum.IsDefined(p.Kind)
                || p.Notes is { } n && (n.Length is < 1 or > 10 || n.Any(note => note is < 0 or > 127))))
            throw new InvalidDataException("Acordes: oito pads, graus 1–7, oitavas 1–6 e notas MIDI 0–127.");
    }
}
internal sealed class SliceRegion
{
    public double Start { get; set; }
    public double End { get; set; } = 1;
}
internal sealed class SlicePad
{
    public int Region { get; set; }
    public int Note { get; set; } = 60;
}
internal sealed class EffectMapping
{
    public string Name { get; set; } = "Low-pass";
    public bool Enabled { get; set; }
    public int Cc { get; set; } = 74;
    public int Stick { get; set; } // 0 = left X, 1 = right X
}
internal sealed class SampleSettings
{
    public string Preset { get; set; } = "Classic Boom Bap";
    public string? FilePath { get; set; }
    public int SourceFrames { get; set; }
    public int SourceRate { get; set; }
    public string? SourceHash { get; set; }
    public ChopMethod Method { get; set; }
    public int SliceCount { get; set; } = 8;
    public double Sensitivity { get; set; } = .5;
    public SlicePlayback Playback { get; set; } = SlicePlayback.OneShot;
    public bool Reverse { get; set; }
    public int Transpose { get; set; }
    public SliceRegion[] Regions { get; set; } = EqualRegions(8);
    public SlicePad[] Pads { get; set; } = Enumerable.Range(0, 8).Select(i => new SlicePad { Region = i, Note = 60 + i }).ToArray();
    public EffectMapping[] Effects { get; set; } =
    [
        new() { Name = "Low-pass", Enabled = true, Cc = 74, Stick = 0 },
        new() { Name = "High-pass", Cc = 71, Stick = 0 },
        new() { Name = "Reverb", Enabled = true, Cc = 91, Stick = 1 },
        new() { Name = "Delay", Cc = 94, Stick = 1 }
    ];
    public static SliceRegion[] EqualRegions(int count) => Enumerable.Range(0, count)
        .Select(i => new SliceRegion { Start = i / (double)count, End = (i + 1.0) / count }).ToArray();
    public double Duration(int region) => SourceRate > 0 && region >= 0 && region < Regions.Length
        ? (Regions[region].End - Regions[region].Start) * SourceFrames / SourceRate / Math.Pow(2, Transpose / 12.0) : 0;
    public SampleSettings Copy() => JsonSerializer.Deserialize<SampleSettings>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        if (!Enum.IsDefined(Method) || !Enum.IsDefined(Playback) || SliceCount is not (2 or 4 or 8)
            || !double.IsFinite(Sensitivity) || Sensitivity is < 0 or > 1 || Transpose is < -24 or > 24
            || SourceFrames < 0 || SourceRate < 0 || (SourceFrames > 0 && (SourceRate is < 8000 or > 384000))
            || Regions is not { Length: >= 1 and <= 8 } || Regions.Any(r => r is null || !double.IsFinite(r.Start)
                || !double.IsFinite(r.End) || r.Start < 0 || r.End > 1 || r.Start >= r.End
                || SourceFrames > 0 && Math.Round(r.End * SourceFrames) <= Math.Round(r.Start * SourceFrames))
            || Pads is not { Length: 8 } || Pads.Any(p => p is null || p.Region < -1 || p.Region >= Regions.Length || p.Note is < 0 or > 127)
            || Effects is not { Length: 4 } || Effects.Any(e => e is null || e.Cc is < 1 or > 119
                || e.Stick is < 0 or > 1 || string.IsNullOrWhiteSpace(e.Name)))
            throw new InvalidDataException("Chopping inválido: regiões dentro do sample, oito pads, pitch ±24 e CC 1–119.");
    }
}
