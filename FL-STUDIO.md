# Preparação e execução no FL Studio

## Duas portas e retorno confirmado

1. Mantenha o loopMIDI aberto com **X-MIDI** e **X-MIDI Return**, portas distintas.
2. Execute `& .\FL-Studio\Install-Script.ps1`. Reinicie o FL ou recarregue o script.
3. Em **F10 → MIDI → Input**, habilite **X-MIDI**, escolha **X-MIDI (user)** e atribua, por exemplo, **Port 10**.
4. Em **Input**, habilite **X-MIDI Return**, selecione o script **X-MIDI Return (user)** e defina **Port 11**. Em **Output**, atribua **X-MIDI Return** à **Port 11**. Ative **Options → Enable MIDI output**. Não habilite clock/master sync.
   O script principal repassa os snapshots internamente ao script de retorno. Esse segundo script consome o retorno físico sem reenviá-lo, evitando loops. Não use controlador genérico na entrada X-MIDI Return. A saída X-MIDI pode ficar na Port 10; o script principal não envia feedback por ela.
5. No X-MIDI, abra **Preparação**, selecione saída **X-MIDI** e retorno **X-MIDI Return**. O aplicativo recusa o mesmo nome nas duas seleções.
6. Abra a cópia `FL-Studio/Boom Bap - X-MIDI Test.flp`. O original `Boom Bap - Classic.flp` é preservado. Veja abaixo o estado real dessa cópia; ela ainda precisa dos chops.

Em **View → Script output → X-MIDI**, a inicialização informa API, BPM e quantidade de receptores de feedback. Esse diagnóstico não substitui um snapshot recebido no aplicativo.

Notas seguem pelo canal MIDI 1, sem tratamento no script; comandos usam o canal 16 e retorno usa o canal 15. Canal FL significa a posição global no Channel Rack, exibida a partir de 1, e não o canal MIDI. Confirme os três destinos em Preparação, inclusive após reordenar os canais. No projeto de origem, **Drum = canal FL 2** (FPC) e **Sample = canal FL 1** (Sampler a preparar); o instrumento de Chord precisa ser adicionado/configurado manualmente.

Sem snapshot completo e recente, o aplicativo exibe estado desconhecido e bloqueia novos ataques, transporte de início e gravação. Selecionar modo exige confirmação do canal pelo FL. Comandos não são reenviados após timeout. A consulta de estado ocorre a cada ~400 ms, expira em 1,5 s e o script para/desarma após 2 s sem consultas. Isso não substitui conferir o transporte no FL se a comunicação falhar.

## Preparar instrumentos e tomada

- No canal de chops, carregue manualmente um FPC/Slicex e os WAVs exportados pelo Sample Mode; o Sampler vazio da cópia não representa um instrumento de oito chops pronto. Não há carregamento automático de plugins/áudio.
- Confira notas dos pads, envelopes de volume, sustain/release e grupos de corte do chimbal/chops. Note Off, CC 123 ou CC 120 podem ser ignorados por plugins; valide o silêncio audível.
- Use compasso **4/4**. No painel View, **Preparar 4 compassos / Loop / precontagem** define 16 beats, modo Pattern, Loop Record e precontagem, quando a API oferece esses recursos.
- Ative **Blend recorded notes / overdub** manualmente e configure o filtro de gravação para **Notes**. A API documentada fornece toggle de Blend, mas não uma leitura de estado que permita automatizá-lo com segurança. Confira também se o canal recebe MIDI nativo, sem bloqueio/roteamento para outro instrumento. Marque a confirmação correspondente em Preparação a cada sessão.
- Se aparecer o diálogo **Recording**, marque **Don't ask this in the future** e escolha **Notes and automation**. Um diálogo modal aberto impede os comandos de execução; resolva-o antes de tocar pelo controle.
- Ajuste o BPM manual do Repeat ao BPM exibido do FL. Receber BPM **não** sincroniza fase; Repeat continua livre.

## Controle

| Entrada | Ação |
| --- | --- |
| Drum A/B/X/Y, banco 1 | Kick / snare / fechado / aberto |
| Drum A/B/X/Y, banco 2 | Rim / tamborim / tom / crash; nomes/notas editáveis |
| LB / RT em Drum | Kick / snare do banco principal; sempre manuais |
| RT | Ataque ≥100/255, soltura ≤70/255 |
| LT | Reduz velocity no próximo ataque, até a mínima configurada |
| RB + pads em Drum | Note Repeat; cima/baixo muda divisão apenas com RB solto |
| Esquerda / direita | Seleciona banco 1 / 2; recusa se entradas musicais estiverem pressionadas |
| Menu | Play/Stop; Stop também desarma e libera notas |
| View ao soltar | Abre/fecha painel, após Stop/desarmar confirmados |
| View por 700 ms | Stop, desarmar e silenciar; não abre painel ao soltar |
| Painel | Cima/baixo navega; esquerda/direita escolhe modo/pattern; A confirma; B volta |
| Armar gravação | A mantido por 600 ms; depois Menu inicia a tomada |
| Full Level | Velocity 127, ignora LT |

