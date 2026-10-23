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
