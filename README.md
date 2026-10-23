# X-MIDI

Requer Windows, SDK .NET 10 e um controle compatível com XInput.
Para enviar MIDI, mantenha uma porta virtual do loopMIDI aberta.

Na pasta do projeto:

```powershell
dotnet run --project X-MIDI.csproj -c Release
```

Conecte o controle e selecione a porta de saída MIDI na janela.
Para usar com FL Studio, siga [a configuração da entrada MIDI](FL-STUDIO.md).

Para executar somente a leitura no console:

```powershell
dotnet run --project X-MIDI.csproj -c Release -- --console
```

Se estiver usando o SDK local desta pasta, substitua `dotnet` por
`& .\.tools\dotnet\dotnet.exe`. Feche o aplicativo antes de recompilar.

## Drum Mode

LT reduz a velocity da nota seguinte: solto usa **Padrão**, pressionado até o fim usa **Mínima** (1–127). As curvas Linear, Suave (redução lenta no início) e Acentuada (redução rápida no início) ficam no painel Drum. As notas já disparadas não mudam.

Ative **Note Repeat** e segure **RB + A/B/X/Y**, em qualquer banco. Selecione a divisão no painel (T = tercina) e ajuste o BPM (20–300). Soltar RB ou o pad encerra as notas; trocar de banco, desconectar e fechar interrompem as repetições. As configurações são salvas junto dos mapeamentos existentes.

O BPM é manual: a conexão atual apenas envia MIDI e não recebe tempo ou posição do FL Studio. A repetição começa no gesto, sem alinhamento de fase à grade ou sincronização com o transporte/swing do FL. O agendamento usa relógio monotônico em uma thread dedicada; latência/jitter reais dependem do Windows e do driver MIDI e requerem validação com hardware.

Testes do motor, sem dispositivos MIDI ou WPF: `dotnet run --project Tests/DrumMode/X-MIDI.DrumMode.Tests.csproj -c Release`.

## Sample / Chord

Selecione o modo ao lado do controle Xbox. Sample Mode oferece waveform, edição de regiões, cortes iguais/manuais/transientes e exportação WAV com reverse/pitch. Chord Mode oferece escalas, progressões de oito acordes, inversões, voicings abertos, presets e notas editáveis por pad. LT e os analógicos continuam disponíveis; RB + instrumento permanece exclusivo do Drum Mode.

Cada modo mantém seus mapeamentos e configurações. Veja o [fluxo de carregamento e vínculos no FL Studio](FL-STUDIO.md#sample-mode-e-chord-mode). Áudio, regiões, instrumentos e efeitos não são carregados automaticamente no FL. O teste portável acima inclui também os modos, os acordes e o processamento WAV; não requer WPF, dispositivos ou FL Studio.

## Step Sequencer / Live Loop Recording

Em Drum Mode, **STEP / LIVE** abre a grade de oito instrumentos. Crie notas com o mouse ou grave camadas com o Xbox sobre o mesmo padrão; os eventos mantêm velocity, duração e microtiming em PPQ 960. Quantização não destrutiva, 16/32/64 passos por compasso, count-in, metrônomo MIDI configurável, Undo/Redo, biblioteca JSON e exportação `.mid` estão disponíveis. O transporte é local, sem sincronismo de clock/posição com o FL Studio; veja [operação e limites](FL-STUDIO.md#step-sequencer--live-loop).
