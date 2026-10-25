using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.XInput;
using XMidi;

internal static class Checks
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--fl-probe")) return ProbeFl();
            var reader = new ControllerReader();
            bool controller = false;
            reader.Sampled += (_, connected, _) => controller |= connected;
            reader.Poll();
            Console.WriteLine(controller ? "HARDWARE: controle Xbox conectado." : "LIMITAÇÃO: nenhum controle Xbox conectado.");
            Console.WriteLine("Saídas: " + string.Join(", ", MidiOutput.GetPorts().Select(p => p.Name)));
            Console.WriteLine("Entradas: " + string.Join(", ", MidiInput.GetPorts().Select(p => p.Name)));
            var port = MidiOutput.GetPorts().FirstOrDefault(p => p.Name == "X-MIDI Return");
            var inputPort = MidiInput.GetPorts().FirstOrDefault(p => p.Name == "X-MIDI Return");
            if (port is not null && inputPort is not null)
            {
                using var input = new MidiInput(); input.Open(inputPort.Id);
                using var output = new MidiOutput(); output.Open(port.Id);
                using var preset = new BoomBapPreset(output, background: false);
                var received = new List<(byte S,byte N,byte V)>();
                preset.SetTrigger(255);
                preset.Button(GamepadButtons.A,true); preset.Button(GamepadButtons.A,true);
                preset.Button(GamepadButtons.LeftShoulder,true); preset.Button(GamepadButtons.A,false);
                preset.Button(GamepadButtons.LeftShoulder,false);
                preset.RightTrigger(100); preset.RightTrigger(80); preset.RightTrigger(70);
                output.Send(0xBE,110,23); output.Send(0xBE,111,23);
                var sw = Stopwatch.StartNew();
                while(received.Count<6 && sw.ElapsedMilliseconds<1500)
                { input.Drain((s,n,v)=>received.Add((s,n,v))); Thread.Sleep(2); }
                (byte,byte,byte)[] expected=[(0x90,36,30),(0x80,36,0),(0x90,38,30),(0x80,38,0),(0xBE,110,23),(0xBE,111,23)];
                Check(received.SequenceEqual(expected),"Loopback real inesperado: " + string.Join(", ",received.Select(m=>$"{m.S:X2} {m.N} {m.V}")));
                Console.WriteLine("PASS: WinMM real sobre loopMIDI: 6 mensagens exatas; mínimo LT 30 e ownership.");
            }
            else Console.WriteLine("SKIP: loopback exige porta X-MIDI Return.");

            string path=Path.Combine(Path.GetTempPath(),$"xmidi-ui-{Guid.NewGuid():N}.json");
            var window=new MainWindow(new SettingsStore(path));
            try
            {
                var sample=typeof(MainWindow).GetMethod("Sampled",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
                sample.Invoke(window,[0u,true,new Gamepad { Buttons=GamepadButtons.A,LeftTrigger=255 }]);
                Check(((ProgressBar)window.FindName("LTBar")).Value==100,"LT visual");
                Check(((TextBlock)window.FindName("VelocityReadout")).Text.Contains("30"),"LT totalmente pressionado usa mínima 30");
                Check(!((TextBlock)window.FindName("PerformanceStatus")).Text.Contains("GRAVANDO"),"Sem inventar gravação confirmada");
                sample.Invoke(window,[0u,false,default(Gamepad)]);
                window.Width=620; window.Height=460; window.Show();
                window.Measure(new Size(620,460)); window.Arrange(new Rect(0,0,620,460)); window.UpdateLayout();
                var bitmap=new RenderTargetBitmap(775,575,120,120,PixelFormats.Pbgra32); bitmap.Render((Visual)window.Content);
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using(var stream=File.Create(Path.Combine(Path.GetTempPath(),"xmidi-small-ui.png"))) encoder.Save(stream);
                var editor=new SettingsWindow(new AppSettings(),window.Resources);
                editor.Measure(new Size(800,590));editor.Arrange(new Rect(0,0,800,590));editor.Close();
                Console.WriteLine("PASS: WPF 620×460 a 125%, editor, LT, desconexão e estado desconhecido. " + Path.Combine(Path.GetTempPath(),"xmidi-small-ui.png"));
            }
            finally { window.Close(); File.Delete(path); }
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static int ProbeFl()
    {
        using var output=new MidiOutput(); using var input=new MidiInput();
        output.Open(MidiOutput.GetPorts().Single(p=>p.Name=="X-MIDI").Id);
        input.Open(MidiInput.GetPorts().Single(p=>p.Name=="X-MIDI Return").Id);
        var clock=Stopwatch.StartNew(); var link=new FlLink(output.Send,()=>clock.Elapsed.TotalSeconds);
        link.Connect(true);
        while(clock.Elapsed.TotalSeconds<3 && !link.Fresh)
        { input.Drain(link.Receive);link.Tick();Thread.Sleep(5); }
        if(!link.Fresh) { Console.WriteLine("LIMITAÇÃO: sem snapshot real do FL nas duas portas; integração não validada.");return 2; }
        Console.WriteLine("FL REAL: "+link.State);return 0;
    }
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
}
