using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vortice.XInput;

namespace XMidi;

public partial class MainWindow : Window
{
    private readonly ControllerReader reader = new();
    private readonly MidiOutput midi = new();
    private readonly BoomBapPreset preset;
    private readonly DispatcherTimer timer;
    private readonly Dictionary<GamepadButtons, Border> indicators;
    private readonly SettingsStore settingsStore;
    private readonly DispatcherTimer saveTimer;
    private bool settingsDirty;
    private bool allowSaving;
    private bool updatingMusic;
    private uint? activeController;
    private GamepadButtons previousButtons;
    private long messageCount;
    private bool refreshing;
    private static readonly Dictionary<string, SolidColorBrush> brushes = new();

    public MainWindow() : this(new SettingsStore()) { }

    internal MainWindow(SettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;
        InitializeComponent();
        var settings = settingsStore.Load(out string? warning);
        allowSaving = warning is null;
        if (warning is not null) SettingsStatus.Text = warning + " Abra Configurar para salvar novos valores.";
        preset = new(midi, settings);
        saveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        saveTimer.Tick += async (_, _) => { saveTimer.Stop(); await SaveSettingsAsync(); };
        preset.Changed += () => { UpdateMusicalControls(); ScheduleSave(); };
        preset.CommandSent += message => CommandStatus.Text = message;
        UpdateMusicalControls();
        indicators = new()
        {
            [GamepadButtons.A] = PadA, [GamepadButtons.B] = PadB,
            [GamepadButtons.X] = PadX, [GamepadButtons.Y] = PadY,
            [GamepadButtons.LeftShoulder] = LB, [GamepadButtons.RightShoulder] = RB,
            [GamepadButtons.Back] = Back, [GamepadButtons.Start] = Start,
            [GamepadButtons.DPadUp] = DPadUp, [GamepadButtons.DPadDown] = DPadDown,
            [GamepadButtons.DPadLeft] = DPadLeft, [GamepadButtons.DPadRight] = DPadRight
        };
        midi.Sent += (status, data1, data2) =>
        {
            string kind = status == 0x90 ? "ON " : status == 0x80 ? "OFF" : "CC ";
            string channel = $"ch {(status & 15) + 1}";
            Monitor.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} {kind} {data1,3} {data2,3} {channel} [{status:X2} {data1:X2} {data2:X2}]");
            if (Monitor.Items.Count > 200) Monitor.Items.RemoveAt(200);
            MessageCount.Text = $"{++messageCount} enviadas";
        };
        reader.Sampled += Sampled;
        timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) =>
        {
            try { reader.Poll(); }
            catch (Exception ex) when (ex is InvalidOperationException or TypeInitializationException or PlatformNotSupportedException)
            {
                midi.Dispose();
                preset.Reset();
                MidiStatus.Text = ex.GetBaseException().Message;
                if (ex is not InvalidOperationException) timer.Stop();
            }
        };
        RefreshPorts();
        timer.Start();
    }

    private void Sampled(uint index, bool connected, Gamepad gamepad)
    {
        if (activeController is null && connected)
        {
            activeController = index;
            previousButtons = GamepadButtons.None;
            ControllerStatus.Text = $"● Controle {index + 1} conectado";
        }
        if (activeController != index) return;
        if (!connected)
        {
            preset.Silence();
            activeController = null;
            ControllerStatus.Text = "● Aguardando controle";
        }
        foreach (var button in Enum.GetValues<GamepadButtons>())
            if (((gamepad.Buttons ^ previousButtons) & button) != 0)
                preset.Button(button, (gamepad.Buttons & button) != 0);
        previousButtons = gamepad.Buttons;
        if (connected) preset.Analog(gamepad);
        foreach (var (button, indicator) in indicators)
            indicator.Background = Brush((gamepad.Buttons & button) != 0 ? "#4F378B" : "#36343B");
        UpdateStick(LeftPuck, gamepad.LeftThumbX, gamepad.LeftThumbY, Gamepad.LeftThumbDeadZone, (gamepad.Buttons & GamepadButtons.LeftThumb) != 0, LeftValues);
        UpdateStick(RightPuck, gamepad.RightThumbX, gamepad.RightThumbY, Gamepad.RightThumbDeadZone, (gamepad.Buttons & GamepadButtons.RightThumb) != 0, RightValues);
        LTBar.Value = gamepad.LeftTrigger * 100.0 / 255;
        RTBar.Value = gamepad.RightTrigger * 100.0 / 255;
        LTLabel.Text = $"LT {LTBar.Value:0}%";
        RTLabel.Text = $"RT {RTBar.Value:0}%";
    }

    private static void UpdateStick(Ellipse puck, short rawX, short rawY, int deadZone, bool pressed, TextBlock label)
    {
        int x = ControllerReader.ThumbPercent(rawX, deadZone), y = ControllerReader.ThumbPercent(rawY, deadZone);
        Canvas.SetLeft(puck, 29 + x * 0.27);
        Canvas.SetTop(puck, 29 - y * 0.27);
        puck.Fill = Brush(pressed ? "#D0BCFF" : "#65558F");
        label.Text = $"X {x}% · Y {y}% · CC {(int)Math.Round((x + 100) * 127.0 / 200)}";
    }

    private static SolidColorBrush Brush(string color)
    {
        if (brushes.TryGetValue(color, out var cached)) return cached;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        brushes.Add(color, brush);
        return brush;
    }

    private void MinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void RefreshClicked(object sender, RoutedEventArgs e) => RefreshPorts();
    private void RefreshPorts()
    {
        string? oldName = (Ports.SelectedItem as MidiPort)?.Name;
        try
        {
            preset.Silence();
            midi.Dispose();
            refreshing = true;
            var ports = MidiOutput.GetPorts();
            Ports.ItemsSource = ports;
            Ports.SelectedItem = ports.FirstOrDefault(p => p.Name == (oldName ?? preset.Settings.PreferredPort))
                ?? ports.FirstOrDefault(p => p.Name.Equals("X-MIDI", StringComparison.OrdinalIgnoreCase))
                ?? ports.FirstOrDefault(p => p.Name.Contains("loopMIDI", StringComparison.OrdinalIgnoreCase));
            MidiStatus.Text = ports.Count == 0 ? "Nenhuma porta MIDI disponível" : "Selecione a porta do loopMIDI";
        }
        catch (InvalidOperationException ex) { MidiStatus.Text = ex.Message; }
        finally { refreshing = false; }
        OpenSelectedPort();
    }

    private void PortSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshing) OpenSelectedPort();
    }

    private void OpenSelectedPort()
    {
        try
        {
            preset.Silence();
            midi.Dispose();
            if (Ports.SelectedItem is not MidiPort port) return;
            midi.Open(port.Id);
            preset.Reset();
            preset.SyncSwing();
            if (preset.Settings.PreferredPort != port.Name)
            {
                preset.Settings.PreferredPort = port.Name;
                ScheduleSave();
            }
            MidiStatus.Text = $"Conectado · {port.Name} · canal 1";
        }
        catch (InvalidOperationException ex) { midi.Dispose(); MidiStatus.Text = ex.Message; }
    }

    private void SilenceClicked(object sender, RoutedEventArgs e)
    {
        try { preset.Silence(); }
        catch (InvalidOperationException ex) { midi.Dispose(); MidiStatus.Text = ex.Message; }
    }

    private void UpdateMusicalControls()
    {
        updatingMusic = true;
        try
        {
            BankLabel.Text = $"↓ Banco {preset.ActiveBank + 1}";
            BankDescription.Text = $"BANCO {preset.ActiveBank + 1} · CANAL 1";
            BankButton.Background = Brush(preset.ActiveBank == 0 ? "#D0BCFF" : "#CCC2DC");
            var labels = new[] { MapA, MapB, MapX, MapY };
            var letters = new[] { "A", "B", "X", "Y" };
            for (int i = 0; i < 4; i++)
            {
                var pad = preset.Settings.Banks[preset.ActiveBank][i];
                labels[i].Text = $"{letters[i]} · {pad.Name} · {pad.Note}";
                labels[i].ToolTip = labels[i].Text;
            }
            SwingValue.Text = $"SWING GLOBAL · {preset.Settings.SwingPercent}%";
            SwingStepLabel.Text = $"passo {preset.Settings.SwingStep}%";
            SwingSlider.TickFrequency = preset.Settings.SwingStep;
            SwingSlider.Value = preset.Settings.SwingPercent;
        }
        finally { updatingMusic = false; }
    }

    private void RunCommand(Action command)
    {
        try { command(); }
        catch (InvalidOperationException ex) { midi.Dispose(); preset.Reset(); MidiStatus.Text = ex.Message; }
    }
    private void BankClicked(object sender, RoutedEventArgs e) => RunCommand(preset.ToggleBank);
    private void PlayPauseClicked(object sender, RoutedEventArgs e) => RunCommand(preset.PlayPause);
    private void SwingDownClicked(object sender, RoutedEventArgs e) => RunCommand(() => preset.SetSwing(preset.Settings.SwingPercent - preset.Settings.SwingStep));
    private void SwingUpClicked(object sender, RoutedEventArgs e) => RunCommand(() => preset.SetSwing(preset.Settings.SwingPercent + preset.Settings.SwingStep));
    private void SwingValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updatingMusic && preset is not null) RunCommand(() => preset.SetSwing((int)Math.Round(e.NewValue)));
    }

    private async void ConfigureClicked(object sender, RoutedEventArgs e)
    {
        var editor = new SettingsWindow(preset.Settings, Resources) { Owner = this };
        if (editor.ShowDialog() != true) return;
        editor.Result.SwingPercent = preset.Settings.SwingPercent;
        RunCommand(() => preset.ApplySettings(editor.Result));
        allowSaving = true;
        saveTimer.Stop();
        await SaveSettingsAsync();
    }

    private void ScheduleSave()
    {
        settingsDirty = true;
        if (!allowSaving) return;
        saveTimer.Stop();
        saveTimer.Start();
    }

    private async Task SaveSettingsAsync()
    {
        if (!settingsDirty || !allowSaving) return;
        settingsDirty = false;
        try
        {
            await settingsStore.SaveAsync(preset.Settings.Copy());
            SettingsStatus.Text = "Configurações salvas · ↑ Play/Pause · ↓ Banco · ←/→ Swing";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            settingsDirty = true;
            SettingsStatus.Text = $"Não foi possível salvar: {ex.Message}";
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        timer.Stop();
        saveTimer.Stop();
        try { preset.Silence(); }
        catch (InvalidOperationException) { }
        finally { midi.Dispose(); }
        if (settingsDirty && allowSaving)
        {
            try { settingsStore.SaveAsync(preset.Settings.Copy()).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { MessageBox.Show(this, ex.Message, "Não foi possível salvar configurações", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        base.OnClosing(e);
    }
}
