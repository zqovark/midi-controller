using Vortice.XInput;
using XMidi;

internal static class SessionChecks
{
    public static readonly (string Name, Action Check)[] All =
    [
        ("RT histerese, LB principal e Full Level", () =>
        {
            var o = new MidiOutput(); using var p = new BoomBapPreset(o, background: false);
            p.SelectBank(1); p.SetTrigger(255); p.Button(GamepadButtons.LeftShoulder, true);
            Check(o.Messages[^1] == (0x90,36,30), "LB usa banco principal e LT");
            p.RightTrigger(99); Check(o.Messages.Count == 1, "Abaixo do limiar");
            p.RightTrigger(100); Check(o.Messages[^1] == (0x90,38,30), "Ataque RT");
            p.RightTrigger(99); p.RightTrigger(71); Check(o.Messages.Count == 2, "Sem oscilação");
            p.RightTrigger(70); Check(o.Messages[^1] == (0x80,38,0), "Limiar de soltura");
            p.SetFullLevel(true); p.RightTrigger(100); Check(o.Messages[^1] == (0x90,38,127), "Full Level ignora LT");
            p.Button(GamepadButtons.RightShoulder, true); Check(!p.IsRepeating, "LB / RT nunca repetem");
            p.SetMode(MusicalMode.Chord); p.RightTrigger(0); int count = o.Messages.Count;
            p.RightTrigger(255); p.Button(GamepadButtons.LeftShoulder, false); p.Button(GamepadButtons.LeftShoulder, true);
            Check(o.Messages.Count == count, "Atalhos Drum não vazam para Chord");
        }),
        ("Simultâneos, bancos sem pendência, Repeat e desconexão", () =>
        {
            using var f = new SessionFixture(); f.Ready();
            f.Sample(GamepadButtons.A | GamepadButtons.X | GamepadButtons.RightShoulder | GamepadButtons.DPadRight, 100, 255);
            Check(f.Preset.IsRepeating && f.Preset.ActiveBank == 0, "Repeat simultâneo e banco recusado");
            Check(f.Output.Messages.Contains((0x90,38,30)), "RT junto dos pads");
            f.Sample(); Check(f.Preset.ActiveBank == 0, "Sem troca pendente");
            f.Press(GamepadButtons.DPadRight); Check(f.Preset.ActiveBank == 1, "Seleção direta");
            f.Sample(); f.Sample(GamepadButtons.A | GamepadButtons.RightShoulder);
            f.Session.Sample(false, default); Check(!f.Preset.IsRepeating && !f.State.Recording, "Desconexão encerra");
            int count = f.NoteOns;
            f.Sample(GamepadButtons.A | GamepadButtons.RightShoulder); f.Sample(GamepadButtons.A);
            Check(f.NoteOns == count, "Reconexão não dispara entradas seguradas");
            f.Sample(); f.Sample(GamepadButtons.A); Check(f.NoteOns == count + 1, "Nova pressão após neutro");
        }),
        ("View toque/700 ms, painel neutro, A 600 ms e tomada", () =>
        {
            using var f = new SessionFixture(); f.Ready();
            f.Sample(GamepadButtons.Back); Check(!f.Session.PanelOpen, "Toque não ocorre na pressão");
            f.Time += .71; f.Sample(GamepadButtons.Back); f.Sample(); f.Sample();
            Check(!f.Session.PanelOpen, "Long press não abre painel ao soltar");
            f.Press(GamepadButtons.Back); f.Sample(); Check(f.Session.PanelOpen, "Abre após Stop confirmado");
            int notes = f.NoteOns; f.Press(GamepadButtons.DPadDown);
            f.Session.RecordingPrepared = true;
            f.Sample(GamepadButtons.A); f.Time += .599; f.Sample(GamepadButtons.A);
            Check(!f.State.Recording, "599 ms não arma");
            f.Time += .002; f.Sample(GamepadButtons.A); Check(f.State.Recording, "600 ms arma");
            f.Time += 1; f.Sample(GamepadButtons.A); Check(f.State.Recording && f.NoteOns == notes, "Manter A não alterna nem toca pad");
            f.Sample(); f.Press(GamepadButtons.Start); Check(f.State.Playing && f.State.Recording, "Menu inicia tomada");
            f.Press(GamepadButtons.Start); Check(!f.State.Playing && !f.State.Recording, "Menu para e desarma");
        }),
        ("Canal confirmado, mudança externa e perda de retorno", () =>
        {
            using var f = new SessionFixture(); f.Ready();
            f.AutoReply = false; f.Session.SelectMode(MusicalMode.Chord); f.Sample(); f.Sample(GamepadButtons.A);
            Check(f.NoteOns == 0 && !f.Session.CanPlay, "Bloqueado enquanto confirmação não chega");
            Check(f.Session.Status.Contains("DESCONHECIDOS"), "Sem exibir estado antigo como confirmação do comando");
            f.AutoReply = true; f.Reply(); f.Sample(); f.Sample(GamepadButtons.A);
            Check(f.NoteOns >= 3, "Acorde após canal confirmado e neutralização");
            f.State = f.State with { Channel = 9 }; f.Time += .5; f.Sample();
            Check(!f.Session.CanPlay && f.Preset.ActiveNotes.Length == 0, "Mudança externa cancela vozes");
            f.AutoReply = false; f.Time += 2; f.Sample();
            Check(!f.Link.Fresh && !f.Session.CanPlay, "Sem retorno, estado desconhecido");
        }),
        ("Stop feito diretamente no FL encerra Repeat e requer nova pressão", () =>
        {
            using var f = new SessionFixture(); f.Ready();
            f.Session.PlayStop(); f.Sample();
            f.Sample(GamepadButtons.A | GamepadButtons.RightShoulder);
            Check(f.Preset.IsRepeating, "Repeat durante transporte");
            f.State = f.State with { Playing = false, Recording = true };
            f.Time += .5; f.Sample(GamepadButtons.A | GamepadButtons.RightShoulder);
            Check(!f.Preset.IsRepeating && !f.State.Recording, "Stop externo também desarma e solta notas");
            int notes = f.NoteOns; f.Sample(GamepadButtons.A); Check(f.NoteOns == notes, "Entrada sustentada não retoma");
        }),
        ("Snapshot incompleto/obsoleto, timeout sem repetição de comando", () =>
        {
            double time = 1; var messages = new List<(byte S,byte N,byte V)>();
            var link = new FlLink((s,n,v) => messages.Add((s,n,v)), () => time);
            link.Connect(true); byte token = messages[^1].V;
            link.Receive(0xBE,110,token); link.Receive(0xBE,0,2); link.Receive(0xBE,111,token);
            Check(!link.Fresh, "Frame incompleto ignorado");
            time += 2; link.Tick();
            Check(messages.Count(m => m.N == 102) == 1, "Stop desejado não repetido por timeout");
            Check(!link.Ready && !link.Command(104,1), "Não arma sem retorno");
        }),
        ("Propriedade da nota entre pad repetido e kick dedicado", () =>
        {
            using var f = new Fixture(); f.Engine.Pad(4,36,true); f.Engine.Shoulder(true); f.Engine.Pad(0,36,true);
            f.Engine.Tick(.125); f.Engine.Pad(0,36,false); f.Engine.Shoulder(false);
            int count = f.Messages.Count; f.Engine.Tick(1); Check(f.Messages.Count == count, "LB não repete e continua dono");
            f.Engine.Pad(4,36,false); Check(f.Messages[^1] == (0x80,36,0), "Último dono encerra");
        })
    ];
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
}

