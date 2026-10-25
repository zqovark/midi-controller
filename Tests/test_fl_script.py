"""Protocol/API contract tests with fakes; not evidence of FL integration."""
import importlib.util
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import Mock


class ScriptTests(unittest.TestCase):
    def setUp(self):
        self.playing = self.recording = self.metro = self.loop = self.precount = False
        self.pattern, self.channel, self.mode = 1, 0, 1
        self.length = 4
        self.native = [(60, 100), (64, 90)]
        self.patterns_data = {1: list(self.native), 2: [(36, 110)]}
        modules = {name: types.ModuleType(name) for name in ('channels', 'device', 'general', 'midi', 'mixer', 'patterns', 'transport', 'ui')}
        sys.modules.update(modules)
        self.c, self.d, self.g, self.m, self.x, self.p, self.t, self.u = [modules[n] for n in modules]
        self.m.MIDI_CONTROLCHANGE=0xB0
        self.m.SS_Start=2
        self.m.PME_System=2
        self.m.GT_Global=8
        self.m.widChannelRack=1
        self.m.FPT_Play=10
        self.m.FPT_Stop=11
        self.m.FPT_Record=12
        self.m.FPT_Metronome=110
        self.m.FPT_LoopRecord=113
        self.m.FPT_CountDown=115
        self.g.getVersion=lambda:45
        self.g.safeToEdit=lambda:True
        self.c.channelCount=lambda global_count:3
        self.c.selectedChannel=lambda *args:self.channel
        self.c.selectOneChannel=lambda index, global_index:setattr(self,'channel',index)
        self.c.setShuffle=Mock()
        self.d.isAssigned=lambda:True
        self.d.midiOutMsg=Mock()
        self.d.dispatchReceiverCount=lambda:1
        self.d.dispatchGetReceiverPortNumber=lambda index:11
        self.d.dispatch=lambda index,message:self.d.midiOutMsg((message & ~255) | 0xBE)
        self.x.getCurrentTempo=Mock(side_effect=lambda as_int:90500 if as_int else 90.5)
        self.t.isPlaying=lambda:self.playing
        self.t.isRecording=lambda:self.recording
        self.t.start=Mock(side_effect=lambda:setattr(self,'playing',True))
        self.t.stop=Mock(side_effect=lambda:setattr(self,'playing',False))
        self.t.record=Mock(side_effect=lambda:setattr(self,'recording',not self.recording))
        self.t.getLoopMode=lambda:self.mode
        self.t.setLoopMode=lambda:setattr(self,'mode',1-self.mode)
        def global_transport(command, value, flags, scope):
            self.assertEqual(scope,8)
            self.assertEqual(value,1)  # Button press; SS_Start=2 is for movement commands.
            self.assertNotEqual(command,11)  # Native stop(), global-only Stop did not stop FL 26.
            if command == 10:
                self.t.start();return
            if command == 11:
                self.t.stop();return
            if command == 12:
                self.t.record();return
            field={110:'metro',113:'loop',115:'precount'}[command]
            setattr(self,field,not getattr(self,field))
        self.t.globalTransport=Mock(side_effect=global_transport)
        self.u.isMetronomeEnabled=lambda:self.metro
        self.u.isLoopRecEnabled=lambda:self.loop
        self.u.isPrecountEnabled=lambda:self.precount
        self.u.getVersion=lambda:'26.1.7'
        self.u.setHintMsg=Mock()
        self.u.setFocused=Mock()
        self.p.patternNumber=lambda:self.pattern
        self.p.patternMax=lambda:999
        self.p.getPatternLength=lambda p:self.length
        self.p.setPatternLength=lambda p,length:setattr(self,'length',length)
        self.p.jumpToPattern=lambda p:setattr(self,'pattern',p)
        self.p.isPatternDefault=lambda p:p not in self.patterns_data
        self.p.clonePattern=Mock(side_effect=lambda a,b:self.patterns_data.update({b:list(self.patterns_data[a])}))
        path=Path(__file__).resolve().parents[1]/'FL-Studio'/'X-MIDI'/'device_XMIDI.py'
        spec=importlib.util.spec_from_file_location('xmidi_script',path)
        self.script=importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.script)

    def event(self,cc,value,channel=15,kind=0xB0):
        e=types.SimpleNamespace(midiId=kind,midiChan=channel,data1=cc,data2=value,pmeFlags=2,handled=False)
        self.script.OnMidiMsg(e)
        return e

    def command(self,cc,value):
        self.event(112,17)
        self.event(108,value>>7)
        self.event(cc,value&127)
        self.event(119,17)

    def test_record_play_stop_desired_states(self):
        self.command(104,1);self.command(104,1)
        self.assertTrue(self.recording);self.t.record.assert_called_once()
        self.command(102,1);self.command(102,1)
        self.assertTrue(self.playing);self.t.start.assert_called_once()
        self.command(102,0)
        self.assertFalse(self.playing);self.assertFalse(self.recording)
        self.command(102,0);self.assertEqual(self.t.record.call_count,2)

    def test_old_toggle_client_and_duplicate_execution_blocked(self):
        self.event(102,127);self.event(104,1)
        self.assertFalse(self.playing or self.recording)
        self.command(113,1);self.event(113,1)
        self.p.clonePattern.assert_called_once()

    def test_clone_preserves_source_and_occupied_destination(self):
        self.command(113,1)
        self.p.clonePattern.assert_called_once_with(1,3)
        self.assertEqual(self.pattern,3)
        self.assertEqual(self.patterns_data[1],self.native)
        self.assertEqual(self.patterns_data[2],[(36,110)])
        self.assertEqual(self.patterns_data[3],self.native)

    def test_unsupported_clone_and_recording_refuse(self):
        self.g.getVersion=lambda:42
        self.command(113,1);self.p.clonePattern.assert_not_called()
        self.g.getVersion=lambda:45
        self.recording=True
        self.command(113,1);self.p.clonePattern.assert_not_called()

    def test_channel_pattern_metronome_and_preparation(self):
        self.command(105,2);self.assertEqual(self.channel,2)
        self.command(105,9);self.assertEqual(self.channel,2)
        self.command(106,140);self.assertEqual(self.pattern,140)
        self.command(107,1);self.command(107,1)
        self.assertTrue(self.metro);self.assertEqual(self.t.globalTransport.call_count,1)
        self.command(109,1);self.command(109,1)
        self.assertTrue(self.loop and self.precount)
        self.assertEqual(self.mode,0);self.assertEqual(self.length,16)
        self.assertEqual(self.t.globalTransport.call_count,3)

    def test_snapshot_and_external_changes(self):
        self.playing=True;self.recording=True;self.pattern=300;self.channel=2
        self.event(119,73)
        data=[call.args[0] for call in self.d.midiOutMsg.call_args_list]
        self.assertEqual(len(data),15)
        self.assertTrue(all(m&255==0xBE for m in data))
        values={m>>8&127:m>>16&127 for m in data}
        self.assertEqual(values[110],73);self.assertEqual(values[111],73)
        self.assertEqual(values[1]&3,3)
        self.assertEqual(values[3]+128*values[4],905)
        self.x.getCurrentTempo.assert_called_with(0)
        self.assertEqual(values[7]+128*values[8],300)
        self.assertEqual(values[5]+128*values[6],3)

    def test_native_notes_effects_and_feedback_never_consumed(self):
        for kind in (0x80,0x90):
            for channel in range(16):
                self.assertFalse(self.event(60,100,channel,kind).handled)
        for channel in (0,14):
            self.assertFalse(self.event(102,1,channel).handled)
        self.assertFalse(self.event(74,64,0).handled)
        self.assertFalse(self.event(120,0,0).handled)

    def test_tempo_units_observed_in_fl_26(self):
        for raw, expected in ((140000, 1400), (90500, 905), (90.5, 905)):
            with self.subTest(raw=raw):
                self.x.getCurrentTempo=Mock(return_value=raw)
                self.d.midiOutMsg.reset_mock()
                self.event(119,73)
                values={call.args[0]>>8&127:call.args[0]>>16&127
                        for call in self.d.midiOutMsg.call_args_list}
                self.assertEqual(values[3]+128*values[4],expected)

    def test_feedback_port_unavailable_then_reconnected(self):
        self.d.dispatchReceiverCount=lambda:0
        self.event(119,17)
        self.d.midiOutMsg.assert_not_called()
        self.d.dispatchReceiverCount=lambda:1
        self.event(119,18)
        self.assertEqual(self.d.midiOutMsg.call_count,15)
        self.assertFalse(self.playing or self.recording)

    def test_return_script_does_not_echo_physical_feedback(self):
        path=Path(__file__).resolve().parents[1]/'FL-Studio'/'X-MIDI'/'device_XMIDI_Return.py'
        spec=importlib.util.spec_from_file_location('xmidi_return',path)
        receiver=importlib.util.module_from_spec(spec);spec.loader.exec_module(receiver)
        for status in (0xBE,0x90,0x80):
            event=types.SimpleNamespace(status=status,data1=60,data2=100,handled=False)
            receiver.OnMidiIn(event)
            self.assertTrue(event.handled)
        self.d.midiOutMsg.assert_not_called()
        event=types.SimpleNamespace(status=0xBD,data1=110,data2=17,handled=False)
        receiver.OnMidiIn(event);receiver.OnMidiMsg(event)
        self.d.midiOutMsg.assert_called_once_with(0xBE | (110<<8) | (17<<16))

    def test_watchdog_stops_and_disarms_once(self):
        self.script.time=types.SimpleNamespace(monotonic=lambda:1)
        self.event(119,1);self.playing=self.recording=True
        self.script.time.monotonic=lambda:3.1
        self.script.OnIdle();self.script.OnIdle()
        self.assertFalse(self.playing or self.recording)
        self.t.stop.assert_called_once();self.t.record.assert_called_once()

    def test_swing_and_native_fl_alias(self):
        for percent in range(101):
            self.event(103,round(percent*127/100))
            self.c.setShuffle.assert_called_with(percent/100)
        del self.c.setShuffle
        self.c.setSwing=Mock();self.event(103,6)
        self.c.setSwing.assert_called_once_with(.05)


if __name__=='__main__':
    unittest.main()
