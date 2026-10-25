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
    private readonly MidiInput feedback = new();
    private readonly FlLink fl;
    private readonly PerformanceSession performance;
    private volatile bool monitorEnabled;
    private long lastUiRefresh;
    private string menuSignature = "";
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
        Func<double> clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        fl = new(midi.Send, clock);
        performance = new(preset, fl, clock);
        DrumChannel.Text = (settings.ModeChannels[0] + 1).ToString();
        SampleChannel.Text = (settings.ModeChannels[1] + 1).ToString();
        ChordChannel.Text = (settings.ModeChannels[2] + 1).ToString();
        Preparation.Collapsed += (_, _) => monitorEnabled = false;
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
            if (!monitorEnabled) return;
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
            try
            {
                feedback.Drain(fl.Receive);
                performance.Tick();
                reader.Poll();
                if (Environment.TickCount64 - lastUiRefresh >= 50)
                { lastUiRefresh = Environment.TickCount64; UpdatePerformance(); }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TypeInitializationException or PlatformNotSupportedException)
            {
                midi.Dispose();
                preset.Reset();
                MidiStatus.Text = ex.GetBaseException().Message;
                if (ex is not InvalidOperationException) timer.Stop();
            }
        };
        RefreshPorts();
        UpdatePerformance();
        timer.Start();
    }

    private void Sampled(uint index, bool connected, Gamepad gamepad)
    {
        if (activeController is null && connected)
        {
            activeController = index;
            ControllerStatus.Text = $"● Controle {index + 1} conectado";
        }
        if (activeController != index) return;
        if (!connected)
        {
            gamepad = default;
            activeController = null;
            ControllerStatus.Text = "● Aguardando controle";
        }
        performance.Sample(connected, gamepad);
        foreach (var (button, indicator) in indicators)
            indicator.Background = Brush((gamepad.Buttons & button) != 0 ? "#631E1E" : "#2A2A2A");
        UpdateStick(LeftPuck, gamepad.LeftThumbX, gamepad.LeftThumbY, Gamepad.LeftThumbDeadZone, (gamepad.Buttons & GamepadButtons.LeftThumb) != 0, LeftValues);
        UpdateStick(RightPuck, gamepad.RightThumbX, gamepad.RightThumbY, Gamepad.RightThumbDeadZone, (gamepad.Buttons & GamepadButtons.RightThumb) != 0, RightValues);
        LTBar.Value = gamepad.LeftTrigger * 100.0 / 255;
        RTBar.Value = gamepad.RightTrigger * 100.0 / 255;
        LTLabel.Text = $"LT {LTBar.Value:0}%";
        RTLabel.Text = $"RT {RTBar.Value:0}% · {(preset.Settings.Mode == MusicalMode.Drum ? "SNARE" : "sem função")}";
        VelocityReadout.Text = $"LT {LTBar.Value:0}% · VEL {preset.CurrentVelocity}";
        RepeatReadout.Text = $"{(preset.IsRepeating ? "● REPETINDO" : "RB + pads")} · {preset.Settings.RepeatDivision}";
        ModePanel.ShowPerformance(gamepad.Buttons);
    }

    private static void UpdateStick(Ellipse puck, short rawX, short rawY, int deadZone, bool pressed, TextBlock label)
    {
        int x = ControllerReader.ThumbPercent(rawX, deadZone), y = ControllerReader.ThumbPercent(rawY, deadZone);
        Canvas.SetLeft(puck, 29 + x * 0.27);
        Canvas.SetTop(puck, 29 - y * 0.27);
        puck.Fill = Brush(pressed ? "#E68E0D" : "#555555");
        label.Text = $"X {x}% · Y {y}% (sem função)";
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
            performance.Stop();
            fl.Connect(false); feedback.Dispose();
            midi.Dispose();
            refreshing = true;
            var ports = MidiOutput.GetPorts();
            Ports.ItemsSource = ports;
            Ports.SelectedItem = ports.FirstOrDefault(p => p.Name == (oldName ?? preset.Settings.PreferredPort))
                ?? ports.FirstOrDefault(p => p.Name.Equals("X-MIDI", StringComparison.OrdinalIgnoreCase))
                ?? ports.FirstOrDefault(p => p.Name.Contains("loopMIDI", StringComparison.OrdinalIgnoreCase));
            var inputs = MidiInput.GetPorts();
            FeedbackPorts.ItemsSource = inputs;
            FeedbackPorts.SelectedItem = inputs.FirstOrDefault(p => p.Name == preset.Settings.FeedbackPort);
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
            performance.Stop();
            fl.Connect(false); feedback.Dispose();
            midi.Dispose();
            if (Ports.SelectedItem is not MidiPort port) return;
            midi.Open(port.Id);
            preset.Reset();
            OpenFeedback();
            if (preset.Settings.PreferredPort != port.Name)
            {
                preset.Settings.PreferredPort = port.Name;
                ScheduleSave();
            }
            MidiStatus.Text = $"Saída {port.Name} · retorno {(feedback.IsOpen ? "aberto" : "ausente")}";
        }
        catch (InvalidOperationException ex) { midi.Dispose(); MidiStatus.Text = ex.Message; }
    }

    private void SilenceClicked(object sender, RoutedEventArgs e)
    {
        try { performance.Stop(); }
        catch (InvalidOperationException ex) { midi.Dispose(); MidiStatus.Text = ex.Message; }
    }

    private void FeedbackSelected(object sender, SelectionChangedEventArgs e)
    { if (!refreshing && performance is not null) RunCommand(OpenFeedback); }
    private void OpenFeedback()
    {
        performance.Stop(); fl.Connect(false); feedback.Dispose();
        if (FeedbackPorts.SelectedItem is not MidiPort input || Ports.SelectedItem is not MidiPort output || !midi.IsOpen) return;
        if (input.Name.Equals(output.Name, StringComparison.OrdinalIgnoreCase))
        { MidiStatus.Text = "Escolha duas portas virtuais distintas para evitar loop"; return; }
        feedback.Open(input.Id);
        preset.Settings.FeedbackPort = input.Name; ScheduleSave();
        fl.Connect(true);
    }
    private void PanelClicked(object sender, RoutedEventArgs e) => RunCommand(performance.TogglePanel);
    private void PreparationOpened(object sender, RoutedEventArgs e)
    {
        monitorEnabled = true;
        if (performance is not null) RunCommand(performance.Stop);
    }
    private void RecordingPreparationChanged(object sender, RoutedEventArgs e)
    { if (performance is not null) performance.RecordingPrepared = RecordingPrepared.IsChecked == true; }
    private void ChannelsClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DrumChannel.Text, out int d) || !int.TryParse(SampleChannel.Text, out int s)
            || !int.TryParse(ChordChannel.Text, out int c) || new[] { d, s, c }.Any(v => v is < 1 or > 127))
        { SettingsStatus.Text = "Use canais FL de 1 a 127"; return; }
        RunCommand(performance.Stop);
        preset.Settings.ModeChannels = [d - 1, s - 1, c - 1];
        ScheduleSave(); UpdateMusicalControls();
        SettingsStatus.Text = "Canais salvos; confirme Modo / canal no painel do controle";
    }
    private void EffectsClicked(object sender, RoutedEventArgs e)
    { RunCommand(preset.PrepareEffects); UpdateMusicalControls(); }
    private void UpdatePerformance()
    {
        PerformanceStatus.Text = performance.Status;
        PerformanceHint.Text = performance.Message + " · " + fl.Notice;
        DedicatedReadout.Text = preset.Settings.Mode == MusicalMode.Drum
            ? $"LB · {preset.Settings.Banks[0][0].Name}    RT · {preset.Settings.Banks[0][1].Name}    {(preset.Settings.FullLevel ? "FULL LEVEL 127" : "LT · ghost notes")}" : "LT · dinâmica dos pads";
        CommandPanel.Visibility = performance.PanelOpen ? Visibility.Visible : Visibility.Collapsed;
        if (performance.PanelOpen)
        {
            var items = performance.MenuItems;
            string signature = string.Join("|", items);
            if (signature != menuSignature) { CommandList.ItemsSource = items; menuSignature = signature; }
            CommandList.SelectedIndex = performance.SelectedRow;
        }
        if (!performance.RecordingPrepared && RecordingPrepared.IsChecked == true) RecordingPrepared.IsChecked = false;
    }

    private void UpdateMusicalControls()
    {
        updatingMusic = true;
        try
        {
            BankLabel.Text = $"Banco {preset.ActiveBank + 1}";
            BankDescription.Text = $"{preset.Settings.Mode.ToString().ToUpperInvariant()} · BANCO {preset.ActiveBank + 1} · MIDI 1 · destino FL {preset.Settings.ModeChannels[(int)preset.Settings.Mode] + 1}";
            ModeSelector.SelectedIndex = (int)preset.Settings.Mode;
            ModeCaption.Text = preset.Settings.Mode switch
            { MusicalMode.Sample => "Waveform / slices", MusicalMode.Chord => "Acordes / notas MIDI", _ => "Entradas em tempo real" };
            MappingTitle.Text = preset.Settings.Mode switch
            { MusicalMode.Sample => "Slices nos pads", MusicalMode.Chord => "Acordes nos pads", _ => "Mapeamento de notas" };
            DrumRepeatControls.Visibility = preset.Settings.Mode == MusicalMode.Drum ? Visibility.Visible : Visibility.Collapsed;
            LeftEffectLabel.Text = preset.EffectsPrepared ? "X · ajuste relativo" : "EFEITOS DESATIVADOS";
            RightEffectLabel.Text = preset.EffectsPrepared ? "Centro mantém valor" : "Preparar para ativar";
            BankButton.Background = Brush(preset.ActiveBank == 0 ? "#E68E0D" : "#BEBEBE");
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
        RunCommand(() => performance.SelectMode((MusicalMode)ModeSelector.SelectedIndex));
        UpdateMusicalControls();
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
    private void PlayPauseClicked(object sender, RoutedEventArgs e) => RunCommand(performance.PlayStop);
    private void SwingDownClicked(object sender, RoutedEventArgs e) => RunCommand(() => preset.SetSwing(preset.Settings.SwingPercent - preset.Settings.SwingStep));
    private void SwingUpClicked(object sender, RoutedEventArgs e) => RunCommand(() => preset.SetSwing(preset.Settings.SwingPercent + preset.Settings.SwingStep));
    private void SwingValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updatingMusic && preset is not null) RunCommand(() => preset.SetSwing((int)Math.Round(e.NewValue)));
    }

    private async void ConfigureClicked(object sender, RoutedEventArgs e)
    {
        RunCommand(performance.Stop);
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
            SettingsStatus.Text = "Configurações salvas · Menu transporte · View comandos · ←/→ banco";
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
        try { performance.Stop(); }
        catch (InvalidOperationException) { }
        finally
        {
            try { preset.Dispose(); }
            catch (InvalidOperationException) { }
            finally { fl.Connect(false); feedback.Dispose(); midi.Dispose(); }
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
