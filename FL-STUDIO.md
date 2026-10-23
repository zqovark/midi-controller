# Configuração no FL Studio

1. No loopMIDI, crie a porta **X-MIDI** e mantenha o programa aberto.
2. Na pasta deste projeto, instale o script:

   ```powershell
   & .\FL-Studio\Install-Script.ps1
   ```

3. Reinicie o FL Studio. Em **F10 → MIDI → Input**, selecione **X-MIDI**, habilite **Enable** e escolha **Controller type: X-MIDI**. Mantenha **Port** em `---` e não configure retorno MIDI para a mesma porta em Output.
4. Abra [FL-Studio/Boom Bap - Classic.flp](FL-Studio/Boom%20Bap%20-%20Classic.flp) e selecione o canal **Default** (FPC). O projeto requer os samples do kit de fábrica Default.
5. No utilitário, selecione a saída **X-MIDI**. Use **Atualizar** se a porta ainda não aparecer.

A/B/X/Y enviam as notas do banco selecionado. O eixo X esquerdo controla o filtro (CC 74); o direito, reverberação (CC 91). ↓ alterna o banco; ↑ envia Play/Pause; ←/→ ajustam o swing global do sequenciador. O swing não altera notas tocadas ao vivo. LT/RT são apenas indicadores.

Use **Editar mapeamento…** para alterar notas e nomes; isso não carrega samples no FPC. As configurações ficam em `%LOCALAPPDATA%\X-MIDI\settings.json`.

A configuração do FL Studio é manual. Transporte e swing exigem o script; a interface mostra comandos enviados, sem receber o estado do FL.

## Sample Mode e Chord Mode

A seleção de modos é feita na interface do X-MIDI. Os bancos e mapeamentos de Drum, Sample e Chord são independentes. Trocar de modo/banco envia Note Off e interrompe o Note Repeat; os plugins também precisam respeitar Note Off para o áudio parar.

**Chord:** selecione um instrumento melódico no FL. No X-MIDI, escolha preset ou tonalidade/escala/oitava/tipo/voicing e use **Gerar progressão** (oito graus). **Editar 8 acordes** aceita notas MIDI explícitas por pad; nesse caso elas têm precedência sobre a geração. Gerar novamente substitui essas notas explícitas. As notas geradas e as notas efetivamente tocadas aparecem no painel e no teclado. Não requer script de acordes no FL.

Os presets iniciais geram, no primeiro pad e sem acorde anterior para aproximar o registro:

| Preset | Notas MIDI do primeiro acorde |
| --- | --- |
| Jazzy Boom Bap | 48, 51, 55, 58 |
| Soulful | 53, 60, 69, 76 |
| Lo-Fi Chill | 57, 60, 64, 67, 71 |
| Neo Soul | 52, 59, 66, 67, 74 |

Os graus seguem as notas da escala; em pentatônicas, graus 6/7 continuam na oitava seguinte. O tipo escolhido define os intervalos do acorde, que podem incluir notas fora da escala. “Registro próximo” aproxima o acorde anterior por oitavas, preservando o voicing; não é otimização individual de todas as vozes. Notas explícitas mantêm o registro informado.

**Sample:** selecione um WAV no X-MIDI, escolha Equal Slices (2/4/8), Manual ou Transientes e use **Dividir**. Transientes são uma heurística por energia: revise as bordas; ataques ausentes recebem cortes de preenchimento. Selecione uma região para arrastar suas bordas ou editar início/fim em segundos. **Aplicar** salva playback, reverse, pitch e a região editada. **Pads / efeitos** associa os oito pads às regiões e configura CCs/analógicos. Com 2/4 regiões, os pads restantes ficam livres para associação manual. Aplicar um preset de chopping restaura oito regiões e notas 60–67; não altera os outros modos.

Use **Exportar WAVs** e abra o `FL-STUDIO.txt` da subpasta criada. Arraste cada WAV exportado para um pad do **FPC**, configure a nota MIDI indicada em `mapping.json`/`FL-STUDIO.txt` e selecione o canal do FPC no FL. Os números MIDI são a referência; a nomenclatura de oitavas pode diferir entre o FL e o X-MIDI. Nada é carregado automaticamente no FL. Não envie os WAVs de volta ao editor como se fossem o original: eles já contêm reverse/pitch.

- **Gate:** o X-MIDI mantém a nota enquanto o botão estiver pressionado.
- **One Shot:** o X-MIDI mantém a nota pela duração exportada, independentemente da soltura do botão, e agenda Note Off ao terminar. Repetir o mesmo pad reinicia a nota; troca de banco/modo e Silenciar encerram os disparos.
- Para ambos funcionarem e a troca de modos realmente cortar áudio, configure o instrumento/pad com envelope de volume que respeite Note Off (sustain habilitado, release curto). Pads one-shot que ignoram Note Off podem continuar soando após o X-MIDI encerrar as notas. O X-MIDI não altera automaticamente envelopes.
- Reverse e pitch são aplicados **offline** nos WAVs; pitch usa reamostragem linear e muda a duração, sem time-stretch. O arquivo original é preservado. WAV mono/estéreo PCM 8/16/24/32 bits e float 32/64 bits são aceitos; exportação PCM16. Limites: 128 MiB por arquivo e 32 milhões de amostras decodificadas.
- Low-pass, high-pass, reverb e delay são **CCs**, não efeitos de áudio internos. No FL, selecione **Link to controller** no parâmetro desejado e mova o analógico configurado. CC 74/71/91/94 são os padrões; não há efeito garantido sem vínculo. Desative vínculos duplicados caso não queira mover vários parâmetros com o mesmo eixo.
- **Slicex** é uma alternativa manual: carregue o WAV original, crie/ajuste suas regiões no plugin e atribua aos pads do X-MIDI as notas MIDI correspondentes. Os marcadores do editor X-MIDI não são enviados ao Slicex. Esta implementação não usa API para criar regiões, carregar plugins ou importar áudio no FL.

A integração MIDI real, os envelopes do FPC/Slicex e a interface precisam ser validados no Windows com FL Studio; os testes portáveis validam geração MIDI e arquivos, sem executar plugins.
