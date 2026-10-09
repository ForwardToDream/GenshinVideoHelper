using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GenshinVideoHelper.Core.Progress;

namespace GenshinVideoHelper.App.Controls;

/// <summary>An episode's timeline: watched spans, the resume point and, when interactive, a draggable selection.</summary>
public sealed class WatchTimeline : FrameworkElement
{
    private const double DragThreshold = 3;
    private double _anchor;
    private bool _dragging;

    public static readonly DependencyProperty SegmentsProperty = Register<IReadOnlyList<WatchSegment>?>(nameof(Segments), null);
    public static readonly DependencyProperty DurationProperty = Register(nameof(Duration), 0d);
    public static readonly DependencyProperty PositionProperty = Register(nameof(Position), double.NaN);
    public static readonly DependencyProperty SelectionStartProperty = Register(nameof(SelectionStart), double.NaN);
    public static readonly DependencyProperty SelectionEndProperty = Register(nameof(SelectionEnd), double.NaN);
    public static readonly DependencyProperty IsInteractiveProperty = Register(nameof(IsInteractive), false);
    public static readonly DependencyProperty TrackBrushProperty = Register<Brush>(nameof(TrackBrush), Brushes.Gainsboro);
    public static readonly DependencyProperty WatchedBrushProperty = Register<Brush>(nameof(WatchedBrush), Brushes.DodgerBlue);
    public static readonly DependencyProperty SelectionBrushProperty = Register<Brush>(nameof(SelectionBrush), Brushes.Orange);
    public static readonly DependencyProperty MarkerBrushProperty = Register<Brush>(nameof(MarkerBrush), Brushes.Black);

    private static DependencyProperty Register<T>(string name, T value) =>
        DependencyProperty.Register(name, typeof(T), typeof(WatchTimeline), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<WatchSegment>? Segments { get => (IReadOnlyList<WatchSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    /// <summary>The resume point, or NaN for none.</summary>
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double SelectionStart { get => (double)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public double SelectionEnd { get => (double)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    public bool IsInteractive { get => (bool)GetValue(IsInteractiveProperty); set => SetValue(IsInteractiveProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush WatchedBrush { get => (Brush)GetValue(WatchedBrushProperty); set => SetValue(WatchedBrushProperty, value); }
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public Brush MarkerBrush { get => (Brush)GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }

    /// <summary>The user dragged out a span; read it from <see cref="SelectionStart"/> and <see cref="SelectionEnd"/>.</summary>
    public event EventHandler? SelectionChanged;
    /// <summary>The user clicked a single point, in seconds.</summary>
    public event Action<double>? TimeClicked;

    public bool HasSelection => double.IsFinite(SelectionStart) && double.IsFinite(SelectionEnd) && SelectionEnd > SelectionStart;

    public void Select(double start, double end)
    {
        SelectionStart = Math.Min(start, end);
        SelectionEnd = Math.Max(start, end);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection() => SelectionStart = SelectionEnd = double.NaN;

    protected override void OnRender(DrawingContext drawing)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var bounds = new Rect(0, 0, width, height);
        var radius = Math.Min(3, height / 2);
        drawing.DrawRoundedRectangle(TrackBrush, null, bounds, radius, radius);
        if (!(Duration > 0)) return;
        drawing.PushClip(new RectangleGeometry(bounds, radius, radius));
        foreach (var segment in Segments ?? [])
        {
            var left = X(segment.Start);
            drawing.DrawRectangle(WatchedBrush, null, new Rect(left, 0, Math.Max(1, X(segment.End) - left), height));
        }
        if (HasSelection)
        {
            var left = X(SelectionStart);
            var selection = new Rect(left, 0, Math.Max(2, X(SelectionEnd) - left), height);
            drawing.PushOpacity(0.35);
            drawing.DrawRectangle(SelectionBrush, null, selection);
            drawing.Pop();
            drawing.DrawRectangle(null, new Pen(SelectionBrush, 1.5), selection);
        }
        drawing.Pop();
        if (double.IsFinite(Position) && Position > 0) drawing.DrawRectangle(MarkerBrush, null, new Rect(Math.Clamp(X(Position) - 1, 0, width - 2), 0, 2, height));
    }

    private double X(double seconds) => Math.Clamp(seconds / Duration, 0, 1) * ActualWidth;
    private double TimeAt(double x) => Math.Round(Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * Duration);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsInteractive || !(Duration > 0)) return;
        _anchor = e.GetPosition(this).X;
        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var x = e.GetPosition(this).X;
        if (!_dragging && Math.Abs(x - _anchor) < DragThreshold) return;
        _dragging = true;
        Select(TimeAt(_anchor), TimeAt(x));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        if (!_dragging) TimeClicked?.Invoke(TimeAt(e.GetPosition(this).X));
        e.Handled = true;
    }
}
