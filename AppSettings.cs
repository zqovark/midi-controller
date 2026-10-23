using System.IO;
using System.Text.Json;

namespace XMidi;

internal sealed class PadMapping
{
    public string Name { get; set; } = "Percussão";
    public int Note { get; set; }
    public PadMapping Copy() => new() { Name = Name, Note = Note };
}

internal sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public PadMapping[][] Banks { get; set; } =
    [
        [new() { Name = "Kick", Note = 36 }, new() { Name = "Snare", Note = 38 },
         new() { Name = "Closed hi-hat", Note = 42 }, new() { Name = "Open hi-hat", Note = 46 }],
        [new() { Name = "SideStick", Note = 37 }, new() { Name = "Tambourine", Note = 54 },
         new() { Name = "Lo Tom", Note = 45 }, new() { Name = "Crash", Note = 49 }]
    ];
    public int SwingPercent { get; set; }
    public int SwingStep { get; set; } = 5;
    public int DefaultVelocity { get; set; } = 110;
    public int MinimumVelocity { get; set; } = 30;
    public VelocityCurve VelocityCurve { get; set; } = VelocityCurve.Linear;
    public bool NoteRepeatEnabled { get; set; } = true;
    public RepeatDivision RepeatDivision { get; set; } = RepeatDivision.Sixteenth;
    public double RepeatBpm { get; set; } = 120;
    public MusicalMode Mode { get; set; }
    public ChordSettings Chords { get; set; } = MusicalPresets.Chord(0);
    public SampleSettings Samples { get; set; } = new();
    public string? PreferredPort { get; set; }

    public AppSettings Copy() => new()
    {
        Mode = Mode, Chords = Chords.Copy(), Samples = Samples.Copy(),
        Version = Version, Banks = Banks.Select(bank => bank.Select(pad => pad.Copy()).ToArray()).ToArray(),
        SwingPercent = SwingPercent, SwingStep = SwingStep, PreferredPort = PreferredPort,
        DefaultVelocity = DefaultVelocity, MinimumVelocity = MinimumVelocity, VelocityCurve = VelocityCurve,
        NoteRepeatEnabled = NoteRepeatEnabled, RepeatDivision = RepeatDivision, RepeatBpm = RepeatBpm
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || Chords is null || Samples is null)
            throw new InvalidDataException("Modo ou configuração musical inválidos.");
        Chords.Validate(); Samples.Validate();
        if (DefaultVelocity is < 1 or > 127 || MinimumVelocity < 1 || MinimumVelocity > DefaultVelocity
            || !Enum.IsDefined(VelocityCurve) || !Enum.IsDefined(RepeatDivision)
            || !double.IsFinite(RepeatBpm) || RepeatBpm is < 20 or > 300)
            throw new InvalidDataException("Velocity: 1–127, mínima ≤ padrão; BPM: 20–300; curva e divisão válidas.");
        if (Version != 1 || SwingPercent is < 0 or > 100 || SwingStep is < 1 or > 100
            || Banks is not { Length: 2 } || Banks.Any(bank => bank is not { Length: 4 }
                || bank.Any(pad => pad is null || string.IsNullOrWhiteSpace(pad.Name)
                    || pad.Name.Length > 40 || pad.Note is < 0 or > 127)))
            throw new InvalidDataException("Configurações inválidas: use dois bancos com quatro nomes/notas (0–127) e swing de 0–100%.");
    }
}

internal sealed class SettingsStore(string? path = null)
{
    public string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X-MIDI", "settings.json");
    private static readonly JsonSerializerOptions json = new() { WriteIndented = true };
    private readonly SemaphoreSlim saveLock = new(1, 1);

    public AppSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(FilePath)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                ?? throw new InvalidDataException("Arquivo de configurações vazio.");
            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            warning = $"Não foi possível carregar as configurações: {ex.Message}";
            return new();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        settings.Validate();
        string contents = JsonSerializer.Serialize(settings, json);
        await saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            string temporary = FilePath + ".tmp";
            await File.WriteAllTextAsync(temporary, contents).ConfigureAwait(false);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { saveLock.Release(); }
    }
}
