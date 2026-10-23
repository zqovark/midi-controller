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
