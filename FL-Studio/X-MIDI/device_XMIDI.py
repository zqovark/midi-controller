# name=X-MIDI
# supportedDevices=X-MIDI
"""Protocol 2. Notes remain native; feedback uses a distinct output, MIDI channel 15."""
import channels
import device
import general
import midi
import mixer
import patterns
import transport
import ui
import time
_token = None
_high = 0
_error = 0
_last_request = 0.0
_watchdog = False



def _has(module, *names):
    return all(callable(getattr(module, name, None)) for name in names)


def capabilities():
    api = general.getVersion()
    result = 1
    if api >= 43 and _has(patterns, 'clonePattern', 'isPatternDefault', 'patternMax'):
        result |= 2
    if _has(ui, 'isMetronomeEnabled') and hasattr(midi, 'FPT_Metronome'):
        result |= 4
    if api >= 39 and _has(patterns, 'setPatternLength') and _has(ui, 'isLoopRecEnabled', 'isPrecountEnabled'):
        result |= 8
    if api >= 33 and _has(channels, 'selectOneChannel', 'selectedChannel'):
        result |= 16
    if _has(patterns, 'jumpToPattern'):
        result |= 32
    return result


def _send(cc, value, receiver):
    device.dispatch(receiver, 0xBD | (cc << 8) | ((int(value) & 127) << 16))


def tempo_bpm():
    tempo = mixer.getCurrentTempo(0)
    # FL 26.1.7 / API 45 was observed returning milli-BPM (140000 at 140 BPM).
    # Other hosts expose the documented BPM float. FL's tempo range is below 1000.
    return tempo / 1000.0 if tempo > 1000 else tempo


def snapshot(token):
    receiver = next((i for i in range(device.dispatchReceiverCount())
                     if device.dispatchGetReceiverPortNumber(i) == 11), None)
    if receiver is None:
        return
    flags = (int(bool(transport.isPlaying())) | int(bool(transport.isRecording())) << 1
             | int(bool(ui.isMetronomeEnabled())) << 2 | int(bool(ui.isLoopRecEnabled())) << 3
             | int(bool(ui.isPrecountEnabled())) << 4 | int(transport.getLoopMode() == 0) << 5)
    bpm = max(0, min(16383, round(tempo_bpm() * 10)))
    channel = channels.selectedChannel(1, 0, 1) + 1
    pattern = patterns.patternNumber()
    beats = int(patterns.getPatternLength(pattern))
    values = [2, flags, capabilities(), bpm & 127, bpm >> 7, channel & 127, channel >> 7,
              pattern & 127, pattern >> 7, min(127, general.getVersion()), _error, beats & 127, beats >> 7]
    _send(110, token, receiver)
    for cc, value in enumerate(values):
        _send(cc, value, receiver)
    _send(111, token, receiver)


def _global(command, event=None):
    # Transport helpers can follow plugin focus (e.g. Edison). Force the FL transport.
    transport.globalTransport(command, 1,
                              event.pmeFlags if event is not None else midi.PME_System,
                              midi.GT_Global)


def stop(event=None):
    ui.setFocused(midi.widChannelRack)
    transport.stop()
    if transport.isRecording():
        transport.record()


def _stopped():
    if transport.isPlaying() or transport.isRecording():
        raise RuntimeError('Pare e desarme antes desta operacao')


def _toggle_to(getter, command, desired, event):
    if bool(getter()) != bool(desired):
        _global(command, event)


def _execute(cc, value, event):
    caps = capabilities()
    if cc == 102:
        if value == 0:
            stop(event)
        elif value == 1 and not transport.isPlaying():
            ui.setFocused(midi.widChannelRack)
            transport.start()
    elif cc == 104:
        if value and transport.isPlaying():
            raise RuntimeError('Arme antes de iniciar')
        if bool(transport.isRecording()) != bool(value):
            ui.setFocused(midi.widChannelRack)
            transport.record()
    elif cc == 105:
        _stopped()
        if not caps & 16 or not 0 <= value < channels.channelCount(1):
            raise RuntimeError('Canal indisponivel')
        channels.selectOneChannel(value, True)
    elif cc == 106:
        _stopped()
        if not caps & 32 or not 1 <= value <= patterns.patternMax():
            raise RuntimeError('Pattern indisponivel')
        patterns.jumpToPattern(value)
    elif cc == 107:
        if not caps & 4:
            raise RuntimeError('Metronomo indisponivel')
        _toggle_to(ui.isMetronomeEnabled, midi.FPT_Metronome, value, event)
    elif cc == 109:
        _stopped()
        if not caps & 8:
            raise RuntimeError('Preparacao indisponivel')
        patterns.setPatternLength(patterns.patternNumber(), 16)
        if transport.getLoopMode() != 0:
            transport.setLoopMode()
        _toggle_to(ui.isLoopRecEnabled, midi.FPT_LoopRecord, True, event)
        _toggle_to(ui.isPrecountEnabled, midi.FPT_CountDown, True, event)
    elif cc == 113:
        _stopped()
        if not caps & 2 or not general.safeToEdit():
            raise RuntimeError('Clonagem segura indisponivel')
        source = patterns.patternNumber()
        # Explicit destination must be empty AND untouched, never a user pattern.
        destination = next((p for p in range(source + 1, patterns.patternMax() + 1)
                            if patterns.isPatternDefault(p)), None)
        if destination is None:
            raise RuntimeError('Nenhum pattern vazio depois da origem')
        patterns.clonePattern(source, destination)
        patterns.jumpToPattern(destination)


def OnMidiMsg(event):
    global _token, _high, _error, _last_request, _watchdog
    if event.midiId != midi.MIDI_CONTROLCHANGE or event.midiChan != 15:
        return  # In particular, never consume/replace native Note On/Off.
    cc, value = event.data1, event.data2
    if cc not in (102, 103, 104, 105, 106, 107, 108, 109, 112, 113, 119):
        return
    event.handled = True
    if cc == 112:
        _token, _high, _error = value, 0, 0
        return
    if cc == 108:
        _high = value
        return
    if cc == 119:
        _last_request = time.monotonic()
        _watchdog = True
        try:
            snapshot(value)
        except RuntimeError as error:
            print('X-MIDI: ' + str(error))
        return
    if cc == 103:
        setter = getattr(channels, 'setShuffle', None) or getattr(channels, 'setSwing', None)
        if setter is not None:
            setter(round(value * 100 / 127) / 100.0)
        else:
            ui.setHintMsg('X-MIDI: API de swing indisponível nesta versão do FL.')
        return
    if _token is None:
        return  # Old toggle-only clients cannot accidentally start/arm transport.
    _token = None  # Consume once, even if the API raises. Never retry a toggle/clone.
    try:
        _execute(cc, value + _high * 128, event)
    except Exception as error:
        _error = 1
        ui.setHintMsg('X-MIDI: ' + str(error))
    finally:
        _high = 0


def OnIdle():
    global _watchdog
    if _watchdog and time.monotonic() - _last_request > 2.0:
        _watchdog = False
        stop()  # App/input failure must not leave recording running.


def OnInit():
    print('X-MIDI protocol 2 | API {} | BPM {} | feedback receivers {}'.format(
        general.getVersion(), tempo_bpm(), device.dispatchReceiverCount()))


def OnDeInit():
    if _watchdog:
        stop()
