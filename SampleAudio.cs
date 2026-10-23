using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XMidi;

internal sealed record SampleAudio(int Rate, int Channels, float[] Samples, string Hash)
{
    public int Frames => Samples.Length / Channels;
    public double Seconds => Frames / (double)Rate;
}
internal static class SampleProcessor
{
    private const int MaximumSamples = 32_000_000;
    public static SampleAudio Load(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 128 * 1024 * 1024) throw new InvalidDataException("WAV limitado a 128 MiB para edição offline.");
        string hash = Convert.ToHexString(SHA256.HashData(file)); file.Position = 0;
        using var reader = new BinaryReader(file, Encoding.ASCII, leaveOpen: true);
        if (file.Length < 12 || new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Selecione um WAV RIFF (PCM ou IEEE float).");
        long limit = reader.ReadUInt32() + 8L;
        if (limit > file.Length || new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Cabeçalho WAV inválido.");
        int format = 0, channels = 0, rate = 0, bits = 0, align = 0;
        long dataAt = 0; int dataBytes = 0;
        while (file.Position + 8 <= limit)
        {
            string id = new(reader.ReadChars(4)); uint size = reader.ReadUInt32(); long start = file.Position;
            if (size > int.MaxValue || start + size > limit) throw new InvalidDataException("Chunk WAV truncado.");
            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("Formato WAV truncado.");
                format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadInt32(); align = reader.ReadUInt16(); bits = reader.ReadUInt16();
                if (format == 0xfffe)
                {
                    if (size < 40 || reader.ReadUInt16() < 22) throw new InvalidDataException("WAV extensível inválido.");
                    int validBits = reader.ReadUInt16(); reader.ReadUInt32();
                    var sub = new Guid(reader.ReadBytes(16));
                    format = sub == new Guid("00000001-0000-0010-8000-00aa00389b71") ? 1
                        : sub == new Guid("00000003-0000-0010-8000-00aa00389b71") ? 3 : 0;
                    if (validBits != 0 && validBits != bits) throw new InvalidDataException("WAV com bits válidos diferentes do contêiner não suportado.");
                }
            }
            else if (id == "data" && dataAt == 0) { dataAt = start; dataBytes = (int)size; }
            file.Position = start + size + (size & 1);
        }
        if (channels is < 1 or > 2 || rate is < 8000 or > 384000 || dataAt == 0 || dataBytes == 0
            || !(format == 1 && bits is 8 or 16 or 24 or 32 || format == 3 && bits is 32 or 64)
            || align != channels * (bits / 8) || dataBytes % align != 0)
            throw new InvalidDataException("Use WAV mono/estéreo PCM 8/16/24/32 bits ou float 32/64 bits, 8–384 kHz.");
        int count = dataBytes / (bits / 8);
        if (count > MaximumSamples) throw new InvalidDataException("Sample excede 32 milhões de amostras decodificadas.");
        var samples = new float[count]; file.Position = dataAt;
        for (int i = 0; i < count; i++)
        {
            double value = (format, bits) switch
            {
                (1, 8) => (reader.ReadByte() - 128) / 128.0,
                (1, 16) => reader.ReadInt16() / 32768.0,
                (1, 24) => Read24(reader) / 8388608.0,
                (1, 32) => reader.ReadInt32() / 2147483648.0,
                (3, 32) => reader.ReadSingle(), (3, 64) => reader.ReadDouble(), _ => 0
            };
            if (!double.IsFinite(value)) throw new InvalidDataException("WAV contém amostras não finitas.");
            samples[i] = (float)Math.Clamp(value, -1, 1);
        }
        return new(rate, channels, samples, hash);
    }
    private static int Read24(BinaryReader reader)
    {
        int raw = reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16;
        return (raw & 0x800000) == 0 ? raw : raw | unchecked((int)0xff000000);
    }
    public static (int Start, int End) Bounds(SampleAudio audio, SliceRegion region)
    {
        if (!double.IsFinite(region.Start) || !double.IsFinite(region.End) || region.Start < 0 || region.End > 1 || region.Start >= region.End)
            throw new InvalidDataException("Região fora dos limites do WAV.");
        int start = (int)Math.Round(region.Start * audio.Frames), end = (int)Math.Round(region.End * audio.Frames);
        if (start < 0 || end > audio.Frames || end <= start) throw new InvalidDataException("Cada região precisa conter ao menos um frame de áudio.");
        return (start, end);
    }
    public static SliceRegion[] Divide(SampleAudio audio, ChopMethod method, int count, double sensitivity)
    {
        if (count is not (2 or 4 or 8) || audio.Frames < count || !double.IsFinite(sensitivity) || sensitivity is < 0 or > 1)
            throw new InvalidDataException("Escolha 2, 4 ou 8 regiões e sensibilidade de 0–100%.");
        if (method == ChopMethod.Manual) throw new InvalidOperationException("Modo manual preserva os marcadores existentes.");
        if (method != ChopMethod.Transients) return SampleSettings.EqualRegions(count);
        // Positive block-energy flux, a local peak test, and a 50 ms exclusion zone.
        // This is a heuristic: edits remain available and equal boundaries fill missing attacks.
        int block = Math.Max(1, audio.Rate / 200);
        var flux = new List<(int Frame, double Flux)>(); double previous = 0;
        for (int frame = 0; frame < audio.Frames; frame += block)
        {
            double energy = 0; int stop = Math.Min(audio.Frames, frame + block);
            for (int f = frame; f < stop; f++)
                for (int c = 0; c < audio.Channels; c++) energy += Math.Abs(audio.Samples[f * audio.Channels + c]);
            energy /= (stop - frame) * audio.Channels;
            flux.Add((frame, Math.Max(0, energy - previous))); previous = energy;
        }
        double threshold = flux.Max(f => f.Flux) * (.8 - .75 * sensitivity);
        int distance = Math.Max(1, audio.Rate / 20);
        var cuts = new List<int> { 0, audio.Frames };
        foreach (var candidate in flux.Where((f, i) => i > 0 && i < flux.Count - 1 && f.Flux > threshold
                      && f.Flux >= flux[i - 1].Flux && f.Flux > flux[i + 1].Flux).OrderByDescending(f => f.Flux))
        {
            if (cuts.All(c => Math.Abs(c - candidate.Frame) >= distance)) cuts.Add(candidate.Frame);
            if (cuts.Count == count + 1) break;
        }
        while (cuts.Count < count + 1)
        {
            cuts.Sort();
            var gap = cuts.Zip(cuts.Skip(1), (a, b) => (Start: a, End: b)).MaxBy(g => g.End - g.Start);
            cuts.Add((gap.Start + gap.End) / 2);
        }
        cuts.Sort();
        return cuts.Zip(cuts.Skip(1), (a, b) => new SliceRegion { Start = a / (double)audio.Frames, End = b / (double)audio.Frames }).ToArray();
    }
    public static (float Min, float Max)[] Envelope(SampleAudio audio, int width = 1000)
    {
        var result = new (float, float)[Math.Min(width, audio.Frames)];
        for (int x = 0; x < result.Length; x++)
        {
            float min = 0, max = 0;
            int start = (int)((long)x * audio.Frames / result.Length), end = (int)((long)(x + 1) * audio.Frames / result.Length);
            for (int f = start; f < end; f++)
                for (int c = 0; c < audio.Channels; c++)
                { float value = audio.Samples[f * audio.Channels + c]; min = Math.Min(min, value); max = Math.Max(max, value); }
            result[x] = (min, max);
        }
        return result;
    }
    public static void WriteSlice(string path, SampleAudio audio, SliceRegion region, bool reverse, int transpose)
    {
        if (transpose is < -24 or > 24) throw new InvalidDataException("Pitch limitado a ±24 semitons.");
        var (start, end) = Bounds(audio, region); int length = end - start;
        double step = Math.Pow(2, transpose / 12.0);
        int frames = Math.Max(1, (int)Math.Ceiling(length / step));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(file);
        int bytes = checked(frames * audio.Channels * 2);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)audio.Channels);
        writer.Write(audio.Rate); writer.Write(audio.Rate * audio.Channels * 2); writer.Write((ushort)(audio.Channels * 2)); writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
        for (int f = 0; f < frames; f++)
        {
            double position = Math.Min(length - 1, f * step); int index = (int)position, next = Math.Min(length - 1, index + 1);
            double fraction = position - index;
            int a = reverse ? end - 1 - index : start + index, b = reverse ? end - 1 - next : start + next;
            for (int c = 0; c < audio.Channels; c++)
            {
                double value = audio.Samples[a * audio.Channels + c] * (1 - fraction) + audio.Samples[b * audio.Channels + c] * fraction;
                writer.Write((short)Math.Clamp((int)Math.Round(value * 32768), short.MinValue, short.MaxValue));
            }
        }
    }
    public static string Export(string folder, SampleAudio audio, SampleSettings settings)
    {
        settings.Validate();
        if (settings.SourceHash != audio.Hash || settings.SourceFrames != audio.Frames || settings.SourceRate != audio.Rate)
            throw new InvalidDataException("O WAV carregado difere da configuração. Recarregue o sample antes de exportar.");
        foreach (var region in settings.Regions) Bounds(audio, region);
        string destination = Path.Combine(folder, $"X-MIDI-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")}");
        if (Directory.Exists(destination)) throw new IOException("Destino de exportação já existe.");
        Directory.CreateDirectory(destination);
        try
        {
            for (int i = 0; i < settings.Regions.Length; i++) WriteSlice(Path.Combine(destination, $"Slice-{i + 1:00}.wav"), audio, settings.Regions[i], settings.Reverse, settings.Transpose);
            File.WriteAllText(Path.Combine(destination, "mapping.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            string[] letters = ["A", "B", "X", "Y"];
            string mapping = string.Join(Environment.NewLine, settings.Pads.Select((p, i) => $"Banco {i / 4 + 1} {letters[i % 4]}: " +
                (p.Region < 0 ? "sem região" : $"Slice-{p.Region + 1:00}.wav -> MIDI {p.Note} ({ChordGenerator.NoteName(p.Note)})")));
            File.WriteAllText(Path.Combine(destination, "FL-STUDIO.txt"),
                "Carregamento manual: arraste cada WAV a um pad do FPC e configure a nota MIDI conforme a lista.\n" +
                "Para respeitar Gate e a duração One Shot agendada pelo X-MIDI, habilite o envelope de volume com sustain e release curto no instrumento.\n" +
                "Em FPC one-shot sem envelope, Note Off pode ser ignorado: não garante parada ao trocar modos.\n" +
                "Alternativa: carregue o WAV original no Slicex e recrie manualmente os marcadores; os WAV exportados já têm reverse/pitch aplicados.\n" +
                "Não há envio automático de áudio, marcadores, plugins ou links de efeitos. Vincule os CC aos parâmetros no FL.\n\n" + mapping);
            return destination;
        }
        catch { Directory.Delete(destination, recursive: true); throw; }
    }
}
