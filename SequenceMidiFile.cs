using System.IO;
using System.Text;

namespace XMidi;

internal static class SequenceMidiFile
{
    public static void Export(string file, SequencePattern pattern, PadMapping[][] banks, int swing)
    {
        pattern.Validate(); var mappings = banks.SelectMany(b => b).ToArray();
        if (mappings.Length != 8 || mappings.Any(p => p.Note is < 0 or > 127)) throw new InvalidDataException("Use os mapeamentos atuais do Drum Mode.");
        string full = Path.GetFullPath(file), temporary = full + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new BinaryWriter(File.Create(temporary)))
            {
                output.Write(Encoding.ASCII.GetBytes("MThd")); Big32(output, 6); Big16(output, 1); Big16(output, 9); Big16(output, pattern.Ppq);
                WriteTrack(output, writer =>
                {
                    Meta(writer, 0, 3, Encoding.UTF8.GetBytes(pattern.Name));
                    int tempo = (int)Math.Round(60_000_000 / pattern.Bpm);
                    Meta(writer, 0, 0x51, [(byte)(tempo >> 16), (byte)(tempo >> 8), (byte)tempo]);
                    Meta(writer, 0, 0x58, [(byte)pattern.Numerator, (byte)Math.Log2(pattern.Denominator), 24, 8]);
                    Meta(writer, pattern.Length, 0x2f, []);
                });
                foreach (var track in pattern.Tracks.OrderBy(t => t.Instrument))
                {
                    WriteTrack(output, writer =>
                    {
                        Meta(writer, 0, 3, Encoding.UTF8.GetBytes(mappings[track.Instrument].Name));
                        var events = track.Events.SelectMany(n =>
                        {
                            long tick = SequenceTiming.PlayTick(n, pattern, swing);
                            return new[] { (Tick: tick, Status: (byte)0x90, Velocity: (byte)n.Velocity), (Tick: tick + n.Duration, Status: (byte)0x80, Velocity: (byte)0) };
                        }).OrderBy(e => e.Tick).ThenBy(e => e.Status).ToArray();
                        long previous = 0;
                        foreach (var e in events)
                        {
                            Variable(writer, e.Tick - previous); writer.Write(e.Status); writer.Write((byte)mappings[track.Instrument].Note); writer.Write(e.Velocity); previous = e.Tick;
                        }
                        Meta(writer, Math.Max(0, pattern.Length - previous), 0x2f, []);
                    });
                }
            }
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteTrack(BinaryWriter output, Action<BinaryWriter> write)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);
        write(writer); writer.Flush(); output.Write(Encoding.ASCII.GetBytes("MTrk")); Big32(output, checked((int)memory.Length)); output.Write(memory.ToArray());
    }
    private static void Meta(BinaryWriter writer, long delta, byte type, byte[] bytes)
    { Variable(writer, delta); writer.Write((byte)0xff); writer.Write(type); Variable(writer, bytes.Length); writer.Write(bytes); }
    private static void Big16(BinaryWriter writer, int value) { writer.Write((byte)(value >> 8)); writer.Write((byte)value); }
    private static void Big32(BinaryWriter writer, int value)
    { writer.Write((byte)(value >> 24)); writer.Write((byte)(value >> 16)); writer.Write((byte)(value >> 8)); writer.Write((byte)value); }
    private static void Variable(BinaryWriter writer, long value)
    {
        if (value is < 0 or > 0x0fffffff) throw new InvalidDataException("Delta MIDI fora dos limites VLQ.");
        Span<byte> buffer = stackalloc byte[4]; int index = 3; buffer[index] = (byte)(value & 127);
        while ((value >>= 7) > 0) buffer[--index] = (byte)((value & 127) | 128);
        for (; index < 4; index++) writer.Write(buffer[index]);
    }
}
