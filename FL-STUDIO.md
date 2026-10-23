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
