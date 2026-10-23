using System.IO;
using System.Text;
using System.Text.Json;
using Vortice.XInput;
using XMidi;

internal static class SequenceChecks
{
    internal static readonly (string Name, Action Check)[] All =
    [
        ("PPQ: conversão de tempo, posição de loop e compasso", () =>
        {
            Near(SequenceTiming.SecondsToTicks(.5,120),960); Near(SequenceTiming.TicksToSeconds(960,60),1);
            Near(SequenceTiming.Wrap(-10,3840),3830); Near(SequenceTiming.Wrap(3850,3840),10);
            var p = new SequencePattern { Numerator=3, Denominator=8, Bars=2 }; p.Validate(); Check(p.BarTicks==1440&&p.Length==2880,"Compasso");
        }),
        ("Quantização parcial, tercinas e preservação do tick original", () =>
        {
            var p=new SequencePattern { Quantize=SequenceQuantize.Sixteenth }; var n=new SequenceNote { OriginalTick=190 };
            Check(SequenceTiming.QuantizedTick(n,p)==240,"100%");p.QuantizeStrength=50;Check(SequenceTiming.QuantizedTick(n,p)==215,"50%");
            p.QuantizeStrength=0;Check(SequenceTiming.QuantizedTick(n,p)==190&&n.OriginalTick==190,"0% preserva");
            p.Quantize=SequenceQuantize.EighthTriplet;p.QuantizeStrength=100;n.OriginalTick=300;Check(SequenceTiming.QuantizedTick(n,p)==320,"1/8T");
            p.Quantize=SequenceQuantize.SixteenthTriplet;n.OriginalTick=150;Check(SequenceTiming.QuantizedTick(n,p)==160,"1/16T");
            p.Quantize=SequenceQuantize.Sixteenth;n.OriginalTick=3830;Check(SequenceTiming.QuantizedTick(n,p)==0,"Quantização circular");
            p.Quantize=SequenceQuantize.Off;n.OffsetTicks=-5;Check(SequenceTiming.QuantizedTick(n,p)==3825,"Microtiming editável");
        }),
        ("Swing compartilhado: notas manuais e gravadas usam a mesma projeção", () =>
        {
            var p=new SequencePattern();var n=new SequenceNote { OriginalTick=240 };
            Check(SequenceTiming.PlayTick(n,p,100)==360&&SequenceTiming.PlayTick(n,p,0)==240,"Swing não destrutivo");
            n.OriginalTick=480;Check(SequenceTiming.PlayTick(n,p,100)==480,"Onbeat preservado");
        }),
        ("Sequencer: Play/Stop, velocity e encerramento durante nota ativa", () =>
        {
            using var f=new Fixture();f.Add(0,0,960,83);f.Start();
            Check(f.Messages.SequenceEqual(new[]{(S:(byte)0x90,N:(byte)36,V:(byte)83)}),"Note On");
            f.Engine.Stop();Check(f.Messages[^1]==(0x80,36,0),"Stop envia Off");int count=f.Messages.Count;f.Advance(10);Check(f.Messages.Count==count,"Não volta a tocar");
        }),
        ("Loop contínuo com oito trilhas e eventos no limite do compasso", () =>
        {
            using var f=new Fixture();for(int track=0;track<8;track++)f.Add(track,track*240,60,80+track);
            f.Add(0,3830,5,100);f.Start();for(int tick=30;tick<=3840*3;tick+=30)f.AtTick(tick);
            // Three complete loops, plus the first event of loop four.
            Check(f.Messages.Count(m=>m.S==0x90)==28,"Nenhum evento perdido em três loops");
            f.Engine.Stop();Check(f.Messages.Count(m=>m.S==0x90)==f.Messages.Count(m=>m.S==0x80),"Todas as notas encerradas");
        }),
        ("Notas atravessam o loop e usam o Note Off da instância correta", () =>
        {
            using var f=new Fixture();f.Add(0,3830,200,100);f.Start();f.AtTick(3830);
            Check(f.Messages[^1].S==0x90,"Nota perto do fim");f.AtTick(3840);Check(f.Messages.Count==1,"Sustenta na virada");
            f.AtTick(4030);Check(f.Messages[^1]==(0x80,36,0),"Off no loop seguinte");
        }),
        ("Eventos duplicados preservados; saída coalescida com maior velocity", () =>
        {
            using var f=new Fixture();f.Add(0,0,120,70);f.Add(0,0,240,110);f.Start();
            Check(f.Engine.Snapshot().Tracks[0].Events.Count==2&&f.Messages.Count==1&&f.Messages[0].V==110,"Dados preservados e um On");
            f.AtTick(120);Check(f.Messages.Count==1,"Não corta nota sobreposta");f.AtTick(240);Check(f.Messages[^1]==(0x80,36,0),"Off da última instância");
        }),
        ("Live: nota aparece imediatamente, preserva LT/duração e não ecoa no mesmo ciclo", () =>
        {
            using var f=new Fixture();f.Engine.SetQuantization(SequenceQuantize.Sixteenth,100);f.Engine.BeginRecording(false);f.AtTick(200);
            f.Engine.CaptureDrum(0,37,true);var note=f.Engine.Snapshot().Tracks[0].Events.Single();
            Check(note.OriginalTick==200&&note.Velocity==37,"Grade usa o evento capturado");f.AtTick(220);f.Engine.CaptureDrum(0,0,false);
            f.AtTick(240);Check(f.Messages.Count==0,"Sem eco quantizado da entrada ao vivo");
            f.Engine.EndRecording();Check(f.Engine.IsPlaying&&!f.Engine.IsRecording,"Parar REC mantém Play");
            Check(f.Engine.Snapshot().Tracks[0].Events[0].Duration==20,"Duração capturada");f.AtTick(4080);Check(f.Messages[^1]==(0x90,36,37),"Próximo ciclo reproduz");
        }),
        ("Gravar uma nota não descarta outro instrumento que vence no mesmo tick", () =>
        {
            using var f=new Fixture();f.Add(0,240,100,90);f.Engine.BeginRecording(false);
            f.Time=SequenceTiming.TicksToSeconds(240,120); // The input callback acquires the lock before the worker.
            f.Engine.CaptureDrum(1,80,true);f.Engine.Process(f.Time);
            Check(f.Messages.Any(m=>m.S==0x90&&m.N==36),"Kick pendente continua agendado");
            f.Time=SequenceTiming.TicksToSeconds(250,120);f.Engine.CaptureDrum(1,0,false);
            f.Engine.EndRecording();f.AtTick(4080);f.AtTick(4090);
            Check(f.Messages.Contains((0x80,38,0)),"Duração gravada é consultada na reprodução da próxima volta");
        }),
        ("Overdub: camadas e duas tomadas com Undo/Redo independente", () =>
        {
            using var f=new Fixture();f.Add(2,0,30,60);f.Engine.BeginRecording(false);f.AtTick(120);f.Engine.CaptureDrum(0,100,true);f.AtTick(150);f.Engine.CaptureDrum(0,0,false);f.Engine.EndRecording();
            f.Engine.BeginRecording(false);f.AtTick(360);f.Engine.CaptureDrum(1,50,true);f.AtTick(400);f.Engine.CaptureDrum(1,0,false);f.Engine.EndRecording();
            Check(f.Count()==3,"Camadas preservadas");f.Engine.Undo();Check(f.Count()==2&&f.Engine.Snapshot().Tracks[0].Events.Count==1,"Undo da segunda tomada");
            f.Engine.Redo();Check(f.Count()==3,"Redo da tomada");f.Engine.Overdub=false;
            Reject(()=>f.Engine.BeginRecording(false));Check(f.Count()==3,"Desabilitar não substitui eventos");
        }),
        ("REC: múltiplas voltas, notas duplicadas e encerramento de pendências", () =>
        {
            using var f=new Fixture();f.Engine.BeginRecording(false);
            f.AtTick(100);f.Engine.CaptureDrum(0,90,true);f.AtTick(200);f.Engine.CaptureDrum(0,0,false);
            f.AtTick(3940);f.Engine.CaptureDrum(0,80,true);f.AtTick(4040);f.Engine.EndRecording();
            var notes=f.Engine.Snapshot().Tracks[0].Events;
            Check(notes.Count==2&&notes.All(n=>n.OriginalTick==100&&n.Duration==100),"Passagens aditivas");
            f.Engine.Undo();Check(f.Count()==0,"Tomada inteira é uma operação Undo");
        }),
        ("Count-in: um compasso, captura começa no zero e metrônomo explícito", () =>
        {
            using var f=new Fixture();f.Engine.ConfigureMetronome(true,10,76,80);f.Engine.BeginRecording(true);f.Advance(0);
            Check(f.Engine.Status().CountIn&&f.Engine.Status().CountInBeats==4&&f.Messages[0].S==0x99,"Clique no canal configurado");f.Engine.CaptureDrum(0,100,true);Check(f.Count()==0,"Preparação não gravada");
            f.Advance(2);Check(!f.Engine.Status().CountIn,"Um compasso a 120 BPM");f.Engine.CaptureDrum(0,100,true);Check(f.Count()==1&&f.Engine.Snapshot().Tracks[0].Events[0].OriginalTick==0,"Captura começa no zero");
        }),
        ("Mudança de BPM preserva posição e gravação em ticks", () =>
        {
            using var f=new Fixture();f.Engine.BeginRecording(false);f.Advance(.5);f.Engine.CaptureDrum(0,100,true);
            double before=f.Engine.Status().AbsoluteTick;f.Engine.SetBpm(60);Near(f.Engine.Status().AbsoluteTick,before);
            f.Advance(1.5);f.Engine.CaptureDrum(0,0,false);Check(f.Engine.IsRecording,"REC continua");
            var note=f.Engine.Snapshot().Tracks[0].Events.Single();Check(note.OriginalTick==960&&note.Duration==960,"Tempo musical independente do BPM");
        }),
        ("Quantização durante REC é não destrutiva e não interrompe transporte", () =>
        {
            using var f=new Fixture();f.Engine.BeginRecording(false);f.AtTick(191);f.Engine.CaptureDrum(0,81,true);
            f.Engine.SetQuantization(SequenceQuantize.Sixteenth,50);f.AtTick(210);f.Engine.CaptureDrum(0,0,false);
            Check(f.Engine.IsRecording&&f.Engine.IsPlaying,"Transporte mantido");var p=f.Engine.Snapshot();Check(p.Tracks[0].Events[0].OriginalTick==191,"Original preservado");
            Check(SequenceTiming.QuantizedTick(p.Tracks[0].Events[0],p)==216,"Quantização parcial");
        }),
        ("Edição manual, remoção, Undo/Redo e mudança de resolução sem apagar notas", () =>
        {
            using var f=new Fixture();f.Add(0,123,100,80);var id=f.Engine.Snapshot().Tracks[0].Events[0].Id;
            f.Engine.Edit(p=>{var n=p.Tracks[0].Events[0];n.Velocity=40;n.Duration=333;n.OffsetTicks=-7;p.StepsPerBar=64;});
            var changed=f.Engine.Snapshot().Tracks[0].Events[0];Check(changed.OriginalTick==123&&changed.OffsetTicks==-7&&changed.Duration==333,"Edição não destrutiva");
            f.Engine.Edit(p=>p.Tracks[0].Events.RemoveAll(n=>n.Id==id));Check(f.Count()==0,"Remoção compartilhada");f.Engine.Undo();Check(f.Count()==1,"Undo remoção");f.Engine.Redo();Check(f.Count()==0,"Redo remoção");
        }),
        ("Troca de mapeamento utiliza os bancos existentes e encerra pitch antigo", () =>
        {
            using var f=new Fixture();f.Add(0,0,3840,100);f.Start();var banks=new AppSettings().Banks;banks[0][0].Note=50;f.Engine.SetMappings(banks);
            Check(f.Messages[^1]==(0x80,36,0),"Pitch antigo liberado");f.AtTick(3840);Check(f.Messages[^1]==(0x90,50,100),"Novo mapeamento no loop seguinte");
        }),
        ("Entrada live e loop compartilham propriedade sem On redundante ou Off prematuro", () =>
        {
            var messages=new List<(byte S,byte N,byte V)>();var ownership=new MidiNoteOwnership((s,n,v)=>messages.Add((s,n,v)));
            ownership.Send(0,0x90,36,90);ownership.Send(1,0x90,36,100);ownership.Send(1,0x80,36,0);
            Check(messages.Count==1,"Live ainda possui nota");ownership.Send(0,0x80,36,0);Check(messages.Count==2&&messages[^1].S==0x80,"Off final");
            ownership.Send(0,0x90,36,90);ownership.Send(1,0x99,36,80);Check(messages.Count==4,"Canal do clique independente");
            ownership.Send(0,0x80,36,0);ownership.Send(1,0x89,36,0);Check(messages.Count==6,"Ambos encerrados");
        }),
        ("Integração Drum: captura dois bancos/LT e desconexão finaliza a tomada", () =>
        {
            double time=0;var output=new MidiOutput();using var p=new BoomBapPreset(output,background:false,clock:()=>time);
            p.Sequencer.BeginRecording(false);p.SetTrigger(255);p.Button(GamepadButtons.A,true);time=.1;p.ToggleBank();p.Button(GamepadButtons.A,false);p.Button(GamepadButtons.A,true);time=.2;p.Silence();
            var pattern=p.Sequencer.Snapshot();Check(pattern.Tracks[0].Events.Count==1&&pattern.Tracks[4].Events.Count==1,"Dois bancos nas trilhas originais");
            Check(pattern.Tracks[0].Events[0].Velocity==30&&pattern.Tracks[4].Events[0].Duration==192,"LT e duração");
            Check(!p.Sequencer.IsPlaying&&!p.Sequencer.IsRecording,"Desconexão para transporte");
            Check(output.Messages.Count(m=>m.S==0x90)==output.Messages.Count(m=>m.S==0x80),"Sem notas presas");
        }),
        ("Note Repeat grava ataques reais, intervalos e velocity atual do LT", () =>
        {
            double time=0;using var sequence=new SequenceEngine((s,n,v)=>{},false,()=>time);
            using var drums=new DrumEngine((s,n,v)=>{},new ManualTempoSource(),false,()=>time);
            drums.NotePlayed+=(pad,velocity,on)=>sequence.CaptureDrum(pad,velocity,on);
            sequence.BeginRecording(false);drums.Shoulder(true);drums.Pad(0,36,true);
            drums.Trigger(255);time=.125;drums.Tick(time);time=.25;drums.Tick(time);time=.3;drums.Stop();sequence.EndRecording();
            var notes=sequence.Snapshot().Tracks[0].Events;
            Check(notes.Select(n=>n.OriginalTick).SequenceEqual(new long[]{0,240,480}),"Ataques separados");
            Check(notes.Select(n=>n.Velocity).SequenceEqual(new[]{110,30,30})&&notes[0].Duration==240,"LT e durações capturados");
        }),
        ("Recorder não captura a própria reprodução do sequenciador", () =>
        {
            double time=0;var output=new MidiOutput();using var p=new BoomBapPreset(output,background:false,clock:()=>time);
            p.Sequencer.Edit(pattern=>pattern.Tracks[0].Events.Add(new(){OriginalTick=0,Duration=60,Velocity=90}));
            p.Sequencer.BeginRecording(false);p.Sequencer.Process(0);time=.1;p.Sequencer.Process(time);Check(p.Sequencer.Snapshot().Tracks[0].Events.Count==1,"Sem feedback de gravação");
            p.SetMode(MusicalMode.Chord);Check(!p.Sequencer.IsPlaying&&!p.Sequencer.IsRecording,"Troca de modo encerra sequenciador");
        }),
        ("Persistência, duplicação, seleção e importação sem sobrescrever padrões", () => WithFolder(folder=>
        {
            using var f=new Fixture();f.Add(6,123,321,42);f.Engine.Edit(p=>p.Name="Groove solto");Guid original=f.Engine.Snapshot().Id;
            f.Engine.Create(true);Check(f.Count()==1&&f.Engine.Snapshot().Id!=original,"Cópia independente");f.Engine.Edit(p=>p.Tracks[6].Events[0].Velocity=99);f.Engine.Select(original);
            Check(f.Engine.Snapshot().Tracks[6].Events[0].Velocity==42,"Original preservado");
            var store=new SequenceStore(Path.Combine(folder,"patterns.json"));store.SaveAsync(f.Engine.LibrarySnapshot()).GetAwaiter().GetResult();
            using var restored=new Fixture();restored.Engine.LoadLibrary(store.Load());Check(restored.Count()==1&&restored.Engine.Snapshot().Name=="Groove solto","Reabertura");
            string single=Path.Combine(folder,"single.json");SequenceStore.SavePatternAsync(single,f.Engine.Snapshot()).GetAwaiter().GetResult();restored.Engine.Import(SequenceStore.Read<SequencePattern>(single));
            Check(restored.Engine.LibrarySnapshot().Patterns.Count==3,"Importação adiciona cópia");
            string bad=Path.Combine(folder,"bad.json");File.WriteAllText(bad,"{}");Reject(()=>new SequenceStore(bad).Load());Check(File.ReadAllText(bad)=="{}","Biblioteca inválida preservada");
        })),
        ("Exportação SMF: PPQ, tempo, compasso, oito trilhas, microtiming e notas cruzando loop", () => WithFolder(folder=>
        {
            var p=new SequencePattern { Bpm=90 };p.Tracks[1].Events.Add(new(){OriginalTick=123,Duration=200,Velocity=77});p.Tracks[7].Events.Add(new(){OriginalTick=3830,Duration=200,Velocity=50});
            string file=Path.Combine(folder,"pattern.mid");SequenceMidiFile.Export(file,p,new AppSettings().Banks,0);
            var parsed=ReadMidi(file);Check(parsed.Ppq==960&&parsed.Tracks.Count==9,"Cabeçalho SMF tipo 1");
            Check(parsed.Tracks[0].Any(e=>e.Status==0x51&&e.Bytes.SequenceEqual(new byte[]{10,44,43})),"Tempo 666667 µs");
            Check(parsed.Tracks[0].Any(e=>e.Status==0x58&&e.Bytes[0]==4&&e.Bytes[1]==2),"Compasso 4/4");
            Check(parsed.Tracks[2].Any(e=>e.Tick==123&&e.Status==0x90&&e.Bytes.SequenceEqual(new byte[]{38,77})),"Microtiming e velocity");
            Check(parsed.Tracks[2].Any(e=>e.Tick==323&&e.Status==0x80),"Duração");Check(parsed.Tracks[8].Any(e=>e.Tick==4030&&e.Status==0x80),"Fim além do loop");
        })),
        ("Validação rejeita posições inválidas e redução destrutiva do loop", () =>
        {
            using var f=new Fixture();f.Engine.Edit(p=>p.Bars=2);f.Add(0,5000,100,90);Reject(()=>f.Engine.Edit(p=>p.Bars=1));
            Check(f.Engine.Snapshot().Bars==2&&f.Count()==1,"Sem apagar evento fora do novo tamanho");Reject(()=>f.Engine.SetBpm(double.NaN));
            Reject(()=>f.Engine.Edit(p=>p.Tracks[0].Events[0].Velocity=0));Reject(()=>f.Engine.Edit(p=>p.Tracks[0].Events[0].Duration=0));
        }),
        ("Scheduler real inicia, emite e faz Join encerrando todas as notas", () =>
        {
            using var hit=new ManualResetEventSlim();int ons=0,offs=0;
            var engine=new SequenceEngine((s,n,v)=>{if(s==0x90){Interlocked.Increment(ref ons);hit.Set();}else if(s==0x80)Interlocked.Increment(ref offs);});
            engine.Edit(p=>p.Tracks[0].Events.Add(new(){OriginalTick=0,Duration=3840,Velocity=90}));
            try{engine.Start();Check(hit.Wait(2000),"Worker envia evento");}finally{engine.Dispose();}
            Check(ons==1&&offs==1,"Dispose libera e encerra o worker");
        })
    ];
    private sealed class Fixture:IDisposable
    {
        public double Time;
        public readonly List<(byte S,byte N,byte V)> Messages=[];
        public SequenceEngine Engine {get;}
        public Fixture()=>Engine=new((s,n,v)=>Messages.Add((s,n,v)),false,()=>Time);
        public void Add(int track,long tick,long duration,int velocity)=>Engine.Edit(p=>p.Tracks[track].Events.Add(new(){OriginalTick=tick,Duration=duration,Velocity=velocity}));
        public void Start(){Engine.Start();Engine.Process(Time);}
        public void Advance(double seconds){Time=seconds;Engine.Process(Time);}
        public void AtTick(long ticks)=>Advance(SequenceTiming.TicksToSeconds(ticks,120));
        public int Count()=>Engine.Snapshot().Tracks.Sum(t=>t.Events.Count);
        public void Dispose()=>Engine.Dispose();
    }
    private sealed record Event(long Tick,int Status,byte[] Bytes);
    private static (int Ppq,List<List<Event>> Tracks) ReadMidi(string file)
    {
        using var reader=new BinaryReader(File.OpenRead(file));Check(Encoding.ASCII.GetString(reader.ReadBytes(4))=="MThd","SMF magic");
        Check(Big32(reader)==6&&Big16(reader)==1,"SMF formato");int count=Big16(reader),ppq=Big16(reader);var tracks=new List<List<Event>>();
        for(int i=0;i<count;i++)
        {
            Check(Encoding.ASCII.GetString(reader.ReadBytes(4))=="MTrk","Chunk de trilha");int length=Big32(reader);long end=reader.BaseStream.Position+length,tick=0;var events=new List<Event>();
            while(reader.BaseStream.Position<end)
            {
                tick+=Variable(reader);int status=reader.ReadByte();
                if(status==0xff){int kind=reader.ReadByte(),size=Variable(reader);events.Add(new(tick,kind,reader.ReadBytes(size)));}
                else {Check(status is 0x80 or 0x90,"Evento de nota");events.Add(new(tick,status,reader.ReadBytes(2)));}
            }
            Check(reader.BaseStream.Position==end,"Tamanho exato da trilha");tracks.Add(events);
        }
        Check(reader.BaseStream.Position==reader.BaseStream.Length,"Arquivo inteiro analisado");return(ppq,tracks);
    }
    private static int Big16(BinaryReader r)=>r.ReadByte()<<8|r.ReadByte();
    private static int Big32(BinaryReader r)=>r.ReadByte()<<24|r.ReadByte()<<16|r.ReadByte()<<8|r.ReadByte();
    private static int Variable(BinaryReader r){int value=0,b;do{b=r.ReadByte();value=value<<7|(b&127);}while((b&128)!=0);return value;}
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<.0001,$"{a} != {b}");
    private static void Reject(Action action){try{action();}catch(Exception ex)when(ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException){return;}throw new Exception("Entrada inválida aceita");}
    private static void WithFolder(Action<string> action){string path=Path.Combine(Path.GetTempPath(),$"xmidi-sequence-{Guid.NewGuid():N}");Directory.CreateDirectory(path);try{action(path);}finally{Directory.Delete(path,true);}}
}
