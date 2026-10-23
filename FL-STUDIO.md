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

## Step Sequencer / Live Loop

No Drum Mode, abra **STEP / LIVE**. A grade e a gravação usam um único padrão de oito trilhas, referenciando os instrumentos dos dois bancos existentes. Clique numa posição vazia para inserir; clique numa nota para selecionar e editar velocity/duração/offset em ticks; clique direito ou **Remover** exclui. Eventos fora da grade aparecem em suas posições reais. Há 16/32/64 passos **por compasso**, 1–16 compassos e navegação horizontal. Diminuir o loop é recusado se deixaria notas fora dele.

- **Play/Stop/REC** controlam somente o transporte local. REC inicia Play se necessário; parar REC mantém Play. Overdub é sempre aditivo: desligá-lo encerra a tomada, sem substituir notas. STEP/LIVE muda a forma de trabalhar, preservando o mesmo padrão.
- As notas e repetições reais do Drum Mode são capturadas com a velocity do LT, posição original e duração em PPQ 960. Notas recém-gravadas entram na reprodução a partir da próxima volta, sem eco na própria passagem. A gravação não captura as mensagens emitidas pelo sequenciador. O BPM do Note Repeat permanece o ajuste próprio já existente; ajuste-o ao BPM local se quiser repetições na mesma velocidade (sem prometer alinhamento de fase).
- Quantização Off/1⁄4/1⁄8/1⁄16/1⁄32/1⁄8T/1⁄16T e força 0–100% não alteram o tick original; offset é uma edição separada. Alterar BPM mantém a posição musical e REC. Editar notas/configuração estrutural encerra a tomada, mas mantém Play quando o tamanho do loop não muda. Undo/Redo encerra REC e para o transporte para restaurar o padrão com segurança. Uma ativação de REC, incluindo suas voltas, é uma única operação Undo.
- Eventos duplicados permanecem no JSON e podem ser editados/desfeitos separadamente. Na reprodução local, notas simultâneas do mesmo pitch/canal compartilham um Note On com a maior velocity e só recebem Note Off após a última duração. Sobreposições do mesmo pitch são legato. Live e sequenciador também compartilham propriedade, evitando On duplicado ou Off que corte uma nota ainda mantida pelo outro.
- **Count-in** prepara um compasso e mostra a contagem antes da captura. O metrônomo MIDI é opcional e exige configurar canal, nota, velocity e um destino audível no FL; o X-MIDI não presume som nem ativa automaticamente o metrônomo da DAW.
- O **swing existente** é a única configuração de swing: no transporte local ele atrasa semicolcheias alternadas em até meia semicolcheia, igualmente para notas da grade e gravadas, sem alterar os dados originais. Enquanto o sequenciador local toca, ajustar swing não envia o comando de swing global ao FL. Fora desse transporte, a integração de swing anterior continua disponível. O swing do sequenciador do FL não é aplicado retroativamente a MIDI ao vivo.
- Não há entrada MIDI de clock/retorno de transporte nesta integração. BPM, fase, Play/Stop e REC locais não são sincronizados com o FL. O botão existente **↑ Play/Pause** para o sequenciador local antes de enviar o comando ao FL. Pare o padrão equivalente na DAW antes de iniciar Play local: o X-MIDI não detecta reprodução iniciada manualmente no FL. Jitter e latência reais exigem validação no Windows; após uma suspensão longa, ataques muito atrasados são ignorados, evitando rajadas de eventos antigos.
- Novo, duplicar, renomear e excluir atuam na biblioteca local. **Salvar JSON** e **Abrir** permitem transportar padrões; abrir adiciona uma cópia. A biblioteca é salva ao fechar em `settings.patterns.json`, ao lado de `settings.json`; uma biblioteca inválida não é sobrescrita automaticamente. JSONs têm limite de 8 MiB; use arquivos individuais para arquivar bibliotecas maiores.
- **MIDI…** exporta SMF tipo 1 com PPQ, BPM, compasso, oito trilhas, nomes/notas dos mapeamentos atuais, velocities, durações e posições com quantização/swing aplicados. O JSON mantém os dados originais. O SMF preserva também eventos sobrepostos/duplicados individualmente; a interpretação dessas sobreposições depende do instrumento/importador da DAW. A exportação para a reprodução local; importe o `.mid` no Piano Roll/canais do FL e configure o FPC/instrumento. Importar MIDI não carrega samples, não configura pads e não assegura sincronização. Use a reprodução local **ou** o padrão importado, evitando emitir as duas cópias.

Os testes portáveis verificam o modelo, agendamento, captura, notas e estrutura SMF; a interface e a importação/reprodução no FL Studio ainda precisam de teste real no Windows.
