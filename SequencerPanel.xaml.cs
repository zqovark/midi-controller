using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace XMidi;

public partial class SequencerPanel : UserControl
{
    private BoomBapPreset? preset;
    private SequenceEngine engine = null!;
    private SequenceStore store = null!;
    private readonly DispatcherTimer refreshTimer;
    private bool updating, canSaveLibrary;
    private long seenRevision = -1;
    private Guid? selected;
    private SequencePattern snapshot = new();
    private int[] activeTracks = [];
    private string mappingNames = "";
    private Guid? highlightedSelection;
    public SequencerPanel()
    {
        InitializeComponent();
        StepsSelector.ItemsSource = new[] { 16, 32, 64 };
        QuantizeSelector.ItemsSource = new[] { "Off", "1/4", "1/8", "1/16", "1/32", "1/8T", "1/16T" };
        refreshTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        refreshTimer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible && preset is not null) { refreshTimer.Start(); Refresh(); } else refreshTimer.Stop(); };
        Unloaded += (_, _) => refreshTimer.Stop();
        StepsGrid.NoteClicked += GridNoteClicked;
    }
    internal void Attach(BoomBapPreset value, string libraryPath)
    {
        preset = value; engine = value.Sequencer; store = new(libraryPath);
        try { engine.LoadLibrary(store.Load()); canSaveLibrary = true; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { StatusText.Text = $"Biblioteca não carregada e não será sobrescrita: {ex.Message} Use Salvar JSON para preservar novos padrões."; }
        Refresh();
    }
    internal Task SaveLibraryAsync() => canSaveLibrary ? store.SaveAsync(engine.LibrarySnapshot()) : Task.CompletedTask;
    internal void Shutdown() => refreshTimer.Stop();
    internal void Resume() { if (IsVisible) refreshTimer.Start(); }
    private static void TextUnlessEditing(TextBox input, string value) { if (!input.IsKeyboardFocusWithin) input.Text = value; }
    private void Refresh()
    {
        if (preset is null) return;
        var state = engine.Status(); updating = true;
        try
        {
            if (seenRevision != state.Revision)
            {
                snapshot = engine.Snapshot(); seenRevision = state.Revision;
                var library = engine.LibrarySnapshot();
                Patterns.DisplayMemberPath = "Name"; Patterns.SelectedValuePath = "Id";
                Patterns.ItemsSource = library.Patterns.Select(p => new { p.Id, p.Name }).ToArray(); Patterns.SelectedValue = snapshot.Id;
                TextUnlessEditing(NameInput, snapshot.Name); TextUnlessEditing(BpmInput, snapshot.Bpm.ToString("0.##", CultureInfo.InvariantCulture));
                TextUnlessEditing(MeterInput, $"{snapshot.Numerator}/{snapshot.Denominator}"); TextUnlessEditing(BarsInput, snapshot.Bars.ToString());
                if (!StepsSelector.IsDropDownOpen) StepsSelector.SelectedItem = snapshot.StepsPerBar;
                if (!QuantizeSelector.IsDropDownOpen) QuantizeSelector.SelectedIndex = (int)snapshot.Quantize;
                TextUnlessEditing(StrengthInput, snapshot.QuantizeStrength.ToString());
                StepsGrid.SetPattern(snapshot, engine.Swing, selected); UpdateSelection();
            }
            StepsGrid.SetPlayhead(state.Playing && state.AbsoluteTick >= 0 ? state.LoopTick : -1);
            string editor = LiveToggle.IsChecked == true ? "LIVE" : "STEP";
            string status = state.CountIn ? $"PREP {state.CountInBeats}" : state.Recording ? "● REC" : state.Playing ? "▶ PLAY" : "■ STOP";
            TransportStatus.Text = $"{editor} · {status} · compasso {state.Bar}/{snapshot.Bars} · tick {state.LoopTick:0} · swing {engine.Swing}% · LOCAL / FL não sincronizado";
            TransportStatus.Foreground = state.Recording ? System.Windows.Media.Brushes.LightSalmon : System.Windows.Media.Brushes.Plum;
            var mappings = preset.Settings.Banks.SelectMany(b => b).ToArray();
            string names = string.Join("|", mappings.Select(m => m.Name));
            if (!activeTracks.SequenceEqual(state.ActiveTracks) || mappingNames != names || highlightedSelection != selected)
            {
                activeTracks = state.ActiveTracks; mappingNames = names; highlightedSelection = selected;
                int selectedTrack = snapshot.Tracks.FirstOrDefault(t => t.Events.Any(n => n.Id == selected))?.Instrument ?? -1;
                TrackNames.ItemsSource = mappings.Select((p, i) => new { Name = $"{i + 1} {p.Name}",
                    Color = activeTracks.Contains(i) ? "#4F378B" : i == selectedTrack ? "#36343B" : "#211F26" }).ToArray();
            }
        }
        finally { updating = false; }
    }
    private void Run(Action action)
    {
        if (preset is null) return;
        try { action(); Refresh(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { StatusText.Text = ex.Message; }
    }
    private void GridNoteClicked(int track, long tick, Guid? id, bool remove) => Run(() =>
    {
        if (remove)
        {
            if (id is { } delete) engine.Edit(p => p.Tracks.Single(t => t.Instrument == track).Events.RemoveAll(n => n.Id == delete));
            if (selected == id) selected = null;
        }
        else if (id is { } hit) selected = hit;
        else
        {
            var note = new SequenceNote { OriginalTick = tick, Velocity = preset!.Settings.DefaultVelocity };
            engine.Edit(p => p.Tracks.Single(t => t.Instrument == track).Events.Add(note)); selected = note.Id;
        }
        snapshot = engine.Snapshot(); StepsGrid.SetPattern(snapshot, engine.Swing, selected); UpdateSelection();
    });
    private void UpdateSelection()
    {
        var found = snapshot.Tracks.SelectMany(t => t.Events.Select(n => (Track: t.Instrument, Note: n))).FirstOrDefault(v => v.Note.Id == selected);
        if (found.Note is null) { SelectedLabel.Text = "Selecione uma nota"; return; }
        SelectedLabel.Text = $"Tr {found.Track + 1} · original {found.Note.OriginalTick}";
        TextUnlessEditing(VelocityInput, found.Note.Velocity.ToString()); TextUnlessEditing(DurationInput, found.Note.Duration.ToString()); TextUnlessEditing(OffsetInput, found.Note.OffsetTicks.ToString());
    }
    private void EditNoteClicked(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (selected is null) return;
        int velocity = int.Parse(VelocityInput.Text); long duration = long.Parse(DurationInput.Text), offset = long.Parse(OffsetInput.Text);
        engine.Edit(p => { var note = p.Tracks.SelectMany(t => t.Events).Single(n => n.Id == selected); note.Velocity = velocity; note.Duration = duration; note.OffsetTicks = offset; });
    });
    private void RemoveNoteClicked(object sender, RoutedEventArgs e) => Run(() =>
    { if (selected is { } id) engine.Edit(p => { foreach (var t in p.Tracks) t.Events.RemoveAll(n => n.Id == id); }); selected = null; });
    private void GridScrolled(object sender, ScrollChangedEventArgs e) => StepsGrid.SetViewport(GridScroll.HorizontalOffset, GridScroll.ViewportWidth);
    private void PatternSelected(object sender, SelectionChangedEventArgs e)
    { if (!updating && preset is not null && Patterns.SelectedValue is Guid id) Run(() => { engine.Select(id); selected = null; }); }
    private void NewClicked(object sender, RoutedEventArgs e) => Run(() => { engine.Create(); selected = null; });
    private void DuplicateClicked(object sender, RoutedEventArgs e) => Run(() => { engine.Create(duplicate: true); selected = null; });
    private void DeleteClicked(object sender, RoutedEventArgs e) => Run(() => { engine.Delete(); selected = null; });
    private void RenameClicked(object sender, RoutedEventArgs e) => Run(() => engine.Edit(p => p.Name = NameInput.Text.Trim()));
    private void ConfigureClicked(object sender, RoutedEventArgs e) => Run(() =>
    {
        double bpm = double.Parse(BpmInput.Text.Replace(',', '.'), CultureInfo.InvariantCulture);
        int[] meter = MeterInput.Text.Split('/').Select(int.Parse).ToArray(); if (meter.Length != 2) throw new FormatException("Use compasso como 4/4.");
        int bars = int.Parse(BarsInput.Text), steps = (int)StepsSelector.SelectedItem;
        var current = engine.Snapshot();
        if (current.Bars != bars || current.Numerator != meter[0] || current.Denominator != meter[1] || current.StepsPerBar != steps)
            engine.Edit(p => { p.Bars = bars; p.Numerator = meter[0]; p.Denominator = meter[1]; p.StepsPerBar = steps; });
        engine.SetBpm(bpm);
    });
    private void QuantizeClicked(object sender, RoutedEventArgs e) => Run(() => engine.SetQuantization((SequenceQuantize)QuantizeSelector.SelectedIndex, int.Parse(StrengthInput.Text)));
    private void RequireMidi()
    { if (preset?.MidiReady != true) throw new InvalidOperationException("Selecione a saída MIDI e o instrumento no FL antes de iniciar."); }
    private void PlayClicked(object sender, RoutedEventArgs e) => Run(() => { RequireMidi(); engine.Start(CountInToggle.IsChecked == true); });
    private void StopClicked(object sender, RoutedEventArgs e) => Run(engine.Stop);
    private void RecordClicked(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (engine.IsRecording) engine.EndRecording();
        else { RequireMidi(); LiveToggle.IsChecked = true; engine.BeginRecording(CountInToggle.IsChecked == true); }
    });
    private void EditorModeChanged(object sender, RoutedEventArgs e) { if (!updating && preset is not null) Refresh(); }
    private void OverdubChanged(object sender, RoutedEventArgs e) { if (!updating && preset is not null) Run(() => engine.Overdub = OverdubToggle.IsChecked == true); }
    private void MetronomeClicked(object sender, RoutedEventArgs e) => Run(() =>
    {
        engine.ConfigureMetronome(MetroToggle.IsChecked == true, int.Parse(MetroChannel.Text), int.Parse(MetroNote.Text), int.Parse(MetroVelocity.Text));
        StatusText.Text = "Clique MIDI exige um instrumento/nota audível no destino configurado; não ativa o metrônomo do FL.";
    });
    private void UndoClicked(object sender, RoutedEventArgs e) => Run(engine.Undo);
    private void RedoClicked(object sender, RoutedEventArgs e) => Run(engine.Redo);
    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Padrão X-MIDI|*.json", FileName = "pattern.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { engine.EndRecording(); await SequenceStore.SavePatternAsync(dialog.FileName, engine.Snapshot()); await SaveLibraryAsync(); StatusText.Text = "Padrão salvo em JSON."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { StatusText.Text = ex.Message; }
    }
    private async void LoadClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Padrão X-MIDI|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var loaded = await Task.Run(() => { var p = SequenceStore.Read<SequencePattern>(dialog.FileName); p.Validate(); return p; });
            engine.Import(loaded); selected = null; Refresh(); StatusText.Text = "Padrão importado como cópia; os padrões existentes foram preservados.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException) { StatusText.Text = ex.Message; }
    }
    private async void MidiClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Standard MIDI File|*.mid", FileName = "pattern.mid" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            engine.Stop(); var p = engine.Snapshot(); var banks = preset!.Settings.Banks.Select(b => b.Select(m => m.Copy()).ToArray()).ToArray(); int swing = engine.Swing;
            await Task.Run(() => SequenceMidiFile.Export(dialog.FileName, p, banks, swing));
            StatusText.Text = "MIDI exportado; reprodução local parada. Importe no FL e configure o instrumento/mapeamentos; use apenas um transporte para o padrão.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { StatusText.Text = ex.Message; }
    }
}
