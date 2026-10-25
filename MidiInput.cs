using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace XMidi;

// WinMM callbacks only enqueue. Parsing and UI callbacks run on the consuming thread.
internal sealed class MidiInput : IDisposable
{
    private readonly ConcurrentQueue<uint> messages = new();
    private readonly MidiCallback callback;
    private nint handle;
    public bool IsOpen => handle != 0;
    public MidiInput() => callback = Receive;
    public static IReadOnlyList<MidiPort> GetPorts()
    {
        var ports = new List<MidiPort>();
        for (uint id = 0; id < midiInGetNumDevs(); id++)
        {
            Check(midiInGetDevCapsW(id, out var caps, (uint)Marshal.SizeOf<Caps>()));
            ports.Add(new(id, caps.Name));
        }
        return ports;
    }
    public void Open(uint id)
    {
        Dispose();
        Check(midiInOpen(out handle, id, callback, 0, 0x30000));
        try { Check(midiInStart(handle)); }
        catch { Dispose(); throw; }
    }
    private void Receive(nint device, uint message, nuint instance, nuint data, nuint timestamp)
    {
        if (message == 0x3C3 && messages.Count < 4096) messages.Enqueue((uint)data);
    }
    public void Drain(Action<byte, byte, byte> receive)
    {
        for (int i = 0; i < 4096 && messages.TryDequeue(out uint data); i++)
            receive((byte)data, (byte)((data >> 8) & 127), (byte)((data >> 16) & 127));
    }
    public void Dispose()
    {
        if (handle != 0)
        { midiInStop(handle); midiInReset(handle); midiInClose(handle); handle = 0; }
        messages.Clear();
        GC.KeepAlive(callback);
    }
    private static void Check(uint code)
    { if (code != 0) throw new InvalidOperationException($"Entrada MIDI WinMM: erro {code}."); }
    private delegate void MidiCallback(nint device, uint message, nuint instance, nuint data, nuint timestamp);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Caps
    {
        public ushort Manufacturer, Product;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Support;
    }
    [DllImport("winmm.dll")] private static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint midiInGetDevCapsW(nuint id, out Caps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint midiInOpen(out nint handle, uint id, MidiCallback callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint midiInStart(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiInStop(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiInReset(nint handle);
    [DllImport("winmm.dll")] private static extern uint midiInClose(nint handle);
}
