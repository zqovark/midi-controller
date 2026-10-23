using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Vortice.XInput;

namespace XMidi;

public partial class MusicalModePanel : UserControl
{
    private BoomBapPreset? preset;
    private Action<Action> execute = action => action();
    private bool updating, busy;
    private int loadGeneration;
    private SampleAudio? audio;
    private string? attemptedPath;
    private byte[] playedNotes = [];
    private GamepadButtons pressed;
    public MusicalModePanel()
    {
        InitializeComponent();
        ChordPreset.ItemsSource = MusicalPresets.Chords; SamplePreset.ItemsSource = MusicalPresets.Samples;
        KeySelector.ItemsSource = new[] { "C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B" };
        ScaleSelector.ItemsSource = new[] { "Maior", "Menor natural", "Menor harmônica", "Pentatônica maior", "Pentatônica menor" };
        OctaveSelector.ItemsSource = Enumerable.Range(1, 6).Select(n => $"Oct {n}").ToArray();
        KindSelector.ItemsSource = new[] { "Maior", "Menor", "Sus2", "Sus4", "Dominante 7", "Maior 7", "Menor 7", "Menor 9", "Maior 9" };
        VoicingSelector.ItemsSource = new[] { "Fundamental", "1ª inversão", "2ª inversão", "Aberto" };
        MethodSelector.ItemsSource = new[] { "Equal Slices", "Manual Slices", "Transientes" }; CountSelector.ItemsSource = new[] { 2, 4, 8 };
        PlaybackSelector.ItemsSource = new[] { "One Shot", "Gate" };
        Waveform.RegionSelected += region => { RegionSelector.SelectedIndex = region; UpdateRegionInputs(); };
        Waveform.MarkerMoved += (index, end, position) => Safe(() =>
        {
            if (preset is null || audio is null) return;
            var settings = preset.Settings.Samples.Copy();
            if (end) settings.Regions[index].End = position; else settings.Regions[index].Start = position;
            SampleProcessor.Bounds(audio, settings.Regions[index]); settings.Method = ChopMethod.Manual;
            Commit(samples: settings);
        });
        Loaded += (_, _) => TryRestoreAudio();
        Unloaded += (_, _) => loadGeneration++;
    }
    internal void Attach(BoomBapPreset value, Action<Action> run) { preset = value; execute = run; Refresh(); }
    internal void Refresh()
    {
        if (preset is null) return;
        updating = true;
        try
        {
            var mode = preset.Settings.Mode;
            Visibility = mode == MusicalMode.Drum ? Visibility.Collapsed : Visibility.Visible;
            ChordPanel.Visibility = mode == MusicalMode.Chord ? Visibility.Visible : Visibility.Collapsed;
            SamplePanel.Visibility = mode == MusicalMode.Sample ? Visibility.Visible : Visibility.Collapsed;
            var c = preset.Settings.Chords;
            ChordPreset.SelectedIndex = Math.Max(0, Array.IndexOf(MusicalPresets.Chords, c.Preset));
            KeySelector.SelectedIndex = c.Key; ScaleSelector.SelectedIndex = (int)c.Scale; OctaveSelector.SelectedIndex = c.Octave - 1;
            KindSelector.SelectedIndex = (int)c.Kind; VoicingSelector.SelectedIndex = (int)c.Voicing; VoiceLeading.IsChecked = c.VoiceLeading;
            ProgressionInput.Text = string.Join(",", c.Pads.Select(p => p.Degree));
            var s = preset.Settings.Samples;
            SamplePreset.SelectedIndex = Math.Max(0, Array.IndexOf(MusicalPresets.Samples, s.Preset));
            MethodSelector.SelectedIndex = (int)s.Method; CountSelector.SelectedItem = s.SliceCount; SensitivitySlider.Value = s.Sensitivity;
            PlaybackSelector.SelectedIndex = (int)s.Playback; ReverseToggle.IsChecked = s.Reverse; PitchInput.Text = s.Transpose.ToString();
            int selected = Math.Clamp(RegionSelector.SelectedIndex, 0, s.Regions.Length - 1);
            RegionSelector.ItemsSource = Enumerable.Range(1, s.Regions.Length).Select(n => $"Slice {n}").ToArray(); RegionSelector.SelectedIndex = selected;
            Waveform.SelectedRegion = selected; Waveform.SetSettings(s); UpdateRegionInputs();
            SourceLabel.Text = s.FilePath is null ? "Nenhum WAV selecionado" : $"{Path.GetFileName(s.FilePath)} · {s.SourceFrames / (double)Math.Max(1, s.SourceRate):0.00}s · exportação offline";
            SourceLabel.ToolTip = s.FilePath;
            RenderChords();
        }
        finally { updating = false; }
        if (IsLoaded) TryRestoreAudio();
    }
    internal void ShowPerformance(GamepadButtons buttons)
    {
        if (preset is null || preset.Settings.Mode != MusicalMode.Chord) return;
        var notes = preset.ActiveNotes;
        if (pressed == buttons && playedNotes.SequenceEqual(notes)) return;
        pressed = buttons; playedNotes = notes; RenderChords();
    }
    private void RenderChords()
    {
        if (preset is null || preset.Settings.Mode != MusicalMode.Chord) return;
        playedNotes = preset.ActiveNotes;
        var activePads = preset.ActivePads;
        ChordReadout.Text = playedNotes.Length == 0 ? "Solte os pads para encerrar as notas." : "TOCANDO · " + ChordGenerator.Describe(playedNotes);
        string[] letters = ["A", "B", "X", "Y"];
        ChordPads.ItemsSource = Enumerable.Range(0, 4).Select(i => new
        {
            Text = $"{letters[i]} · {preset.PadDescription(i)}",
            Color = activePads.Contains(i) ? "#4F378B" : "#211F26"
        }).ToArray();
        DrawPiano();
    }
    private void PianoSizeChanged(object sender, SizeChangedEventArgs e) => DrawPiano();
    private void DrawPiano()
    {
        Piano.Children.Clear(); if (preset is null || preset.Settings.Mode != MusicalMode.Chord || Piano.ActualWidth <= 0) return;
        var all = Enumerable.Range(preset.ActiveBank * 4, 4).SelectMany(i => ChordGenerator.Generate(preset.Settings.Chords, i)).Concat(playedNotes).ToArray();
        int first = Math.Max(0, all.Min() / 12 * 12), last = Math.Min(127, (all.Max() / 12 + 1) * 12 - 1);
        double width = Piano.ActualWidth / (last - first + 1);
        for (int note = first; note <= last; note++)
        {
            bool black = note % 12 is 1 or 3 or 6 or 8 or 10;
            var key = new Border { Width = Math.Max(1, width - 1), Height = black ? 18 : 27,
                Background = playedNotes.Contains((byte)note) ? Brushes.Plum : black ? Brushes.DimGray : Brushes.LightGray,
                ToolTip = $"{ChordGenerator.NoteName(note)} · MIDI {note}" };
            Canvas.SetLeft(key, (note - first) * width); Piano.Children.Add(key);
        }
    }
    private void Commit(ChordSettings? chords = null, SampleSettings? samples = null)
    {
        if (preset is not null) execute(() => preset.ApplyModeSettings(chords, samples));
    }
    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { ModeStatus.Text = ex.Message; }
    }
    private void ChordPresetClicked(object sender, RoutedEventArgs e) => Safe(() => Commit(chords: MusicalPresets.Chord(Math.Max(0, ChordPreset.SelectedIndex))));
    private void GenerateChordsClicked(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (preset is null) return;
        int[] degrees = ProgressionInput.Text.Split(',').Select(n => int.Parse(n.Trim())).ToArray();
        if (degrees.Length != 8) throw new InvalidDataException("Informe oito graus, separados por vírgula.");
        var c = preset.Settings.Chords.Copy(); c.Key = KeySelector.SelectedIndex; c.Scale = (ScaleKind)ScaleSelector.SelectedIndex;
        c.Octave = OctaveSelector.SelectedIndex + 1; c.Kind = (ChordKind)KindSelector.SelectedIndex;
        c.Voicing = (ChordVoicing)VoicingSelector.SelectedIndex; c.VoiceLeading = VoiceLeading.IsChecked == true;
        c.Pads = degrees.Select((degree, i) => new ChordPad { Name = c.Pads[i].Name, Degree = degree, Kind = c.Kind, OctaveShift = c.Pads[i].OctaveShift }).ToArray();
        c.Preset = "Personalizado"; c.Validate(); Commit(chords: c); ModeStatus.Text = "Progressão gerada · notas editáveis nos oito pads.";
    });
    private void ChordMappingClicked(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (preset is null) return;
        var dialog = new ModeMappingWindow(preset.Settings.Chords, null, Window.GetWindow(this).Resources) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) Commit(chords: dialog.Chords);
    });
    private void SampleMappingClicked(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (preset is null) return;
        var dialog = new ModeMappingWindow(null, preset.Settings.Samples, Window.GetWindow(this).Resources) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) Commit(samples: dialog.Samples);
    });
    private void SetBusy(bool value) { busy = value; IsEnabled = !value; }
    private async void LoadSampleClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "WAV PCM / float|*.wav", Title = "Selecionar sample (não carrega no FL Studio)" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await LoadAudioAsync(dialog.FileName, restore: false);
    }
    private async void TryRestoreAudio()
    {
        if (busy || preset?.Settings.Mode != MusicalMode.Sample || preset.Settings.Samples.FilePath is not { } path || attemptedPath == path) return;
        await LoadAudioAsync(path, restore: true);
    }
    private async Task LoadAudioAsync(string path, bool restore)
    {
        if (preset is null || busy) return;
        SetBusy(true); int generation = ++loadGeneration; attemptedPath = path; ModeStatus.Text = "Lendo WAV e waveform…";
        try
        {
            var loaded = await Task.Run(() => { var a = SampleProcessor.Load(path); return (Audio: a, Envelope: SampleProcessor.Envelope(a)); });
            if (generation != loadGeneration) return;
            var settings = preset.Settings.Samples.Copy();
            if (restore && (settings.SourceHash != loaded.Audio.Hash || settings.SourceFrames != loaded.Audio.Frames || settings.SourceRate != loaded.Audio.Rate))
                throw new InvalidDataException("O arquivo mudou. Selecione-o novamente para atualizar o chopping explicitamente.");
            audio = loaded.Audio; Waveform.SetAudio(audio, loaded.Envelope);
            if (!restore)
            {
                settings.FilePath = Path.GetFullPath(path); settings.SourceFrames = audio.Frames; settings.SourceRate = audio.Rate; settings.SourceHash = audio.Hash;
                settings.Regions = SampleProcessor.Divide(audio, settings.Method == ChopMethod.Manual ? ChopMethod.Equal : settings.Method, settings.SliceCount, settings.Sensitivity);
                Reassign(settings); Commit(samples: settings);
            }
            ModeStatus.Text = restore ? "Waveform restaurada · nenhum áudio enviado ao FL." : "WAV carregado no editor · exporte os slices e carregue-os no FL.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (generation == loadGeneration) { audio = null; Waveform.SetAudio(null); ModeStatus.Text = ex.Message; } }
        finally { if (generation == loadGeneration) SetBusy(false); }
    }
    private static void Reassign(SampleSettings s)
    { for (int i = 0; i < 8; i++) s.Pads[i].Region = i < s.Regions.Length ? i : -1; }
    private async void DivideClicked(object sender, RoutedEventArgs e)
    {
        if (busy || preset is null || audio is null) { ModeStatus.Text = "Selecione um WAV primeiro."; return; }
        SetBusy(true);
        try
        {
            var settings = ReadSampleControls(); var source = audio;
            if (settings.Method == ChopMethod.Manual) { Commit(samples: settings); ModeStatus.Text = "Manual: arraste as bordas da região selecionada."; return; }
            settings.Regions = await Task.Run(() => SampleProcessor.Divide(source, settings.Method, settings.SliceCount, settings.Sensitivity));
            Reassign(settings); Commit(samples: settings);
            ModeStatus.Text = settings.Method == ChopMethod.Transients ? "Ataques estimados por energia; revise os marcadores. Lacunas usam cortes iguais." : "Regiões iguais criadas.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or OverflowException or ArgumentException) { ModeStatus.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private SampleSettings ReadSampleControls()
    {
        var s = preset!.Settings.Samples.Copy(); s.Method = (ChopMethod)MethodSelector.SelectedIndex;
        s.SliceCount = (int)CountSelector.SelectedItem; s.Sensitivity = SensitivitySlider.Value;
        s.Playback = (SlicePlayback)PlaybackSelector.SelectedIndex; s.Reverse = ReverseToggle.IsChecked == true; s.Transpose = int.Parse(PitchInput.Text);
        return s;
    }
    private void RegionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || preset is null) return;
        Waveform.SelectedRegion = Math.Max(0, RegionSelector.SelectedIndex); Waveform.InvalidateVisual(); UpdateRegionInputs();
    }
    private void UpdateRegionInputs()
    {
        if (preset is null) return;
        var s = preset.Settings.Samples; var r = s.Regions[Math.Clamp(RegionSelector.SelectedIndex, 0, s.Regions.Length - 1)];
        double seconds = s.SourceFrames / (double)Math.Max(1, s.SourceRate);
        StartInput.Text = (r.Start * seconds).ToString("0.000000", CultureInfo.InvariantCulture);
        EndInput.Text = (r.End * seconds).ToString("0.000000", CultureInfo.InvariantCulture);
    }
    private void ApplySampleClicked(object sender, RoutedEventArgs e) => Safe(() => ApplySampleControls());
    private SampleSettings ApplySampleControls()
    {
        if (preset is null || audio is null) throw new InvalidOperationException("Selecione um WAV primeiro.");
        var s = ReadSampleControls(); int index = Math.Clamp(RegionSelector.SelectedIndex, 0, s.Regions.Length - 1);
        static double Seconds(string text) => double.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture);
        var region = new SliceRegion { Start = Seconds(StartInput.Text) / audio.Seconds, End = Seconds(EndInput.Text) / audio.Seconds };
        // Text is rounded for display; snap edits to exact source frames before validating bounds.
        region.Start = Math.Round(region.Start * audio.Frames) / audio.Frames; region.End = Math.Round(region.End * audio.Frames) / audio.Frames;
        SampleProcessor.Bounds(audio, region); s.Regions[index] = region; s.Validate(); Commit(samples: s);
        ModeStatus.Text = "Chopping salvo localmente · exporte novamente para aplicar alterações de áudio no FL.";
        return s;
    }
    private async void SamplePresetClicked(object sender, RoutedEventArgs e)
    {
        if (busy || preset is null) return;
        SetBusy(true);
        try
        {
            var s = preset.Settings.Samples.Copy(); MusicalPresets.Sample(s, Math.Max(0, SamplePreset.SelectedIndex));
            if (audio is { } source) { s.Regions = await Task.Run(() => SampleProcessor.Divide(source, s.Method, s.SliceCount, s.Sensitivity)); Reassign(s); }
            Commit(samples: s); ModeStatus.Text = "Preset aplicado às configurações · não carrega instrumentos ou efeitos.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { ModeStatus.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (busy || preset is null || audio is null) { ModeStatus.Text = "Selecione um WAV primeiro."; return; }
        var dialog = new OpenFolderDialog { Title = "Pasta de destino (será criada uma subpasta)" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        SetBusy(true);
        try
        {
            var settings = ApplySampleControls(); var source = audio;
            ModeStatus.Text = "Exportando WAVs…";
            string result = await Task.Run(() => SampleProcessor.Export(dialog.FolderName, source, settings));
            ModeStatus.Text = $"Exportado: {result} · siga FL-STUDIO.txt para carregar e mapear no FL.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or OverflowException or ArgumentException) { ModeStatus.Text = ex.Message; }
        finally { SetBusy(false); }
    }
}
