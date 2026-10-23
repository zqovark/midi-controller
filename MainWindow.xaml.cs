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
        preset.RepeatFailed += ex => Dispatcher.BeginInvoke(new Action(() =>
        {
            midi.Dispose();
            preset.Reset();
            MidiStatus.Text = $"Envio MIDI interrompido: {ex.Message}";
        }));
        ModePanel.Attach(preset, RunCommand);
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
            // MIDI is sent on the scheduler thread; the monitor must never block it.
            var stamp = DateTime.Now;
            void AddMessage()
            {
                if (Dispatcher.HasShutdownStarted) return;
                string kind = status == 0x90 ? "ON " : status == 0x80 ? "OFF" : "CC ";
                Monitor.Items.Insert(0, $"{stamp:HH:mm:ss.fff} {kind} {data1,3} {data2,3} ch {(status & 15) + 1} [{status:X2} {data1:X2} {data2:X2}]");
                if (Monitor.Items.Count > 200) Monitor.Items.RemoveAt(200);
                MessageCount.Text = $"{++messageCount} enviadas";
            }
            if (Dispatcher.CheckAccess()) AddMessage();
            else Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(AddMessage));
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
            gamepad = default;
            activeController = null;
            ControllerStatus.Text = "● Aguardando controle";
        }
        preset.SetTrigger(gamepad.LeftTrigger);
        var changed = gamepad.Buttons ^ previousButtons;
        // Release first; RB pressed alongside a pad release must not retrigger that pad.
        foreach (var button in Enum.GetValues<GamepadButtons>())
            if ((changed & button) != 0 && (gamepad.Buttons & button) == 0)
                preset.Button(button, false);
        // Apply RB before new presses so simultaneous RB + pad uses repeat from the first hit.
        if ((changed & gamepad.Buttons & GamepadButtons.RightShoulder) != 0)
            preset.Button(GamepadButtons.RightShoulder, true);
        foreach (var button in Enum.GetValues<GamepadButtons>().Where(b => b != GamepadButtons.RightShoulder))
            if ((changed & gamepad.Buttons & button) != 0)
                preset.Button(button, true);
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
        VelocityReadout.Text = $"LT {LTBar.Value:0}% · VEL {preset.CurrentVelocity}";
        RepeatReadout.Text = preset.IsRepeating ? "● REPETINDO" : "RB + A/B/X/Y";
        ModePanel.ShowPerformance(gamepad.Buttons);
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
            BankDescription.Text = $"{preset.Settings.Mode.ToString().ToUpperInvariant()} · BANCO {preset.ActiveBank + 1} · CANAL 1";
            ModeSelector.SelectedIndex = (int)preset.Settings.Mode;
            ModeCaption.Text = preset.Settings.Mode switch
            { MusicalMode.Sample => "Waveform / slices", MusicalMode.Chord => "Acordes / notas MIDI", _ => "Entradas em tempo real" };
            MappingTitle.Text = preset.Settings.Mode switch
            { MusicalMode.Sample => "Slices nos pads", MusicalMode.Chord => "Acordes nos pads", _ => "Mapeamento de notas" };
            DrumRepeatControls.Visibility = preset.Settings.Mode == MusicalMode.Drum ? Visibility.Visible : Visibility.Collapsed;
            string Effects(int stick) => string.Join(" / ", preset.Settings.Samples.Effects.Where(e => e.Enabled && e.Stick == stick).Select(e => $"{e.Name} CC {e.Cc}"));
            LeftEffectLabel.Text = preset.Settings.Mode == MusicalMode.Sample ? Effects(0) : "FILTRO · CC 74";
            RightEffectLabel.Text = preset.Settings.Mode == MusicalMode.Sample ? Effects(1) : "EFEITO · CC 91";
            BankButton.Background = Brush(preset.ActiveBank == 0 ? "#D0BCFF" : "#CCC2DC");
            var labels = new[] { MapA, MapB, MapX, MapY };
            var letters = new[] { "A", "B", "X", "Y" };
            for (int i = 0; i < 4; i++)
            {
                labels[i].Text = $"{letters[i]} · {preset.PadDescription(i)}";
                labels[i].ToolTip = labels[i].Text;
            }
            SwingValue.Text = $"SWING GLOBAL · {preset.Settings.SwingPercent}%";
            SwingStepLabel.Text = $"passo {preset.Settings.SwingStep}%";
            SwingSlider.TickFrequency = preset.Settings.SwingStep;
            SwingSlider.Value = preset.Settings.SwingPercent;
            DefaultVelocitySlider.Value = preset.Settings.DefaultVelocity;
            MinimumVelocitySlider.Maximum = preset.Settings.DefaultVelocity;
            MinimumVelocitySlider.Value = preset.Settings.MinimumVelocity;
            VelocityCurveSelector.SelectedIndex = (int)preset.Settings.VelocityCurve;
            RepeatEnabled.IsChecked = preset.Settings.NoteRepeatEnabled;
            RepeatDivisionSelector.SelectedIndex = (int)preset.Settings.RepeatDivision;
            BpmSlider.Value = preset.Settings.RepeatBpm;
            UpdateDrumLabels();
            ModePanel.Refresh();
        }
        finally { updatingMusic = false; }
    }

    private void ModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingMusic || preset is null || ModeSelector.SelectedIndex < 0) return;
        RunCommand(() => preset.SetMode((MusicalMode)ModeSelector.SelectedIndex));
        ModePanel.ShowPerformance(GamepadButtons.None);
    }

    private void UpdateDrumLabels()
    {
        DefaultVelocityLabel.Text = $"PADRÃO {preset.Settings.DefaultVelocity}";
        MinimumVelocityLabel.Text = $"MÍNIMA {preset.Settings.MinimumVelocity}";
        BpmLabel.Text = $"BPM {preset.Settings.RepeatBpm:0}";
        VelocityReadout.Text = $"LT {LTBar.Value:0}% · VEL {preset.CurrentVelocity}";
    }

    private void DrumValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateDrumSettings();
    private void DrumSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDrumSettings();
    private void RepeatEnabledChanged(object sender, RoutedEventArgs e) => UpdateDrumSettings();
    private void UpdateDrumSettings()
    {
        if (updatingMusic || preset is null) return;
        var settings = preset.Settings;
        settings.DefaultVelocity = (int)DefaultVelocitySlider.Value;
        settings.MinimumVelocity = Math.Min((int)MinimumVelocitySlider.Value, settings.DefaultVelocity);
        settings.VelocityCurve = (VelocityCurve)Math.Max(0, VelocityCurveSelector.SelectedIndex);
        settings.NoteRepeatEnabled = RepeatEnabled.IsChecked == true;
        settings.RepeatDivision = (RepeatDivision)Math.Max(0, RepeatDivisionSelector.SelectedIndex);
        settings.RepeatBpm = BpmSlider.Value;
        RunCommand(preset.ConfigureDrums);
        UpdateMusicalControls();
        ScheduleSave();
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
        if (preset.Settings.Mode != MusicalMode.Drum)
        {
            var mapping = new ModeMappingWindow(preset.Settings.Mode == MusicalMode.Chord ? preset.Settings.Chords : null,
                preset.Settings.Mode == MusicalMode.Sample ? preset.Settings.Samples : null, Resources) { Owner = this };
            if (mapping.ShowDialog() != true) return;
            RunCommand(() => preset.ApplyModeSettings(mapping.Chords, mapping.Samples));
            allowSaving = true; saveTimer.Stop(); await SaveSettingsAsync(); return;
        }
        var editor = new SettingsWindow(preset.Settings, Resources) { Owner = this };
        if (editor.ShowDialog() != true) return;
        editor.Result.SwingPercent = preset.Settings.SwingPercent;
        editor.Result.Mode = preset.Settings.Mode;
        editor.Result.Chords = preset.Settings.Chords.Copy();
        editor.Result.Samples = preset.Settings.Samples.Copy();
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
        finally
        {
            try { preset.Dispose(); }
            catch (InvalidOperationException) { }
            finally { midi.Dispose(); }
        }
        if (settingsDirty && allowSaving)
        {
            try { settingsStore.SaveAsync(preset.Settings.Copy()).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { MessageBox.Show(this, ex.Message, "Não foi possível salvar configurações", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        base.OnClosing(e);
    }
}
