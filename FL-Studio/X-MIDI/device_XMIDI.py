# name=X-MIDI
"""Transporte/swing na mesma entrada MIDI; notas e CC 74/91 seguem o fluxo nativo."""
import channels
import midi
import transport
import ui


def OnMidiMsg(event):
    # Canal 16 é reservado aos comandos. A bateria/efeitos continuam no canal 1.
    if event.midiId != midi.MIDI_CONTROLCHANGE or event.midiChan != 15:
        return
    if event.data1 == 102:
        event.handled = True
        if event.data2 == 127:
            transport.globalTransport(midi.FPT_Play, midi.SS_Start, event.pmeFlags)
    elif event.data1 == 103:
        event.handled = True
        # O manual atual usa setShuffle; FL 2026/API 45 expõe setSwing.
        setter = getattr(channels, "setShuffle", None) or getattr(channels, "setSwing", None)
        if setter is None:
            ui.setHintMsg("X-MIDI: API de swing indisponível nesta versão do FL.")
            return
        # Recupera percentuais inteiros exatos, inclusive os passos padrão de 5%.
        percent = max(0, min(100, round(event.data2 * 100 / 127)))
        setter(percent / 100.0)
        ui.setHintMsg("X-MIDI: swing global do sequenciador {}% (não altera notas ao vivo)".format(percent))
