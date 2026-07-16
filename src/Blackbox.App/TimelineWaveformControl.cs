using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Blackbox.Domain;

namespace Blackbox.App;

public sealed class TimelineWaveformControl : FrameworkElement
{
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
    private static readonly Brush WaveformBrush = new SolidColorBrush(Color.FromRgb(108, 184, 255));
    private static readonly Brush ProtectedBrush = new SolidColorBrush(Color.FromArgb(115, 58, 139, 90));
    private static readonly Brush DamagedBrush = new SolidColorBrush(Color.FromArgb(150, 182, 77, 77));
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255));
    private static readonly Brush SegmentOddBrush = new SolidColorBrush(Color.FromRgb(38, 42, 46));
    private static readonly Brush SegmentEvenBrush = new SolidColorBrush(Color.FromRgb(30, 34, 37));
    private static readonly Pen BoundaryPen = new(new SolidColorBrush(Color.FromRgb(120, 120, 120)), 1);
    private static readonly Pen EmphasizedBoundaryPen = new(new SolidColorBrush(Color.FromRgb(218, 222, 224)), 2);
    private static readonly Pen MarkerPen = new(new SolidColorBrush(Color.FromRgb(244, 201, 93)), 2);
    private static readonly Pen CursorPen = new(Brushes.White, 2);

    static TimelineWaveformControl()
    {
        foreach (var freezable in new Freezable[]
        {
            BackgroundBrush,
            WaveformBrush,
            ProtectedBrush,
            DamagedBrush,
            SelectionBrush,
            SegmentOddBrush,
            SegmentEvenBrush,
            BoundaryPen,
            EmphasizedBoundaryPen,
            MarkerPen,
            CursorPen
        })
        {
            freezable.Freeze();
        }
    }

    public TimeSpan Duration { get; set; }
    public IReadOnlyList<double> Samples { get; set; } = [];
    public IReadOnlyList<TimeSpan> SegmentBoundaries { get; set; } = [];
    public IReadOnlyList<TimelineDisplayRange> ProtectedRanges { get; set; } = [];
    public IReadOnlyList<TimelineDisplayRange> DamagedRanges { get; set; } = [];
    public IReadOnlyList<TimelineMarker> Markers { get; set; } = [];
    public TimeSpan SelectionStart { get; set; }
    public TimeSpan SelectionEnd { get; set; }
    public TimeSpan CursorPosition { get; set; }
    public bool ShowSegmentBands { get; set; }

    public event EventHandler<TimeSpan>? ScrubRequested;

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(BackgroundBrush, null, bounds);
        if (Duration <= TimeSpan.Zero || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        if (ShowSegmentBands)
        {
            DrawSegmentBands(drawingContext);
        }

        foreach (var range in ProtectedRanges)
        {
            DrawRange(drawingContext, range, ProtectedBrush);
        }

        foreach (var range in DamagedRanges)
        {
            DrawRange(drawingContext, range, DamagedBrush);
        }

        var center = ActualHeight / 2;
        if (Samples.Count > 0)
        {
            var pen = new Pen(WaveformBrush, Math.Max(1, ActualWidth / Samples.Count));
            pen.Freeze();
            for (var index = 0; index < Samples.Count; index++)
            {
                var x = Samples.Count == 1 ? 0 : index * ActualWidth / (Samples.Count - 1);
                var halfHeight = Math.Clamp(Samples[index], 0, 1) * (ActualHeight * 0.42);
                drawingContext.DrawLine(pen, new Point(x, center - halfHeight), new Point(x, center + halfHeight));
            }
        }

        if (SelectionEnd > SelectionStart)
        {
            DrawRange(
                drawingContext,
                new TimelineDisplayRange(SelectionStart, SelectionEnd),
                SelectionBrush);
        }

        foreach (var boundary in SegmentBoundaries)
        {
            var x = ToX(boundary);
            drawingContext.DrawLine(
                ShowSegmentBands ? EmphasizedBoundaryPen : BoundaryPen,
                new Point(x, 0),
                new Point(x, ActualHeight));
        }

        foreach (var marker in Markers)
        {
            var x = ToX(marker.Offset);
            drawingContext.DrawLine(MarkerPen, new Point(x, 0), new Point(x, 15));
            if (ShowSegmentBands)
            {
                drawingContext.DrawGeometry(MarkerPen.Brush, null, CreateMarkerPin(x));
            }
        }

        var cursorX = ToX(CursorPosition);
        drawingContext.DrawLine(CursorPen, new Point(cursorX, 0), new Point(cursorX, ActualHeight));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        RequestScrub(e.GetPosition(this).X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            RequestScrub(e.GetPosition(this).X);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private void RequestScrub(double x)
    {
        if (Duration <= TimeSpan.Zero || ActualWidth <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(x / ActualWidth, 0, 1);
        CursorPosition = TimeSpan.FromTicks((long)(Duration.Ticks * ratio));
        InvalidateVisual();
        ScrubRequested?.Invoke(this, CursorPosition);
    }

    private void DrawRange(DrawingContext drawingContext, TimelineDisplayRange range, Brush brush)
    {
        var startX = ToX(range.Start);
        var endX = ToX(range.End);
        drawingContext.DrawRectangle(
            brush,
            null,
            new Rect(startX, 0, Math.Max(1, endX - startX), ActualHeight));
    }

    private void DrawSegmentBands(DrawingContext drawingContext)
    {
        var starts = new[] { TimeSpan.Zero }.Concat(SegmentBoundaries).ToArray();
        var ends = SegmentBoundaries.Concat([Duration]).ToArray();
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var index = 0; index < starts.Length; index++)
        {
            var startX = ToX(starts[index]);
            var endX = ToX(ends[index]);
            var width = Math.Max(1, endX - startX);
            drawingContext.DrawRectangle(
                index % 2 == 0 ? SegmentOddBrush : SegmentEvenBrush,
                null,
                new Rect(startX, 0, width, ActualHeight));
            if (width < 32)
            {
                continue;
            }

            var label = new FormattedText(
                $"S{index + 1}",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                10,
                Brushes.LightGray,
                pixelsPerDip);
            drawingContext.DrawText(label, new Point(startX + 6, Math.Max(2, ActualHeight - label.Height - 3)));
        }
    }

    private static Geometry CreateMarkerPin(double x)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(x - 5, 0), true, true);
            context.LineTo(new Point(x + 5, 0), true, false);
            context.LineTo(new Point(x, 7), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private double ToX(TimeSpan value)
    {
        return Math.Clamp(value.TotalSeconds / Duration.TotalSeconds, 0, 1) * ActualWidth;
    }
}

public sealed record TimelineDisplayRange(TimeSpan Start, TimeSpan End);
