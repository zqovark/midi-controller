namespace XMidi;

// Ownership only; all bytes still travel through the single existing MidiOutput.
internal sealed class MidiNoteOwnership(Action<byte, byte, byte> send)
{
    private readonly object gate = new();
    private readonly Dictionary<(int Channel, byte Note), HashSet<int>> owners = [];
    public void Send(int source, byte status, byte note, byte velocity)
    {
        lock (gate)
        {
            var key = (status & 15, note);
            if ((status & 0xf0) == 0x90 && velocity != 0)
            {
                if (!owners.TryGetValue(key, out var active)) owners[key] = active = [];
                bool first = active.Count == 0;
                if (active.Add(source) && first) send(status, note, velocity);
            }
            else if ((status & 0xf0) == 0x80 || (status & 0xf0) == 0x90)
            {
                if (owners.TryGetValue(key, out var active) && active.Contains(source))
                {
                    if (active.Count == 1) send((byte)(0x80 | key.Item1), note, 0);
                    active.Remove(source); if (active.Count == 0) owners.Remove(key);
                }
            }
            else send(status, note, velocity);
        }
    }
}