internal sealed class SessionFixture : IDisposable
{
    public double Time = 1;
    public bool AutoReply = true;
    public MidiOutput Output = new();
    public BoomBapPreset Preset;
    public FlLink Link;
    public PerformanceSession Session;
    public FlState State = new(false, false, false, true, true, true, 90, 0, 1, 45, 63, 16, 0);
    private byte token;
    private int high;
    public int NoteOns => Output.Messages.Count(m => m.S == 0x90);
    public SessionFixture()
    {
        Preset = new(Output, background:false, clock:()=>Time);
        Link = new((s,n,v) =>
        {
            Output.Send(s,n,v);
            if(n==108) high=v;
            if(n==102) State=State with { Playing=v==1, Recording=v==0 ? false : State.Recording };
            if(n==104) State=State with { Recording=v==1 };
            if(n==105) State=State with { Channel=v+high*128 };
            if(n==119) { token=v; if(AutoReply) Reply(); }
        },()=>Time);
        Session = new(Preset,Link,()=>Time);
    }
    public void Ready()
    {
        Link.Connect(true); Sample(); Sample(); Session.SelectMode(MusicalMode.Drum); Sample(); Sample();
        Output.Messages.Clear();
    }
    public void Sample(GamepadButtons buttons = GamepadButtons.None, byte rt = 0, byte lt = 0)
        => Session.Sample(true,new Gamepad { Buttons=buttons, RightTrigger=rt, LeftTrigger=lt });
    public void Press(GamepadButtons button) { Sample(button); Sample(); }
    public void Reply()
    {
        int flags=(State.Playing?1:0)|(State.Recording?2:0)|(State.Metronome?4:0)|(State.Loop?8:0)|(State.Precount?16:0)|(State.PatternMode?32:0);
        int bpm=(int)(State.Bpm*10), channel=State.Channel+1;
        int[] values=[2,flags,State.Capabilities,bpm&127,bpm>>7,channel&127,channel>>7,State.Pattern&127,State.Pattern>>7,State.Api,0,State.Beats&127,State.Beats>>7];
        Link.Receive(0xBE,110,token);
        for(byte i=0;i<values.Length;i++) Link.Receive(0xBE,i,(byte)values[i]);
        Link.Receive(0xBE,111,token);
    }
    public void Dispose() => Preset.Dispose();
}
