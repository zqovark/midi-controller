using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace XMidi;

public sealed class WaveformView : FrameworkElement
{
    private (float Min, float Max)[] envelope = [];
    private SampleSettings settings = new();
    private int frames = 1;
    private bool dragging, endMarker;
    internal int SelectedRegion { get; set; }
    internal event Action<int>? RegionSelected;
    internal event Action<int, bool, double>? MarkerMoved;
    internal void SetAudio(SampleAudio? audio, (float Min, float Max)[]? values = null)
    { frames = audio?.Frames ?? 1; envelope = values ?? []; InvalidateVisual(); }
    internal void SetSettings(SampleSettings value)
    { settings = value; SelectedRegion = Math.Clamp(SelectedRegion, 0, value.Regions.Length - 1); InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 18, 24)), null, new Rect(0, 0, width, height));
        if (width <= 0 || height <= 0) return;
        var wave = new Pen(new SolidColorBrush(Color.FromRgb(176, 198, 213)), 1);
        for (int i = 0; i < envelope.Length; i++)
        {
            double x = i * width / envelope.Length;
            dc.DrawLine(wave, new Point(x, height * (.5 - envelope[i].Max * .38)), new Point(x, height * (.5 - envelope[i].Min * .38)));
        }
        for (int i = 0; i < settings.Regions.Length; i++)
        {
            var region = settings.Regions[i]; double start = region.Start * width, end = region.End * width;
            bool selected = i == SelectedRegion;
            var color = selected ? Color.FromArgb(65, 208, 188, 255) : Color.FromArgb(16, 208, 188, 255);
            var marker = new Pen(selected ? Brushes.Plum : Brushes.SlateGray, selected ? 2 : 1);
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(start, 0, Math.Max(0, end - start), height));
            dc.DrawLine(marker, new(start, 0), new(start, height)); dc.DrawLine(marker, new(end, 0), new(end, height));
            string[] letters = ["A", "B", "X", "Y"];
            string pads = string.Join(",", settings.Pads.Select((p, index) => (p, index)).Where(p => p.p.Region == i)
                .Select(p => $"{p.index / 4 + 1}{letters[p.index % 4]}"));
            var text = new FormattedText($"{i + 1} {pads}", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Consolas"), 10, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.PushClip(new RectangleGeometry(new Rect(start, 0, Math.Max(0, end - start), height)));
            dc.DrawText(text, new Point(start + 4, selected ? 3 : height - 16)); dc.Pop();
        }
        if (envelope.Length == 0)
            dc.DrawText(new FormattedText("Selecione um WAV para editar os slices", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Consolas"), 12, Brushes.LightGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(12, height / 2));
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (envelope.Length == 0 || ActualWidth <= 0) return;
        double x = e.GetPosition(this).X;
        var selected = settings.Regions[SelectedRegion];
        double startDistance = Math.Abs(x - selected.Start * ActualWidth), endDistance = Math.Abs(x - selected.End * ActualWidth);
        if (Math.Min(startDistance, endDistance) <= 9)
        {
            endMarker = endDistance < startDistance; dragging = true; CaptureMouse();
        }
        else
        {
            int index = Array.FindIndex(settings.Regions, r => x / ActualWidth >= r.Start && x / ActualWidth <= r.End);
            if (index >= 0) { SelectedRegion = index; RegionSelected?.Invoke(index); InvalidateVisual(); }
        }
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging || ActualWidth <= 0) return;
        double position = Math.Round(Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1) * frames) / frames;
        var r = settings.Regions[SelectedRegion];
        position = endMarker ? Math.Clamp(position, r.Start + 1.0 / frames, 1) : Math.Clamp(position, 0, r.End - 1.0 / frames);
        MarkerMoved?.Invoke(SelectedRegion, endMarker, position);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonUp(e); dragging = false; ReleaseMouseCapture(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); dragging = false; }
}
