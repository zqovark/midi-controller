using System.Windows;

namespace XMidi;

public partial class SettingsWindow : Window
{
    internal sealed class PadEditor
    {
        public string Letter { get; init; } = "";
        public string Name { get; set; } = "";
        public string Note { get; set; } = "";
    }
    private readonly PadEditor[][] editors;
    internal AppSettings Result { get; private set; }

    internal SettingsWindow(AppSettings settings, ResourceDictionary resources)
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(resources);
        Result = settings.Copy();
        string[] letters = ["A", "B", "X", "Y"];
        editors = settings.Banks.Select(bank => bank.Select((pad, i) => new PadEditor
        { Letter = letters[i], Name = pad.Name, Note = pad.Note.ToString() }).ToArray()).ToArray();
        Bank1Editors.ItemsSource = editors[0];
        Bank2Editors.ItemsSource = editors[1];
        SwingStepInput.Text = settings.SwingStep.ToString();
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SwingStepInput.Text, out int step) || step is < 1 or > 100)
        { ErrorText.Text = "O incremento deve ser um inteiro entre 1 e 100%."; return; }
        var banks = new PadMapping[2][];
        for (int bank = 0; bank < 2; bank++)
        {
            banks[bank] = new PadMapping[4];
            for (int pad = 0; pad < 4; pad++)
            {
                var editor = editors[bank][pad];
                if (string.IsNullOrWhiteSpace(editor.Name) || !int.TryParse(editor.Note, out int note) || note is < 0 or > 127)
                { ErrorText.Text = $"Banco {bank + 1}, botão {editor.Letter}: informe um nome e uma nota de 0 a 127."; return; }
                banks[bank][pad] = new() { Name = editor.Name.Trim(), Note = note };
            }
        }
        Result.Banks = banks;
        Result.SwingStep = step;
        DialogResult = true;
    }
}
