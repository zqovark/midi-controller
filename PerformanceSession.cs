using Vortice.XInput;

namespace XMidi;

// One sampled state decides simultaneous inputs, holds, neutralization and command edges.
internal sealed class PerformanceSession(BoomBapPreset preset, FlLink fl, Func<double> now)
{
    private const GamepadButtons Musical = GamepadButtons.A | GamepadButtons.B | GamepadButtons.X | GamepadButtons.Y
        | GamepadButtons.LeftShoulder | GamepadButtons.RightShoulder;
    private const GamepadButtons Navigation = GamepadButtons.DPadUp | GamepadButtons.DPadDown | GamepadButtons.DPadLeft | GamepadButtons.DPadRight;
    private GamepadButtons previous;
    private bool connected, neutral = true, backLong, armDone, opening, wasFresh, routeReady, wasPlaying;
    private double backAt, armAt;
    private int row, candidateMode, candidatePattern = 1;
    public bool PanelOpen { get; private set; }
    public bool RecordingPrepared { get; set; }
    public string Message { get; private set; } = "Prepare duas portas e confirme o canal no FL";
    public bool CanPlay => fl.Ready && routeReady && fl.State?.Channel == TargetChannel;
    private int TargetChannel => preset.Settings.ModeChannels[(int)preset.Settings.Mode];
    public string Status => fl.Ready && fl.State is { } s
        ? $"{preset.Settings.Mode} · canal FL {s.Channel + 1} · banco {preset.ActiveBank + 1} · pattern {s.Pattern}\n"
            + $"{(s.Playing ? "TOCANDO" : "PARADO")} · REC {(s.Recording ? (s.Playing ? "GRAVANDO" : "ARMADO") : "DESARMADO")} · {s.Bpm:0.0} BPM · {(CanPlay ? "canal confirmado" : "ataques bloqueados")}"
        : $"{preset.Settings.Mode} · banco {preset.ActiveBank + 1} · FL / REC / canal / pattern DESCONHECIDOS";
    public string[] MenuItems => [
        $"Modo / canal: {(MusicalMode)candidateMode} → FL {preset.Settings.ModeChannels[candidateMode] + 1}",
        "Armar gravação: manter A por 600 ms (soltar A desarmado cancela)",
        $"Metrônomo: {(fl.Ready ? fl.State!.Metronome ? "ligado" : "desligado" : "?")}",
        $"Selecionar pattern: {candidatePattern}", "Duplicar variação (preserva origem)",
        $"Dinâmica: {(preset.Settings.FullLevel ? "Full Level 127" : "normal / LT")}",
        $"Repeat livre: {preset.Settings.RepeatDivision}", "Preparar 4 compassos / Loop / precontagem"];
    public int SelectedRow => row;
    public void Tick()
    {
        fl.Tick();
        bool fresh = fl.Fresh;
        if (wasFresh && !fresh)
        {
            Stop(); routeReady = false; RecordingPrepared = false;
            Message = "Retorno perdido: notas liberadas; confirme o canal novamente";
        }
        wasFresh = fresh;
        if (fl.Ready && fl.State is { } s)
        {
            if (wasPlaying && !s.Playing)
            {
                preset.Silence(); neutral = true;
                if (s.Recording) Stop();
            }
            wasPlaying = s.Playing;
            if (s.Channel != TargetChannel && routeReady)
            { Stop(); routeReady = false; Message = "Canal mudou no FL; confirme Modo / canal no painel"; }
            if (opening && !s.Playing && !s.Recording)
            { opening = false; PanelOpen = true; candidateMode = (int)preset.Settings.Mode; candidatePattern = s.Pattern; }
        }
    }
    public void Stop()
    {
        preset.Silence(); neutral = true; armAt = 0; armDone = true;
        fl.Command(102, 0, safety: true);
    }
    public void TogglePanel()
    {
        preset.Silence(); neutral = true; armAt = 0;
        if (PanelOpen || opening) { PanelOpen = opening = false; return; }
        Stop(); opening = true; Message = "Aguardando Stop e gravação desarmada para abrir painel";
    }
    public void SelectMode(MusicalMode mode)
    {
        if (!fl.Ready || fl.State!.Playing || fl.State.Recording)
        { Message = "Pare e desarme a gravação antes de selecionar modo/canal"; return; }
        preset.Silence(); neutral = true; routeReady = false;
        preset.SetMode(mode); candidateMode = (int)mode;
        if (fl.Command(105, TargetChannel)) selectingChannel = true;
    }
    private bool selectingChannel;
    public void PlayStop()
    {
        if (!fl.Ready || fl.State!.Playing) { Stop(); return; }
        if (!CanPlay) { Message = "Confirme Modo / canal no painel antes de tocar"; return; }
        if (fl.State.Recording && !RecordingPrepared)
        { Stop(); Message = "Confirme Blend e filtro de notas na preparação antes de gravar"; return; }
        preset.Silence(); neutral = true; PanelOpen = opening = false;
        fl.Command(102, 1);
    }
    public void Sample(bool isConnected, Gamepad g)
    {
        Tick();
        if (selectingChannel && fl.Ready)
        {
            routeReady = fl.State!.Channel == TargetChannel && fl.State.Error == 0;
            selectingChannel = false; neutral = true;
            Message = routeReady ? "Canal confirmado; solte as entradas para tocar" : "Canal não confirmado; selecione novamente";
        }
        if (connected != isConnected)
        {
            connected = isConnected; previous = g.Buttons; Stop(); backAt = now(); armAt = 0; backLong = true;
            PanelOpen = opening = false;
            Message = connected ? "Controle conectado; solte as entradas. View abre comandos" : "Controle desconectado; gravação e Repeat interrompidos";
        }
        if (!connected) return;
        preset.SetTrigger(g.LeftTrigger);
        var pressed = g.Buttons & ~previous;
        var released = previous & ~g.Buttons;
        previous = g.Buttons;
        // Emergency remains available while waiting for a neutral musical input.
        if ((pressed & GamepadButtons.Back) != 0) { backAt = now(); backLong = false; }
        if ((g.Buttons & GamepadButtons.Back) != 0 && !backLong && now() - backAt >= .7)
        { backLong = true; PanelOpen = opening = false; Stop(); Message = "Stop / desarmar / silenciar enviado"; }
        if ((released & GamepadButtons.Back) != 0 && !backLong) { TogglePanel(); return; }
        if ((pressed & GamepadButtons.Start) != 0) { PlayStop(); return; }
        if (neutral)
        {
            if ((g.Buttons & (Musical | Navigation | GamepadButtons.Back | GamepadButtons.Start)) == 0
                && g.RightTrigger <= 70 && g.LeftTrigger <= 30
                && ControllerReader.ThumbPercent(g.LeftThumbX, Gamepad.LeftThumbDeadZone) == 0
                && ControllerReader.ThumbPercent(g.RightThumbX, Gamepad.RightThumbDeadZone) == 0) neutral = false;
            return;
        }
        if (opening) return;
        if (PanelOpen) { PanelInput(g, pressed, released); return; }
        if (!CanPlay) return;
        foreach (var b in Enum.GetValues<GamepadButtons>())
            if ((released & b & Musical) != 0) preset.Button(b, false);
        if ((pressed & GamepadButtons.RightShoulder) != 0) preset.Button(GamepadButtons.RightShoulder, true);
        preset.RightTrigger(g.RightTrigger);
        foreach (var b in new[] { GamepadButtons.A, GamepadButtons.B, GamepadButtons.X, GamepadButtons.Y, GamepadButtons.LeftShoulder })
            if ((pressed & b) != 0) preset.Button(b, true);
        if ((pressed & (GamepadButtons.DPadLeft | GamepadButtons.DPadRight)) != 0)
        {
            if ((g.Buttons & Musical) != 0 || g.RightTrigger > 70)
                Message = "Banco recusado: solte as entradas musicais e pressione o direcional novamente";
            else { preset.SelectBank((pressed & GamepadButtons.DPadLeft) != 0 ? 0 : 1); neutral = true; }
        }
        if ((pressed & GamepadButtons.DPadUp) != 0) preset.ChangeDivision(1);
        if ((pressed & GamepadButtons.DPadDown) != 0) preset.ChangeDivision(-1);
        preset.Analog(g);
    }
    private void PanelInput(Gamepad g, GamepadButtons pressed, GamepadButtons released)
    {
        if ((pressed & GamepadButtons.B) != 0) { TogglePanel(); return; }
        if ((pressed & Navigation) != 0)
        {
            armAt = 0; armDone = true;
            if ((pressed & GamepadButtons.DPadUp) != 0) row = (row + 7) % 8;
            if ((pressed & GamepadButtons.DPadDown) != 0) row = (row + 1) % 8;
            int delta = (pressed & GamepadButtons.DPadLeft) != 0 ? -1 : (pressed & GamepadButtons.DPadRight) != 0 ? 1 : 0;
            if (row == 0) candidateMode = (candidateMode + delta + 3) % 3;
            if (row == 3) candidatePattern = Math.Clamp(candidatePattern + delta, 1, 999);
        }
        if ((pressed & GamepadButtons.A) != 0)
        {
            armAt = now(); armDone = false;
            if (row != 1) Confirm();
        }
        if ((released & GamepadButtons.A) != 0) { armAt = 0; armDone = true; }
        if (row == 1 && (g.Buttons & GamepadButtons.A) != 0 && !armDone && now() - armAt >= .6)
        {
            armDone = true;
            if (!CanPlay || fl.State!.Playing || !RecordingPrepared || !fl.State.Loop || !fl.State.Precount
                || !fl.State.PatternMode || fl.State.Beats != 16)
            { Message = "Prepare canal, 4 compassos, Loop, precontagem, Blend e filtro Notes antes de armar"; return; }
            fl.Command(104, fl.State.Recording ? 0 : 1);
            Message = "Aguarde REC confirmado; Menu inicia a tomada";
        }
    }
    private void Confirm()
    {
        switch (row)
        {
            case 0: SelectMode((MusicalMode)candidateMode); break;
            case 2:
                if (fl.Ready && (fl.State!.Capabilities & 4) != 0) fl.Command(107, fl.State.Metronome ? 0 : 1);
                break;
            case 3: PatternCommand(106, candidatePattern, 32); break;
            case 4: PatternCommand(113, 1, 2); break;
            case 5: preset.SetFullLevel(!preset.Settings.FullLevel); break;
            case 6: preset.ChangeDivision((int)preset.Settings.RepeatDivision == 6 ? -6 : 1); break;
            case 7: PatternCommand(109, 1, 8); break;
        }
    }
    private void PatternCommand(byte command, int value, int capability)
    {
        if (!fl.Ready || fl.State!.Playing || fl.State.Recording || (fl.State.Capabilities & capability) == 0)
        { Message = "Operação requer FL parado/desarmado e API compatível"; return; }
        preset.Silence(); neutral = true; fl.Command(command, value);
        Message = "Comando enviado uma vez; aguarde confirmação do FL";
    }
}
