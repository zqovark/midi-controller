# name=X-MIDI Return
# supportedDevices=X-MIDI Return
# receiveFrom=X-MIDI
"""Dedicated feedback endpoint. Consumes physical feedback to prevent MIDI loops."""
import device


def OnMidiIn(event):
    if event.handled:
        return
    event.handled = True
    # Internal dispatch uses channel 14; the wire uses channel 15.
    # A message received back from loopMIDI therefore cannot be sent again.
    if event.status == 0xBD:
        device.midiOutMsg(0xBE | (event.data1 << 8) | (event.data2 << 16))


def OnMidiMsg(event):
    OnMidiIn(event)
