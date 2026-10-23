using System.Text.Json;
using Vortice.XInput;
using XMidi;

var tests = new (string Name, Action Check)[]
{
    ("LT: extremos, limites e monotonicidade das três curvas", () =>
    {
        foreach (var curve in Enum.GetValues<VelocityCurve>())
        {
            Check(DrumTiming.Velocity(0, 110, 30, curve) == 110, "LT solto");
            Check(DrumTiming.Velocity(255, 110, 30, curve) == 30, "LT pressionado");
            int previous = 127;
            for (int lt = 0; lt <= 255; lt++)
            {
                int value = DrumTiming.Velocity((byte)lt, 127, 1, curve);
                Check(value >= 1 && value <= previous, "Velocity MIDI progressiva"); previous = value;
            }
        }
        Check(DrumTiming.Velocity(128, 110, 30, VelocityCurve.Soft) > DrumTiming.Velocity(128, 110, 30, VelocityCurve.Linear), "Curva suave");
        Check(DrumTiming.Velocity(128, 110, 30, VelocityCurve.Accented) < DrumTiming.Velocity(128, 110, 30, VelocityCurve.Linear), "Curva acentuada");
        Check(DrumTiming.Velocity(200, 1, 1, VelocityCurve.Linear) == 1, "Nunca Note On zero");
    }),
    ("Intervalos binários, tercinas e mudança de BPM", () =>
    {
        double[] expected = [.5, .25, .125, .0625, 1.0 / 6, 1.0 / 12, 1.0 / 24];
        foreach (var d in Enum.GetValues<RepeatDivision>())
        {
            Near(DrumTiming.Interval(120, d), expected[(int)d]);
            Near(DrumTiming.Interval(60, d), expected[(int)d] * 2);
        }
    }),
    ("Note Repeat: início, deadline, deduplicação e nova velocity", () =>
    {
        using var f = new Fixture();
        f.Engine.Shoulder(true); f.Engine.Pad(0, 36, true); f.Engine.Pad(0, 36, true);
        Check(f.Messages.Count == 1, "Um Note On inicial");
        f.Engine.Trigger(255);
        Check(f.Messages.Count == 1 && f.Messages[0].V == 110, "LT não altera nota ativa");
        f.Engine.Tick(.124); Check(f.Messages.Count == 1, "Não antecipar");
        f.Engine.Tick(.125); f.Engine.Tick(.125);
        Check(f.Messages.SequenceEqual(new (byte, byte, byte)[] { (0x90,36,110), (0x80,36,0), (0x90,36,30) }), "Par Off/On e velocity atual");
        f.Engine.Pad(0, 36, false); int count = f.Messages.Count;
        f.Engine.Tick(10); Check(f.Messages.Count == count && f.Messages[^1].S == 0x80, "Parada imediata do pad");
    }),
    ("Soltar RB encerra notas; pressionar novamente retoma pads segurados", () =>
    {
        using var f = new Fixture();
        f.Engine.Pad(0, 36, true); f.Engine.Shoulder(true);
        f.Engine.Tick(.125); f.Engine.Shoulder(false);
        Check(!f.Engine.IsRepeating && f.Messages[^1].S == 0x80, "Soltar RB encerra");
        int count = f.Messages.Count; f.Engine.Tick(1);
        Check(f.Messages.Count == count, "Nenhuma repetição futura");
        f.Engine.Shoulder(true); Check(f.Engine.IsRepeating && f.Messages[^1].S == 0x90, "Retoma");
    }),
    ("Instrumentos simultâneos e notas mapeadas duplicadas", () =>
    {
        using var f = new Fixture(); f.Engine.Shoulder(true);
        f.Engine.Pad(0, 36, true); f.Engine.Pad(1, 38, true); f.Engine.Pad(2, 36, true);
        Check(f.Messages.Count == 2, "Sem Note On duplicado");
        f.Engine.Tick(.125); Check(f.Messages.Count == 6, "Duas vozes repetidas");
        f.Engine.Pad(0, 36, false); Check(f.Messages.Count == 6, "Nota compartilhada preservada");
        f.Engine.Pad(2, 36, false); Check(f.Messages[^1] == (0x80,36,0), "Último dono encerra");
        f.Engine.Stop(); Check(f.Messages[^1] == (0x80,38,0), "Segunda voz encerrada");
    }),
    ("Desativação, mudança de divisão e ausência de rajada após atraso", () =>
    {
        using var f = new Fixture(); f.Engine.Shoulder(true); f.Engine.Pad(0, 36, true);
        f.Engine.Tick(5); Check(f.Messages.Count == 3, "Um único retrigger após atraso");
        Check(f.Engine.Tick(5) > 5 && f.Messages.Count == 3, "Deadline avança");
        var settings = new AppSettings { RepeatBpm = 60, RepeatDivision = RepeatDivision.EighthTriplet };
        f.Time = 5; f.Engine.Configure(settings); Near(f.Engine.Tick(5), 5 + 1.0 / 3);
        settings.NoteRepeatEnabled = false; f.Engine.Configure(settings);
        Check(!f.Engine.IsRepeating && f.Messages[^1].S == 0x80, "Desativação libera");
    }),
    ("Banco, mapeamento, desconexão e silêncio no preset real", () =>
    {
        var output = new MidiOutput(); using var preset = new BoomBapPreset(output, background: false);
        preset.SetTrigger(255); preset.Button(GamepadButtons.RightShoulder, true); preset.Button(GamepadButtons.A, true);
        Check(preset.IsRepeating && output.Messages[^1] == (0x90,36,30), "RB e LT");
        preset.ToggleBank(); Check(!preset.IsRepeating && output.Messages[^1] == (0x80,36,0), "Troca encerra");
        preset.Button(GamepadButtons.A, false); preset.Button(GamepadButtons.A, true);
        Check(preset.IsRepeating && output.Messages[^1].N == 37, "RB segurado funciona no banco 2");
        preset.Silence(); Check(!preset.IsRepeating && output.Messages.Contains((0x80,37,0)), "Desconexão encerra vozes");
        Check(output.Messages[^1] == (0xB0,123,0), "All Notes Off");
        preset.Button(GamepadButtons.A, true); preset.Reset();
        Check(output.Messages[^1] == (0x80,37,0), "Reset encerra notas");
    }),
    ("Dispose encerra todas as notas e impede novos disparos", () =>
    {
        var f = new Fixture(); f.Engine.Shoulder(true); f.Engine.Pad(0, 36, true); f.Engine.Pad(1, 38, true);
        f.Dispose(); int count = f.Messages.Count; f.Engine.Tick(10); f.Engine.Pad(2, 42, true);
        Check(f.Messages.Count == count && f.Messages.TakeLast(2).All(m => m.S == 0x80), "Fechamento sem notas presas");
        f.Dispose();
    }),
    ("Configurações antigas, cópia, persistência e rejeição de valores inválidos", () =>
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"SwingPercent\":25}")!;
        settings.Validate(); Check(settings.DefaultVelocity == 110 && settings.RepeatBpm == 120 && settings.SwingPercent == 25, "Defaults compatíveis");
        settings.DefaultVelocity = 100; settings.MinimumVelocity = 12; settings.VelocityCurve = VelocityCurve.Accented;
        settings.RepeatDivision = RepeatDivision.ThirtySecondTriplet; settings.RepeatBpm = 137; settings.NoteRepeatEnabled = false;
        var copy = settings.Copy(); var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(copy))!;
        Check(restored.DefaultVelocity == 100 && restored.MinimumVelocity == 12 && restored.VelocityCurve == VelocityCurve.Accented
            && restored.RepeatDivision == RepeatDivision.ThirtySecondTriplet && restored.RepeatBpm == 137 && !restored.NoteRepeatEnabled, "Round trip");
        foreach (Action<AppSettings> corrupt in new Action<AppSettings>[] { s => s.MinimumVelocity = 128, s => s.DefaultVelocity = 0,
            s => s.RepeatBpm = double.NaN, s => s.RepeatBpm = 0, s => s.VelocityCurve = (VelocityCurve)99, s => s.RepeatDivision = (RepeatDivision)99 })
        {
            var invalid = settings.Copy(); corrupt(invalid);
            try { invalid.Validate(); throw new Exception("Configuração inválida aceita"); } catch (System.IO.InvalidDataException) { }
        }
        string path = Path.Combine(Path.GetTempPath(), $"xmidi-drum-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path); store.SaveAsync(settings).GetAwaiter().GetResult();
            Check(store.Load(out var warning).RepeatBpm == 137 && warning is null, "Persistência real");
            File.WriteAllText(path, "{\"MinimumVelocity\":128}"); store.Load(out warning);
            Check(warning is not null && File.ReadAllText(path).Contains("128"), "Arquivo inválido não sobrescrito");
        }
        finally { File.Delete(path); }
    }),
    ("Thread de agendamento inicia, envia e encerra sem callbacks posteriores", () =>
    {
        using var firstHit = new ManualResetEventSlim(); using var repeated = new ManualResetEventSlim();
        int hits = 0, offs = 0;
        var engine = new DrumEngine((s, n, v) =>
        {
            if (s == 0x90) { if (Interlocked.Increment(ref hits) == 1) firstHit.Set(); else repeated.Set(); }
            else Interlocked.Increment(ref offs);
        }, new ManualTempoSource());
        engine.Configure(new() { RepeatBpm = 300, RepeatDivision = RepeatDivision.ThirtySecond });
        try
        {
            engine.Shoulder(true); engine.Pad(0, 36, true);
            Check(firstHit.IsSet && repeated.Wait(2000), "Worker entrega hits");
        }
        finally { engine.Dispose(); }
        Check(hits >= 2 && offs == hits, "Todos os Note On encerrados ao fazer Join");
    })
};
int failures = 0;
foreach (var (name, check) in tests)
{
    try { check(); Console.WriteLine($"PASS: {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL: {name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passaram.");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Near(double actual, double expected) => Check(Math.Abs(actual - expected) < 1e-9, $"{actual} != {expected}");

sealed class Fixture : IDisposable
{
    public double Time;
    public readonly List<(byte S, byte N, byte V)> Messages = [];
    public DrumEngine Engine { get; }
    public Fixture() => Engine = new((s,n,v) => Messages.Add((s,n,v)), new ManualTempoSource(), false, () => Time);
    public void Dispose() => Engine.Dispose();
}

// Only the OS MIDI boundary is replaced; engine, preset and controller math are production code.
namespace XMidi
{
    internal sealed class MidiOutput
    {
        public bool IsOpen => true;
        public readonly List<(byte S, byte N, byte V)> Messages = [];
        public void Send(byte status, byte note, byte velocity) => Messages.Add((status, note, velocity));
    }
}
