"""Testes do protocolo; não substituem a execução no FL Studio real."""
import importlib.util
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import Mock


class ScriptTests(unittest.TestCase):
    def setUp(self):
        self.channels = types.ModuleType("channels")
        self.channels.setShuffle = Mock()
        self.transport = types.ModuleType("transport")
        self.transport.globalTransport = Mock()
        self.ui = types.ModuleType("ui")
        self.ui.setHintMsg = Mock()
        midi = types.ModuleType("midi")
        midi.MIDI_CONTROLCHANGE = 0xB0
        midi.FPT_Play = 10
        midi.SS_Start = 2
        for name, value in (("channels", self.channels), ("transport", self.transport), ("ui", self.ui), ("midi", midi)):
            sys.modules[name] = value
        path = Path(__file__).resolve().parents[1] / "FL-Studio" / "X-MIDI" / "device_XMIDI.py"
        spec = importlib.util.spec_from_file_location("xmidi_script", path)
        self.script = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.script)

    def event(self, controller, value, channel=15, kind=0xB0):
        event = types.SimpleNamespace(midiId=kind, midiChan=channel, data1=controller, data2=value, pmeFlags=2, handled=False)
        self.script.OnMidiMsg(event)
        return event

    def test_transport_only_on_command_press(self):
        self.assertTrue(self.event(102, 127).handled)
        self.event(102, 0)
        self.transport.globalTransport.assert_called_once_with(10, 2, 2)

    def test_swing_percentages_and_limits(self):
        for percent in range(101):
            self.assertTrue(self.event(103, round(percent * 127 / 100)).handled)
            self.channels.setShuffle.assert_called_with(percent / 100)

    def test_native_notes_and_effects_remain_unhandled(self):
        for cc in (74, 91, 102, 103):
            self.assertFalse(self.event(cc, 127, channel=0).handled)
        self.assertFalse(self.event(36, 110, channel=0, kind=0x90).handled)
        self.channels.setShuffle.assert_not_called()
        self.transport.globalTransport.assert_not_called()

    def test_unsupported_swing_reports_limitation(self):
        del self.channels.setShuffle
        self.assertTrue(self.event(103, 64).handled)
        self.assertIn("indisponível", self.ui.setHintMsg.call_args.args[0])

    def test_fl_2026_native_method(self):
        del self.channels.setShuffle
        self.channels.setSwing = Mock()
        self.event(103, 6)
        self.channels.setSwing.assert_called_once_with(0.05)


if __name__ == "__main__":
    unittest.main()
