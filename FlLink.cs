namespace XMidi;

internal sealed record FlState(bool Playing, bool Recording, bool Metronome, bool Loop, bool Precount,
    bool PatternMode, double Bpm, int Channel, int Pattern, int Api, int Capabilities, int Beats, int Error);

// Channel 16 is commands; channel 15 is feedback, on a separate physical MIDI port.
// Complete, token-matched snapshots only. Queries can repeat; commands never retry.
internal sealed class FlLink(Action<byte, byte, byte> send, Func<double> now)
{
    private int sequence, expected = -1, frame = -1, seen;
    private readonly byte[] values = new byte[13];
    private double requested, confirmed = double.NegativeInfinity, nextQuery;
    private bool pendingCommand;
    public FlState? State { get; private set; }
    public bool Connected { get; private set; }
    public bool Fresh => Connected && State is not null && now() - confirmed < 1.5;
    public bool Ready => Fresh && !pendingCommand;
    public string Notice { get; private set; } = "Retorno FL desconhecido";
    public void Connect(bool connected)
    {
        Connected = connected; State = null; confirmed = double.NegativeInfinity;
        expected = frame = -1; pendingCommand = false; nextQuery = 0;
        Notice = "Aguardando retorno FL";
        if (connected) Command(102, 0, safety: true); // Desired Stop/disarm, never a toggle.
    }
    public void Tick()
    {
        if (!Connected) return;
        if (expected >= 0 && now() - requested >= 1.5)
        {
            if (pendingCommand) { State = null; Notice = "Comando sem confirmação; não será repetido"; }
            pendingCommand = false; expected = frame = -1;
        }
        if (expected < 0 && now() >= nextQuery) Request(null, 0);
    }
    public bool Command(byte command, int value, bool safety = false)
    {
        if (!Connected || (!safety && !Ready)) { Notice = "Aguarde confirmação do FL"; return false; }
        Request(command, Math.Clamp(value, 0, 16383));
        return true;
    }
    private void Request(byte? command, int value)
    {
        expected = sequence = sequence % 127 + 1;
        requested = now(); nextQuery = requested + .4; pendingCommand = command.HasValue;
        frame = -1;
        if (command.HasValue)
        {
            send(0xBF, 112, (byte)expected);
            send(0xBF, 108, (byte)(value >> 7));
            send(0xBF, command.Value, (byte)(value & 127));
        }
        send(0xBF, 119, (byte)expected);
    }
    public void Receive(byte status, byte cc, byte value)
    {
        if (!Connected || status != 0xBE) return;
        if (cc == 110) { frame = value == expected ? value : -1; seen = 0; return; }
        if (frame < 0) return;
        if (cc < values.Length) { values[cc] = value; seen |= 1 << cc; return; }
        if (cc != 111) return;
        if (value == frame && seen == (1 << values.Length) - 1 && values[0] == 2)
        {
            int flags = values[1];
            State = new((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0,
                (flags & 8) != 0, (flags & 16) != 0, (flags & 32) != 0,
                (values[3] + values[4] * 128) / 10.0, values[5] + values[6] * 128 - 1,
                values[7] + values[8] * 128, values[9], values[2], values[11] + values[12] * 128, values[10]);
            confirmed = now(); pendingCommand = false; expected = -1;
            Notice = State.Error == 0 ? "FL confirmado" : $"FL recusou comando (código {State.Error})";
        }
        frame = -1;
    }
}
