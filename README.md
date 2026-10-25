# X-MIDI

Requer Windows, SDK .NET 10 e um controle compatível com XInput.
Para o fluxo com FL Studio, mantenha duas portas loopMIDI abertas: X-MIDI e X-MIDI Return.

Na pasta do projeto:

```powershell
dotnet run --project X-MIDI.csproj -c Release
```

Conecte o controle e selecione as portas de saída e retorno em Preparação. View abre o painel de comandos; confirme o modo/canal antes de tocar.
Para usar com FL Studio, siga [a configuração da entrada MIDI](FL-STUDIO.md).

Para executar somente a leitura no console:

```powershell
dotnet run --project X-MIDI.csproj -c Release -- --console
```

Se estiver usando o SDK local desta pasta, substitua `dotnet` por
`& .\.tools\dotnet\dotnet.exe`. Feche o aplicativo antes de recompilar.

## Drum Mode

LT reduz a velocity da nota seguinte: solto usa **Padrão**, pressionado até o fim usa **Mínima** (1–127). As curvas Linear, Suave (redução lenta no início) e Acentuada (redução rápida no início) ficam no painel Drum. As notas já disparadas não mudam.

Ative **Note Repeat** e segure **RB + A/B/X/Y**, em qualquer banco. Selecione a divisão no painel (T = tercina) e ajuste o BPM (20–300). Soltar RB ou o pad encerra as notas; trocas aceitas, desconexão e fechamento interrompem as repetições. Trocar banco com entradas pressionadas é recusado, sem troca pendente. LB e RT tocam kick/snare do banco principal manualmente. Full Level usa 127 e ignora LT. As configurações são salvas junto dos mapeamentos existentes.

O BPM do Repeat é manual. O retorno do FL informa transporte, gravação, BPM, canal e pattern, mas não fase musical. A repetição começa no gesto, sem alinhamento de fase à grade ou sincronização com o transporte/swing do FL. O agendamento usa relógio monotônico em uma thread dedicada; latência/jitter reais dependem do Windows e do driver MIDI e requerem validação com hardware.

Testes do motor, sem dispositivos MIDI ou WPF: `dotnet run --project Tests/DrumMode/X-MIDI.DrumMode.Tests.csproj -c Release`.

## Sample / Chord

Selecione e confirme modo/canal no painel View, com transporte parado/desarmado. Sample Mode oferece waveform, edição de regiões, cortes iguais/manuais/transientes e exportação WAV com reverse/pitch. Chord Mode oferece escalas, progressões de oito acordes, inversões, voicings abertos, presets e notas editáveis por pad. LT continua disponível; os efeitos pelos eixos X exigem preparação explícita e são relativos (centro mantém valor); RB + instrumento permanece exclusivo do Drum Mode.

Cada modo mantém seus mapeamentos e configurações. Veja o [fluxo de carregamento e vínculos no FL Studio](FL-STUDIO.md#sample-mode-e-chord-mode). Áudio, regiões, instrumentos e efeitos não são carregados automaticamente no FL. O teste portável acima inclui também os modos, os acordes e o processamento WAV; não requer WPF, dispositivos ou FL Studio.

## Verificação e limites

`dotnet run --project Tests/X-MIDI.Tests.csproj` verifica WinMM/loopMIDI e WPF; requer a porta X-MIDI Return para o loopback. `python Tests/test_fl_script.py` verifica o protocolo com módulos simulados. `dotnet run --project Tests/X-MIDI.Tests.csproj -- --fl-probe` consulta o retorno real, sem simular sucesso.

A leitura XInput continua no temporizador WPF de 8 ms nominais; trabalho pesado na interface pode atrasar ataques. O worker do Repeat é independente. O controle foi detectado, mas a latência da execução física ainda não foi medida. Consulte [preparação, fluxo e pendências reais](FL-STUDIO.md) antes de gravar.
