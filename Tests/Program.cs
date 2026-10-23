using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using Vortice.XInput;
using XMidi;

internal static class Checks
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Equal(0, ControllerReader.ThumbPercent(500, Gamepad.LeftThumbDeadZone), "Zona morta");
            Equal(-100, ControllerReader.ThumbPercent(short.MinValue, Gamepad.LeftThumbDeadZone), "Extremo negativo");
            Equal(100, ControllerReader.ThumbPercent(short.MaxValue, Gamepad.LeftThumbDeadZone), "Extremo positivo");
            Equal((byte?)36, BoomBapPreset.NoteFor(GamepadButtons.A), "Kick");
            Equal((byte?)38, BoomBapPreset.NoteFor(GamepadButtons.B), "Snare");
            Equal((byte?)42, BoomBapPreset.NoteFor(GamepadButtons.X), "Closed hat");
            Equal((byte?)46, BoomBapPreset.NoteFor(GamepadButtons.Y), "Open hat");
            Equal((byte?)null, BoomBapPreset.NoteFor(GamepadButtons.Start), "Botão sem mapeamento");
            Console.WriteLine("PASS: normalização, zona morta e preset fixo.");
            CheckStateAndPersistence();

            var port = MidiOutput.GetPorts().FirstOrDefault(p => p.Name == "X-MIDI");
            if (port is null)
            {
                Console.WriteLine("SKIP: loopback e WPF requerem uma porta loopMIDI chamada X-MIDI.");
                return 2;
            }

            using var input = new LoopbackInput("X-MIDI");
            using (var output = new MidiOutput())
            {
                output.Open(port.Id);
                var preset = new BoomBapPreset(output);
                foreach (var button in new[] { GamepadButtons.A, GamepadButtons.B, GamepadButtons.X, GamepadButtons.Y })
                {
                    preset.Button(button, true);
                    preset.Button(button, true); // Segurar não deve repetir Note On.
                    preset.Button(button, false);
                }
                preset.Analog(new Gamepad { LeftThumbX = short.MinValue, RightThumbX = short.MaxValue });
                preset.Analog(new Gamepad()); // Limitado a 35 ms; deve enviar o valor final depois.
                Thread.Sleep(45);
                preset.Analog(new Gamepad());
                preset.Button(GamepadButtons.A, true);
                preset.Silence();
                input.WaitForCount(15);
                var messages = input.Messages.ToArray();
                uint[] expected = [Pack(0x90,36,110), Pack(0x80,36,0), Pack(0x90,38,110), Pack(0x80,38,0),
                    Pack(0x90,42,110), Pack(0x80,42,0), Pack(0x90,46,110), Pack(0x80,46,0),
                    Pack(0xB0,74,0), Pack(0xB0,91,127), Pack(0xB0,74,64), Pack(0xB0,91,64),
                    Pack(0x90,36,110), Pack(0x80,36,0), Pack(0xB0,123,0)];
                if (!messages.SequenceEqual(expected))
                    throw new Exception($"Loopback inesperado: {string.Join(", ", messages.Select(m => m.ToString("X6")))}");
                Console.WriteLine("PASS: 15 mensagens reais recebidas pelo loopMIDI: notas, liberações, CC, limite de frequência e All Notes Off.");
                CheckBanksAndCommands(output, input);
            }

            string uiSettings = Path.Combine(Path.GetTempPath(), $"xmidi-ui-{Guid.NewGuid():N}.json");
            var window = new MainWindow(new SettingsStore(uiSettings));
            try
            {
                var sample = typeof(MainWindow).GetMethod("Sampled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                sample.Invoke(window, [0u, true, new Gamepad { Buttons = GamepadButtons.A | GamepadButtons.X, LeftTrigger = 255, RightThumbX = short.MaxValue }]);
                Equal(100.0, ((ProgressBar)window.FindName("LTBar")).Value, "Indicador LT");
                var label = (TextBlock)window.FindName("RightValues");
                if (!label.Text.Contains("X 100%")) throw new Exception("Analógico não atualizou a interface.");
                var monitor = (ListBox)window.FindName("Monitor");
                if (!monitor.Items.Cast<string>().Any(line => line.Contains("[90 24 6E]"))) throw new Exception("Note On A não aparece no monitor.");
                sample.Invoke(window, [0u, true, new Gamepad { Buttons = GamepadButtons.A | GamepadButtons.X | GamepadButtons.DPadDown }]);
                Equal("↓ Banco 2", ((TextBlock)window.FindName("BankLabel")).Text, "Indicador de banco");
                if (!((TextBlock)window.FindName("MapA")).Text.Contains("SideStick")) throw new Exception("Instrumento do banco 2 não aparece.");
                sample.Invoke(window, [0u, false, default(Gamepad)]);
                Equal(0.0, ((ProgressBar)window.FindName("LTBar")).Value, "Desconexão limpa LT");
                if (!monitor.Items.Cast<string>().Any(line => line.Contains("[80 24 00]"))) throw new Exception("Desconexão não liberou kick.");
                var editor = new SettingsWindow(new AppSettings(), window.Resources);
                editor.Measure(new System.Windows.Size(800, 590));
                editor.Arrange(new System.Windows.Rect(0, 0, 800, 590));
                editor.UpdateLayout();
                editor.Close();
                Console.WriteLine("PASS: WPF atualiza banco, instrumentos, gatilho/analógico/monitor, abre editor e libera notas (amostras simuladas).");
            }
            finally { window.Close(); File.Delete(uiSettings); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void CheckStateAndPersistence()
    {
        using var output = new MidiOutput();
        var preset = new BoomBapPreset(output);
        Equal(0, preset.ActiveBank, "Sempre inicia no banco 1");
        Equal(0, preset.Settings.SwingPercent, "Swing inicial");
        preset.Button(GamepadButtons.DPadDown, true);
        preset.Button(GamepadButtons.DPadDown, true);
        Equal(1, preset.ActiveBank, "Segurar ↓ não alterna repetidamente");
        preset.Button(GamepadButtons.DPadDown, false);
        preset.Button(GamepadButtons.DPadDown, true);
        Equal(0, preset.ActiveBank, "Segundo toque retorna ao banco 1");
        preset.Button(GamepadButtons.DPadRight, true);
        preset.Button(GamepadButtons.DPadRight, true);
        Equal(5, preset.Settings.SwingPercent, "Passo padrão de 5%, sem repetição ao segurar");
        preset.SetSwing(-30); Equal(0, preset.Settings.SwingPercent, "Limite inferior");
        preset.SetSwing(200); Equal(100, preset.Settings.SwingPercent, "Limite superior");
        preset.Button(GamepadButtons.DPadLeft, true);
        Equal(95, preset.Settings.SwingPercent, "Reduz swing em 5%");

        string path = Path.Combine(Path.GetTempPath(), $"xmidi-settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(path);
        try
        {
            var original = new AppSettings { SwingPercent = 35, SwingStep = 10, PreferredPort = "Porta de teste" };
            for (int bank = 0; bank < 2; bank++)
                for (int pad = 0; pad < 4; pad++) original.Banks[bank][pad] = new() { Name = $"Instrumento {bank}/{pad}", Note = bank * 4 + pad };
            store.SaveAsync(original).GetAwaiter().GetResult();
            var restored = store.Load(out string? warning);
            Equal<string?>(null, warning, "Persistência válida");
            Equal(35, restored.SwingPercent, "Swing persistido");
            Equal(10, restored.SwingStep, "Incremento persistido");
            Equal("Porta de teste", restored.PreferredPort, "Porta persistida");
            for (int bank = 0; bank < 2; bank++)
                for (int pad = 0; pad < 4; pad++)
                {
                    Equal(original.Banks[bank][pad].Note, restored.Banks[bank][pad].Note, "Nota persistida");
                    Equal(original.Banks[bank][pad].Name, restored.Banks[bank][pad].Name, "Nome persistido");
                }
            Equal(0, new BoomBapPreset(output, restored).ActiveBank, "Banco ativo não é restaurado");
            File.WriteAllText(path, "{\"SwingPercent\":25}");
            restored = store.Load(out warning);
            Equal(25, restored.SwingPercent, "Compatibilidade com campos ausentes");
            Equal(36, restored.Banks[0][0].Note, "Classic preservado nos campos ausentes");
            File.WriteAllText(path, "{\"Banks\":null}");
            restored = store.Load(out warning);
            if (warning is null) throw new Exception("Configuração inválida não foi sinalizada.");
            Equal(36, restored.Banks[0][0].Note, "Fallback seguro");
            Equal("{\"Banks\":null}", File.ReadAllText(path), "Load não sobrescreve arquivo inválido");
            File.WriteAllText(path, "json inválido");
            store.Load(out warning);
            if (warning is null) throw new Exception("JSON corrompido não foi sinalizado.");
            original.Banks[0][0].Note = 128;
            try { original.Validate(); throw new Exception("Nota inválida foi aceita."); }
            catch (InvalidDataException) { }
        }
        finally { File.Delete(path); }
        Console.WriteLine("PASS: bancos, limites/passos de swing, oito mapeamentos persistidos, compatibilidade e arquivos inválidos.");
    }

    private static void CheckBanksAndCommands(MidiOutput output, LoopbackInput input)
    {
        input.Messages.Clear();
        var settings = new AppSettings();
        for (int pad = 0; pad < 4; pad++) settings.Banks[1][pad].Note = 60 + pad;
        var preset = new BoomBapPreset(output, settings);
        preset.Button(GamepadButtons.A, true);
        preset.Button(GamepadButtons.DPadDown, true);
        preset.Button(GamepadButtons.DPadDown, true);
        preset.Button(GamepadButtons.DPadDown, false);
        preset.Button(GamepadButtons.A, false); // Não pode liberar outra nota no banco novo.
        foreach (var button in new[] { GamepadButtons.A, GamepadButtons.B, GamepadButtons.X, GamepadButtons.Y })
        { preset.Button(button, true); preset.Button(button, false); }
        preset.Button(GamepadButtons.DPadRight, true);
        preset.Button(GamepadButtons.DPadRight, true);
        preset.Button(GamepadButtons.DPadRight, false);
        preset.Button(GamepadButtons.DPadLeft, true);
        preset.Button(GamepadButtons.DPadLeft, false);
        preset.Button(GamepadButtons.DPadUp, true);
        preset.Button(GamepadButtons.DPadUp, true);
        preset.Button(GamepadButtons.DPadUp, false);
        preset.Button(GamepadButtons.DPadUp, true);
        preset.Button(GamepadButtons.DPadUp, false);
        uint[] expected = [Pack(0x90,36,110), Pack(0x80,36,0),
            Pack(0x90,60,110), Pack(0x80,60,0), Pack(0x90,61,110), Pack(0x80,61,0),
            Pack(0x90,62,110), Pack(0x80,62,0), Pack(0x90,63,110), Pack(0x80,63,0),
            Pack(0xBF,103,6), Pack(0xBF,103,0), Pack(0xBF,102,127), Pack(0xBF,102,127)];
        input.WaitForCount(expected.Length);
        if (!input.Messages.ToArray().SequenceEqual(expected)) throw new Exception("Troca de bancos/comandos gerou MIDI inesperado.");

        input.Messages.Clear();
        settings.Banks[1][0].Note = settings.Banks[1][1].Note = 70;
        preset.Button(GamepadButtons.A, true);
        preset.Button(GamepadButtons.B, true);
        preset.Button(GamepadButtons.A, false);
        preset.Button(GamepadButtons.B, false);
        preset.Button(GamepadButtons.X, true);
        preset.ToggleBank();
        preset.Button(GamepadButtons.X, false);
        expected = [Pack(0x90,70,110), Pack(0x80,70,0), Pack(0x90,62,110), Pack(0x80,62,0)];
        input.WaitForCount(expected.Length);
        if (!input.Messages.ToArray().SequenceEqual(expected)) throw new Exception("Notas duplicadas/troca com nota sustentada falhou.");
        Console.WriteLine("PASS: loopback de quatro notas independentes do banco 2, troca segura, notas duplicadas, comandos de transporte/swing e bordas de pressão.");
    }

    private static uint Pack(byte status, byte data1, byte data2) => (uint)(status | data1 << 8 | data2 << 16);
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: esperado {expected}, recebido {actual}");
    }
}

internal sealed class LoopbackInput : IDisposable
{
    private readonly Callback callback;
    private nint handle;
    public ConcurrentQueue<uint> Messages { get; } = new();
    public LoopbackInput(string name)
    {
        callback = (_, message, _, data, _) => { if (message == 0x3C3) Messages.Enqueue((uint)data & 0xFFFFFF); };
        for (uint id = 0; id < midiInGetNumDevs(); id++)
        {
            Check(midiInGetDevCapsW(id, out var caps, (uint)Marshal.SizeOf<InputCaps>()));
            if (caps.Name != name) continue;
            Check(midiInOpen(out handle, id, callback, 0, 0x30000));
            Check(midiInStart(handle));
            return;
        }
        throw new Exception($"Entrada MIDI {name} não encontrada.");
    }
    public void WaitForCount(int count)
    {
        if (!SpinWait.SpinUntil(() => Messages.Count >= count, 2000)) throw new Exception($"Loopback recebeu {Messages.Count}/{count} mensagens.");
    }
    public void Dispose()
    {
        if (handle == 0) return;
        midiInStop(handle); midiInReset(handle); midiInClose(handle); handle = 0;
        GC.KeepAlive(callback);
    }
    private static void Check(uint code) { if (code != 0) throw new Exception($"MIDI input: erro {code}"); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InputCaps
    {
        public ushort Manufacturer, Product;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Support;
    }
    private delegate void Callback(nint handle, uint message, nuint instance, nuint data, nuint timestamp);
    [DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiInGetDevCapsW(nuint id, out InputCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiInOpen(out nint handle, uint id, Callback callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiInStart(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiInStop(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiInReset(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiInClose(nint handle);
}
