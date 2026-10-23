using System.Runtime.InteropServices;
using System.Text;

namespace XMidi;

internal sealed record MidiPort(uint Id, string Name)
{
    public override string ToString() => Name;
}

internal sealed class MidiOutput : IDisposable
{
    private readonly object gate = new();
    private nint handle;
    public bool IsOpen { get { lock (gate) return handle != 0; } }
    public event Action<byte, byte, byte>? Sent;
    public static IReadOnlyList<MidiPort> GetPorts()
    {
        var ports = new List<MidiPort>();
        for (uint id = 0; id < midiOutGetNumDevs(); id++)
        {
            Check(midiOutGetDevCapsW(id, out var caps, (uint)Marshal.SizeOf<MidiOutCaps>()));
            ports.Add(new(id, caps.Name));
        }
        return ports;
    }
    public void Open(uint id)
    {
        lock (gate)
        {
            Dispose();
            Check(midiOutOpen(out handle, id, 0, 0, 0));
        }
    }
    public void Send(byte status, byte data1, byte data2)
    {
        lock (gate)
        {
            if (handle == 0) return;
            Check(midiOutShortMsg(handle, (uint)(status | data1 << 8 | data2 << 16)));
            Sent?.Invoke(status, data1, data2);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (handle == 0) return;
            midiOutReset(handle);
            midiOutClose(handle);
            handle = 0;
        }
    }
    private static void Check(uint result)
    {
        if (result == 0) return;
        var text = new StringBuilder(256);
        midiOutGetErrorTextW(result, text, (uint)text.Capacity);
        throw new InvalidOperationException($"MIDI: {text} (código {result})");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiOutCaps
    {
        public ushort Manufacturer, Product;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public ushort Technology, Voices, Notes, ChannelMask;
        public uint Support;
    }
    [DllImport("winmm.dll")] private static extern uint midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiOutGetDevCapsW(nuint device, out MidiOutCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiOutOpen(out nint handle, uint device, nuint callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiOutShortMsg(nint handle, uint message);
    [DllImport("winmm.dll")] private static extern uint midiOutReset(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiOutClose(nint handle);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiOutGetErrorTextW(uint error, StringBuilder text, uint length);
}
