using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace XMidi;

public sealed class StepSequenceGrid : FrameworkElement
{
    internal const double CellWidth = 24, RowHeight = 27, HeaderHeight = 22;
    private SequencePattern pattern = new();
    private int swing;
    private double playTick = -1, viewStart, viewWidth = 600;
    private Guid? selected;
    internal event Action<int, long, Guid?, bool>? NoteClicked;
    public StepSequenceGrid() { Width = 16 * CellWidth; Height = HeaderHeight + 8 * RowHeight; }
    internal void SetPattern(SequencePattern value, int swingValue, Guid? selection)
    { pattern = value; swing = swingValue; selected = selection; Width = value.Bars * value.StepsPerBar * CellWidth; Height = HeaderHeight + 8 * RowHeight; InvalidateVisual(); }
    internal void SetViewport(double offset, double width) { viewStart = offset; viewWidth = width; InvalidateVisual(); }
    internal void SetPlayhead(double tick) { playTick = tick; InvalidateVisual(); }
    private double X(double tick) => tick / pattern.Length * pattern.Bars * pattern.StepsPerBar * CellWidth;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        int count = pattern.StepsPerBar * pattern.Bars;
        int first = Math.Max(0, (int)(viewStart / CellWidth)), last = Math.Min(count - 1, (int)Math.Ceiling((viewStart + viewWidth) / CellWidth));
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Width, Height));
        for (int col = first; col <= last; col++)
        {
            int step = col % pattern.StepsPerBar;
            var background = new SolidColorBrush(Color.FromRgb((byte)(step % 4 == 0 ? 53 : 32), (byte)(step % 4 == 0 ? 47 : 29), (byte)(step % 4 == 0 ? 62 : 38)));
            dc.DrawRectangle(background, new Pen(Brushes.DimGray, .3), new Rect(col * CellWidth, HeaderHeight, CellWidth, 8 * RowHeight));
            Text(dc, (step + 1).ToString(), col * CellWidth + 4, 3, Brushes.LightGray, 9);
        }
        for (int row = 0; row < 8; row++)
        {
            dc.DrawLine(new Pen(Brushes.DimGray, .4), new Point(viewStart, HeaderHeight + (row + 1) * RowHeight), new Point(viewStart + viewWidth, HeaderHeight + (row + 1) * RowHeight));
            foreach (var note in pattern.Tracks.Single(t => t.Instrument == row).Events)
            {
                double x = X(SequenceTiming.PlayTick(note, pattern, swing));
                double width = Math.Max(6, Math.Min(X(note.Duration), Width - x - 1));
                if (x + width < viewStart || x > viewStart + viewWidth) continue;
                double height = 5 + note.Velocity / 127.0 * 18;
                var rect = new Rect(x + 1, HeaderHeight + (row + 1) * RowHeight - height - 1, Math.Max(2, width - 2), height);
                dc.DrawRoundedRectangle(note.Id == selected ? Brushes.Plum : new SolidColorBrush(Color.FromRgb(157, 137, 197)),
                    new Pen(note.Id == selected ? Brushes.White : Brushes.MediumPurple, 1), rect, 2, 2);
            }
        }
        if (playTick >= 0)
        {
            double x = X(playTick); dc.DrawLine(new Pen(Brushes.LightGoldenrodYellow, 2), new Point(x, 0), new Point(x, Height));
        }
    }
    private void Text(DrawingContext dc, string text, double x, double y, Brush brush, double size) => dc.DrawText(
        new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        var pos = e.GetPosition(this); int row = (int)((pos.Y - HeaderHeight) / RowHeight);
        if (pos.Y < HeaderHeight || row is < 0 or > 7) return;
        int column = Math.Clamp((int)(pos.X / CellWidth), 0, pattern.Bars * pattern.StepsPerBar - 1);
        long tick = (long)Math.Round(column * pattern.Length / (double)(pattern.Bars * pattern.StepsPerBar));
        // Select by the actual note position/duration; never round an existing note into a cell.
        var hits = pattern.Tracks.Single(t => t.Instrument == row).Events.Where(n =>
        {
            double x = X(SequenceTiming.PlayTick(n, pattern, swing));
            return pos.X >= x && pos.X <= x + Math.Max(7, X(n.Duration));
        }).OrderBy(n => n.OriginalTick).ThenBy(n => n.Id).ToArray();
        Guid? id = null;
        if (hits.Length != 0)
        {
            int index = Array.FindIndex(hits, n => n.Id == selected);
            id = hits[(index + 1) % hits.Length].Id;
        }
        NoteClicked?.Invoke(row, tick, id, e.ChangedButton == MouseButton.Right);
        e.Handled = true;
    }
}
