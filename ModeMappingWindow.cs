using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace XMidi;

internal sealed class ModeMappingWindow : Window
{
    internal ChordSettings? Chords { get; }
    internal SampleSettings? Samples { get; }
    private readonly List<(TextBox Name, TextBox Notes, TextBox Octave)> chordInputs = [];
    private readonly List<(ComboBox Region, TextBox Note)> sliceInputs = [];
    private readonly List<(CheckBox Enabled, TextBox Cc, ComboBox Stick)> effects = [];
    private readonly TextBlock error = new() { Foreground = Brushes.LightSalmon, TextWrapping = TextWrapping.Wrap };
    internal ModeMappingWindow(ChordSettings? chords, SampleSettings? samples, ResourceDictionary resources)
    {
        Chords = chords?.Copy(); Samples = samples?.Copy();
        Title = chords is null ? "Slices / CC" : "Oito acordes";
        Width = 690; Height = 630; MinWidth = 650; MinHeight = 500;
        Background = new SolidColorBrush(Color.FromRgb(29, 27, 32));
        Foreground = Brushes.White; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(resources);
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Margin = new Thickness(5) };
        var save = new Button { Content = "Salvar", IsDefault = true, Margin = new Thickness(5) }; save.Click += SaveClicked;
        buttons.Children.Add(cancel); buttons.Children.Add(save); bottom.Children.Add(buttons);
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = chords is null ? "Banco / pad → região → nota MIDI" : "Nome · notas MIDI separadas por vírgula · deslocamento de oitava", Margin = new Thickness(0, 0, 0, 8) });
        if (chords is not null) panel.Children.Add(new TextBlock { Text = "Notas vazias = geração pela escala. Notas explícitas mantêm o registro informado.", FontSize = 11, TextWrapping = TextWrapping.Wrap });
        string[] letters = ["A", "B", "X", "Y"];
        for (int i = 0; i < 8; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) }; panel.Children.Add(row);
            row.Children.Add(new TextBlock { Text = $"{i / 4 + 1} {letters[i % 4]}", Width = 40, VerticalAlignment = VerticalAlignment.Center });
            if (Chords is { } c)
            {
                var p = c.Pads[i];
                var name = Input(p.Name, 145); var notes = Input(p.Notes is null ? "" : string.Join(",", p.Notes), 285);
                notes.ToolTip = "Gerado: " + ChordGenerator.Describe(ChordGenerator.Generate(c, i));
                var octave = Input(p.OctaveShift.ToString(), 45); octave.ToolTip = "Oitavas relativas: -2 a +2 (somente acordes gerados)";
                row.Children.Add(name); row.Children.Add(notes); row.Children.Add(octave); chordInputs.Add((name, notes, octave));
            }
            else if (Samples is { } s)
            {
                var region = new ComboBox { Width = 200, ItemsSource = new[] { "Sem região" }.Concat(Enumerable.Range(1, s.Regions.Length).Select(n => $"Slice {n}")).ToArray(), SelectedIndex = s.Pads[i].Region + 1 };
                var note = Input(s.Pads[i].Note.ToString(), 90); row.Children.Add(region); row.Children.Add(note); sliceInputs.Add((region, note));
            }
        }
        if (Samples is { } sample)
        {
            panel.Children.Add(new TextBlock { Text = "Efeitos: habilitar · CC · analógico X (vincule o parâmetro no FL)", Margin = new Thickness(0, 16, 0, 5), FontSize = 11 });
            foreach (var effect in sample.Effects)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) }; panel.Children.Add(row);
                var enabled = new CheckBox { Content = effect.Name, IsChecked = effect.Enabled, Width = 145, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
                var cc = Input(effect.Cc.ToString(), 65);
                var stick = new ComboBox { ItemsSource = new[] { "Esquerdo X", "Direito X" }, SelectedIndex = effect.Stick, Width = 150 };
                row.Children.Add(enabled); row.Children.Add(cc); row.Children.Add(stick); effects.Add((enabled, cc, stick));
            }
        }
    }
    private static TextBox Input(string text, double width) => new() { Text = text, Width = width, Margin = new Thickness(3, 0, 3, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Chords is { } chords)
            {
                for (int i = 0; i < 8; i++)
                {
                    var input = chordInputs[i]; var pad = chords.Pads[i];
                    pad.Name = input.Name.Text.Trim(); pad.OctaveShift = int.Parse(input.Octave.Text);
                    pad.Notes = string.IsNullOrWhiteSpace(input.Notes.Text) ? null : input.Notes.Text.Split(',').Select(n => int.Parse(n.Trim())).ToArray();
                }
                chords.Validate();
            }
            if (Samples is { } samples)
            {
                for (int i = 0; i < 8; i++) { samples.Pads[i].Region = sliceInputs[i].Region.SelectedIndex - 1; samples.Pads[i].Note = int.Parse(sliceInputs[i].Note.Text); }
                for (int i = 0; i < 4; i++)
                { samples.Effects[i].Enabled = effects[i].Enabled.IsChecked == true; samples.Effects[i].Cc = int.Parse(effects[i].Cc.Text); samples.Effects[i].Stick = effects[i].Stick.SelectedIndex; }
                samples.Validate();
            }
            DialogResult = true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or System.IO.InvalidDataException) { error.Text = ex.Message; }
    }
}
