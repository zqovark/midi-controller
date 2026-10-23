using System.IO;
using System.Text;
using System.Text.Json;
using Vortice.XInput;
using XMidi;

internal static class ModeChecks
{
    internal static readonly (string Name, Action Check)[] All =
    [
        ("Acordes: nove tipos, escalas e transposição de tonalidade/oitava", () =>
        {
            int[][] intervals = [[0,4,7],[0,3,7],[0,2,7],[0,5,7],[0,4,7,10],[0,4,7,11],[0,3,7,10],[0,3,7,10,14],[0,4,7,11,14]];
            var c = new ChordSettings { Key = 0, Octave = 3 };
            foreach (var kind in Enum.GetValues<ChordKind>())
            {
                c.Pads[0].Kind = kind;
                Equal(ChordGenerator.Generate(c, 0), intervals[(int)kind].Select(n => n + 48));
                c.Key = 2; c.Octave = 4;
                Equal(ChordGenerator.Generate(c, 0), intervals[(int)kind].Select(n => n + 62));
                c.Key = 0; c.Octave = 3;
            }
            foreach (var scale in Enum.GetValues<ScaleKind>())
            {
                c.Scale = scale; c.Pads[0].Kind = ChordKind.Major;
                for (int degree = 1; degree <= 7; degree++)
                {
                    c.Pads[0].Degree = degree;
                    int[] steps = ChordGenerator.Scale(scale);
                    Check(ChordGenerator.Generate(c,0)[0] == 48 + steps[(degree-1)%steps.Length] + (degree-1)/steps.Length*12, "Graus da escala");
                }
            }
        }),
        ("Inversões, voicing aberto, registro próximo e notas personalizadas", () =>
        {
            var c = new ChordSettings(); c.Pads[0].Kind = ChordKind.Major;
            c.Voicing = ChordVoicing.FirstInversion; Equal(ChordGenerator.Generate(c,0), [52,55,60]);
            c.Voicing = ChordVoicing.SecondInversion; Equal(ChordGenerator.Generate(c,0), [55,60,64]);
            c.Voicing = ChordVoicing.Open; Equal(ChordGenerator.Generate(c,0), [48,55,64]);
            c.VoiceLeading = true;
            var close = ChordGenerator.Generate(c,0,[72,79,88]);
            Check(close.Average(n => (double)n) > 70, "Registro aproximado ao acorde anterior");
            c.Pads[0].Notes = [36,60,67,72,60]; Equal(ChordGenerator.Generate(c,0), [36,60,67,72]);
            c.Pads[0].Notes = null; c.Octave = 6; c.Pads[0].OctaveShift = 2; c.Pads[0].Kind = ChordKind.MajorNinth;
            Check(ChordGenerator.Generate(c,0).All(n => n <= 127), "Limites MIDI");
        }),
        ("Presets harmônicos/samples editáveis, sem recursos externos", () =>
        {
            for (int i = 0; i < 4; i++)
            {
                var chord = MusicalPresets.Chord(i); chord.Validate();
                int[][] documented = [[48,51,55,58], [53,60,69,76], [57,60,64,67,71], [52,59,66,67,74]];
                Equal(ChordGenerator.Generate(chord,0), documented[i]);
                Check(chord.Pads.Length == 8 && ChordGenerator.Generate(chord,0).Length >= 4, "Acordes estendidos");
                var sample = new SampleSettings(); MusicalPresets.Sample(sample,i); sample.Validate();
                Check(sample.Pads.Select(p=>p.Note).SequenceEqual(Enumerable.Range(60,8)) && sample.FilePath is null, "Preset sem carregar sample");
                var copy = chord.Copy(); copy.Pads[0].Name = "Editado"; Check(chord.Pads[0].Name != copy.Pads[0].Name, "Cópia independente");
            }
        }),
        ("Vozes MIDI: notas compartilhadas, velocity, Release e Stop", () =>
        {
            var messages = new List<(byte S, byte N, byte V)>();
            using var voices = new MidiVoices((s,n,v)=>messages.Add((s,n,v)), background:false);
            voices.Play(0,[60,64,67],80); voices.Play(1,[60,65,69],30);
            Check(messages.Count == 5 && messages[0].V == 80 && messages[^1].V == 30, "Deduplica notas comuns");
            voices.Release(0); Check(!messages.Contains((0x80,60,0)) && messages.Contains((0x80,64,0)), "Nota comum mantida");
            voices.Stop(); Check(voices.ActiveNotes.Length == 0, "Todas encerradas");
            Check(messages.Count(m=>m.S==0x90) == messages.Count(m=>m.S==0x80), "Um Off por On");
        }),
        ("Sample One Shot: release agendado independente do botão", () =>
        {
            double time=0; var messages = new List<(byte S,byte N,byte V)>();
            using var voices = new MidiVoices((s,n,v)=>messages.Add((s,n,v)),false,()=>time);
            voices.Play(0,[60],110,.25); voices.Tick(.249); Check(messages.Count==1,"Mantém até duração");
            voices.Tick(.25); Check(messages[^1]==(0x80,60,0),"Encerra no final");
            voices.Play(0,[60],90,.5); voices.Stop(); int count=messages.Count; voices.Tick(10); Check(messages.Count==count,"Stop cancela prazo futuro");
        }),
        ("Troca de modos/bancos: encerra acordes e Drum Repeat e preserva configurações", () =>
        {
            var output=new MidiOutput(); using var p=new BoomBapPreset(output,background:false);
            p.Button(GamepadButtons.RightShoulder,true); p.Button(GamepadButtons.A,true); Check(p.IsRepeating,"Drum ativo");
            p.ToggleBank(); p.SetMode(MusicalMode.Chord); Check(!p.IsRepeating && p.ActiveBank==0,"Modo novo / repeat parado");
            p.Button(GamepadButtons.A,false); p.Button(GamepadButtons.A,true);
            Check(p.ActiveNotes.Length>=4,"Acorde enviado"); p.ToggleBank(); Check(p.ActiveNotes.Length==0,"Banco encerra acorde");
            p.SetMode(MusicalMode.Drum); Check(p.ActiveBank==1 && p.Settings.Banks[1][0].Note==37,"Banco Drum preservado");
            p.SetMode(MusicalMode.Chord); Check(p.ActiveBank==1,"Banco Chord preservado");
            p.Button(GamepadButtons.A,false); p.SetTrigger(255); p.Button(GamepadButtons.A,true);
            Check(output.Messages.Last(m=>m.S==0x90).V==30,"LT reutilizado");
            p.SetMode(MusicalMode.Sample); Check(p.ActiveNotes.Length==0,"Troca encerra todas as notas");
            Check(output.Messages.Count(m=>m.S==0x90)==output.Messages.Count(m=>m.S==0x80),"Sem notas presas");
        }),
        ("Mapeamento de slices nos dois bancos, Gate, One Shot e desconexão", () =>
        {
            var settings=new AppSettings { Mode=MusicalMode.Sample }; settings.Samples.SourceFrames=8000;settings.Samples.SourceRate=8000;
            settings.Samples.Playback=SlicePlayback.Gate;
            var output=new MidiOutput(); using var p=new BoomBapPreset(output,settings,false);
            p.Button(GamepadButtons.A,true); Check(output.Messages[^1].N==60,"Slice 1");
            p.Button(GamepadButtons.A,false); Check(output.Messages[^1]==(0x80,60,0),"Gate release");
            p.ToggleBank(); p.Button(GamepadButtons.Y,true); Check(output.Messages[^1].N==67,"Slice 8");
            p.Silence(); Check(output.Messages.Contains((0x80,67,0)) && p.ActiveNotes.Length==0,"Desconexão / Silence");
            var sample=p.Settings.Samples.Copy(); sample.Playback=SlicePlayback.OneShot; p.ApplyModeSettings(samples:sample);
            p.Button(GamepadButtons.A,true); int count=output.Messages.Count; p.Button(GamepadButtons.A,false);
            Check(output.Messages.Count==count,"One Shot ignora soltura do botão");
            p.SetMode(MusicalMode.Chord); Check(output.Messages[^1]==(0x80,64,0),"Troca encerra One Shot");
        }),
        ("Efeitos Sample: CC configuráveis e canais existentes", () =>
        {
            var s=new AppSettings { Mode=MusicalMode.Sample };
            for(int i=0;i<4;i++){s.Samples.Effects[i].Enabled=true;s.Samples.Effects[i].Cc=20+i;s.Samples.Effects[i].Stick=i%2;}
            var output=new MidiOutput();using var p=new BoomBapPreset(output,s,false);
            p.Analog(new Gamepad { LeftThumbX=short.MinValue,RightThumbX=short.MaxValue });
            for(int i=0;i<4;i++) Check(output.Messages.Contains(((byte)0xB0,(byte)(20+i),(byte)(i%2==0?0:127))),"CC correto");
        }),
        ("Equal Slices 2/4/8: cobertura, limites e pequenos samples", () =>
        {
            var audio=Audio();
            foreach(int count in new[]{2,4,8})
            {
                var regions=SampleProcessor.Divide(audio,ChopMethod.Equal,count,.5); int end=0;
                foreach(var region in regions){var b=SampleProcessor.Bounds(audio,region);Check(b.Start==end && b.End>b.Start,"Contínuo");end=b.End;}
                Check(end==audio.Frames,"Cobre até último frame");
            }
            Reject(()=>SampleProcessor.Bounds(audio,new(){Start=-.1,End=.5}));
            Reject(()=>SampleProcessor.Bounds(audio,new(){Start=.5,End=.5}));
            Reject(()=>SampleProcessor.Bounds(audio,new(){Start=0,End=1.1}));
            Reject(()=>SampleProcessor.Divide(new(8000,1,[0,0],"test"),ChopMethod.Equal,8,.5));
            Reject(()=>SampleProcessor.Bounds(audio,new(){Start=0,End=.000001}));
        }),
        ("Transientes: ataques, silêncio e sensibilidade determinística", () =>
        {
            var samples=new float[8000];for(int i=2000;i<2400;i++)samples[i]=.8f;for(int i=5000;i<5400;i++)samples[i]=1;
            var audio=new SampleAudio(8000,1,samples,"test");
            var regions=SampleProcessor.Divide(audio,ChopMethod.Transients,4,1);
            Check(regions.Any(r=>Math.Abs(r.Start-.25)<.01)&&regions.Any(r=>Math.Abs(r.Start-.625)<.01),"Ataques detectados");
            var silence=SampleProcessor.Divide(Audio(),ChopMethod.Transients,8,.5);
            Check(silence.Length==8 && silence.All(r=>r.End>r.Start),"Fallback editável");
        }),
        ("WAV: exportação PCM, reverse, pitch, estéreo e fonte preservada", () => WithFolder(folder=>
        {
            var audio=new SampleAudio(8000,2,Enumerable.Range(0,800).SelectMany(i=>new[]{i/1000f,-i/1000f}).ToArray(),"test");
            var original=audio.Samples.ToArray();
            string normal=Path.Combine(folder,"normal.wav"), reverse=Path.Combine(folder,"reverse.wav"), pitch=Path.Combine(folder,"pitch.wav");
            SampleProcessor.WriteSlice(normal,audio,new(){Start=.25,End=.75},false,0);
            SampleProcessor.WriteSlice(reverse,audio,new(){Start=.25,End=.75},true,0);
            SampleProcessor.WriteSlice(pitch,audio,new(){Start=.25,End=.75},false,12);
            var a=SampleProcessor.Load(normal);var b=SampleProcessor.Load(reverse);var c=SampleProcessor.Load(pitch);
            Check(a.Frames==400&&a.Channels==2&&a.Rate==8000&&c.Frames==200,"Cabeçalho e pitch/duração");
            Near(a.Samples[0],.2);Near(a.Samples[1],-.2);Near(b.Samples[0],.599);Near(b.Samples[^2],.2);
            Check(original.SequenceEqual(audio.Samples),"Original imutável");
            Reject(()=>SampleProcessor.WriteSlice(normal,audio,new(),false,0)); // no overwrite
        })),
        ("WAV: PCM24/float e rejeição de formatos truncados/não finitos", () => WithFolder(folder=>
        {
            string path=Path.Combine(folder,"test.wav");
            WriteRaw(path,1,24,[0,0,0,255,255,127,0,0,128]);
            var pcm=SampleProcessor.Load(path); Check(pcm.Frames==3,"PCM24 decodificado");Near(pcm.Samples[1],1);Near(pcm.Samples[2],-1);
            WriteRaw(path,3,32,BitConverter.GetBytes(.5f)); Near(SampleProcessor.Load(path).Samples[0],.5);
            WriteRaw(path,3,32,BitConverter.GetBytes(float.NaN)); Reject(()=>SampleProcessor.Load(path));
            File.WriteAllText(path,"RIFF"); Reject(()=>SampleProcessor.Load(path));
            WriteRaw(path,1,16,[1]); Reject(()=>SampleProcessor.Load(path));
        })),
        ("Exportação: arquivos, manifesto, mapeamentos e subpastas sem sobrescrita", () => WithFolder(folder=>
        {
            var source=Audio();var settings=new SampleSettings { SourceFrames=source.Frames,SourceRate=source.Rate,SourceHash=source.Hash };
            string output=SampleProcessor.Export(folder,source,settings);
            Check(Directory.GetFiles(output,"*.wav").Length==8,"Oito WAVs");
            var mapped=JsonSerializer.Deserialize<SampleSettings>(File.ReadAllText(Path.Combine(output,"mapping.json")))!;
            Check(mapped.Pads[7].Note==67&&mapped.Pads[7].Region==7,"Manifesto musical");
            Check(File.ReadAllText(Path.Combine(output,"FL-STUDIO.txt")).Contains("Banco 2 Y"),"Instruções por pad");
            Check(SampleProcessor.Export(folder,source,settings)!=output,"Destino único");
            settings.SourceHash="changed";Reject(()=>SampleProcessor.Export(folder,source,settings));
        })),
        ("Persistência independente e compatibilidade com arquivos antigos", () => WithFolder(folder=>
        {
            var old=JsonSerializer.Deserialize<AppSettings>("{\"SwingPercent\":25,\"Banks\":[[{\"Name\":\"Kick\",\"Note\":36},{\"Name\":\"Snare\",\"Note\":38},{\"Name\":\"Hat\",\"Note\":42},{\"Name\":\"Open\",\"Note\":46}],[{\"Name\":\"A\",\"Note\":37},{\"Name\":\"B\",\"Note\":54},{\"Name\":\"X\",\"Note\":45},{\"Name\":\"Y\",\"Note\":49}]]}")!;
            old.Validate();Check(old.Mode==MusicalMode.Drum&&old.SwingPercent==25,"Defaults antigos preservados");
            old.Mode=MusicalMode.Chord;old.Chords.Pads[0].Notes=[36,60,67];old.Samples.Reverse=true;old.Samples.Pads[0].Note=50;
            var copy=old.Copy();copy.Chords.Pads[0].Notes![0]=35;copy.Samples.Regions[0].End=.1;
            Check(old.Chords.Pads[0].Notes![0]==36&&old.Samples.Regions[0].End==.125,"Cópia profunda");
            var store=new SettingsStore(Path.Combine(folder,"settings.json"));store.SaveAsync(old).GetAwaiter().GetResult();
            var saved=store.Load(out var warning);Check(warning is null&&saved.Mode==MusicalMode.Chord&&saved.Chords.Pads[0].Notes![0]==36&&saved.Samples.Reverse&&saved.Samples.Pads[0].Note==50,"Round trip");
            old.Samples.Pads[0].Region=99;Reject(old.Validate);
        }))
    ];
    private static SampleAudio Audio() => new(8000,1,new float[8000],"test-source");
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Equal(IEnumerable<byte> actual,IEnumerable<int> expected)=>Check(actual.Select(n=>(int)n).SequenceEqual(expected),"Notas inesperadas");
    private static void Near(double a,double b)=>Check(Math.Abs(a-b)<.0001,$"{a} != {b}");
    private static void Reject(Action action)
    {
        try{action();}catch(Exception ex)when(ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException){return;}
        throw new Exception("Entrada inválida aceita");
    }
    private static void WithFolder(Action<string> action)
    {
        string folder=Path.Combine(Path.GetTempPath(),$"xmidi-modes-{Guid.NewGuid():N}");Directory.CreateDirectory(folder);
        try{action(folder);}finally{Directory.Delete(folder,true);}
    }
    private static void WriteRaw(string path,ushort format,ushort bits,byte[] data)
    {
        using var writer=new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(36+data.Length+(data.Length%2));writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);writer.Write(format);writer.Write((ushort)1);writer.Write(8000);writer.Write(8000*(bits/8));writer.Write((ushort)(bits/8));writer.Write(bits);
        writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write(data.Length);writer.Write(data);if(data.Length%2!=0)writer.Write((byte)0);
    }
}