Entrar/sair do painel, trocar modo e reconectar exigem neutralizar entradas. Não há desfazer camada/apagar por atalhos. Eixos Y, cliques dos sticks e Guide não têm novas funções. LB/RT não recebem atalhos de bateria em Sample/Chord.

Para criar uma frase: confirme Sample/canal; arme e grave chops; pare; confirme Drum/canal; arme e acrescente kick/snare; faça outra passagem para hats/ghost notes. Pare e desarme antes de **Duplicar variação**. Aguarde o novo número de pattern confirmado, arme e grave a virada. A clonagem procura destino vazio e intocado, sem substituir a origem; requer API 43+.

## Estado da verificação desta entrega

- .NET: testes determinísticos de execução/transições, acordes, WAV, feedback e falhas; Python: contrato da API com módulos simulados. Esses testes não demonstram integração musical.
- WinMM/loopMIDI: envio e recepção reais de notas, ownership, velocity mínima e mensagens de retorno; o loopMIDI precisou ser reiniciado por ausência de tráfego.
- FL instalado: 26.1.7.5654, API 45 e capacidades 63 confirmadas. O probe WinMM do aplicativo recebeu um snapshot completo pela porta **X-MIDI Return**, com 140,0 BPM, canal, pattern e estados reais. A escala de BPM desta API foi normalizada.
- Comandos reais confirmados: seleção dos canais FL 1/2, metrônomo ligado/desligado, Pattern com 16 beats, Loop Record e precontagem, Play/Stop, clonagem do pattern 1 para 2 e retorno ao pattern 1. A clonagem foi testada no pattern de preparação, sem validar ainda a preservação de uma tomada musical.
- Uma tentativa de Record abriu o caminho de áudio/Edison, portanto não comprova gravação MIDI. Transporte agora usa as funções nativas com foco no Channel Rack para evitar o foco de plugins.
- Pendente: Record/Blend/Loop com notas realmente presentes no Piano roll, preservação dessas notas ao clonar, envelopes/cortes audíveis e timing sob carga. Um controle Xbox foi detectado; o fluxo físico e o timing ainda estão pendentes.
- A cópia conserva o FPC original e seus samples de fábrica; o canal de chops ainda precisa de instrumento/samples. Não é apresentada como template musical totalmente validado.

Referência utilizada: [API oficial MIDI Scripting da Image-Line](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/midi_scripting.htm). O script anuncia versão da API/capacidades e recusa comandos incompatíveis; uma função documentada não garante que o ambiente local esteja configurado.

## Sample Mode e Chord Mode

A seleção de modo/canal deve ser confirmada no painel View do X-MIDI, com o FL parado e desarmado. Os bancos e mapeamentos de Drum, Sample e Chord são independentes. Trocar de banco com entradas pressionadas é recusado. Trocas aceitas encerram as notas e exigem nova pressão; os plugins também precisam respeitar Note Off para o áudio parar.

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
- Low-pass, high-pass, reverb e delay são **CCs**, não efeitos de áudio internos. No FL, vincule o CC indicado ao parâmetro. No X-MIDI, use **Preparar efeitos vinculados em 64**: isso envia explicitamente 64 e estabelece o valor inicial. Depois mova X; centralizar mantém o último valor. Conectar ou trocar modo desativa os efeitos e não envia valores. Em Sample, habilite também os efeitos desejados no editor. CC 74/71/91/94 são os padrões; não há efeito garantido sem vínculo. Desative vínculos duplicados caso não queira mover vários parâmetros com o mesmo eixo.
- **Slicex** é uma alternativa manual: carregue o WAV original, crie/ajuste suas regiões no plugin e atribua aos pads do X-MIDI as notas MIDI correspondentes. Os marcadores do editor X-MIDI não são enviados ao Slicex. Esta implementação não usa API para criar regiões, carregar plugins ou importar áudio no FL.

A integração MIDI real, os envelopes do FPC/Slicex e a interface precisam ser validados no Windows com FL Studio; os testes portáveis validam geração MIDI e arquivos, sem executar plugins.
