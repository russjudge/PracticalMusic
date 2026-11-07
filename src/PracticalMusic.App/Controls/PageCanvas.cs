using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PracticalMusic.Core.Models;
using PracticalMusic.Core.Timing;

namespace PracticalMusic.App.Controls;

/// <summary>Argument for taps and marker drags: normalized page coordinates plus the control's Tag.</summary>
public sealed class PageTap
{
    public double X { get; init; }
    public double Y { get; init; }
    public object? Tag { get; init; }
    public string? MarkerId { get; init; }
}

/// <summary>
/// Draws one PDF page scaled to fit, plus the playback highlight band and the time markers.
/// Taps are reported in normalized page coordinates so they work at any zoom level.
/// </summary>
public class PageCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> PageProperty =
        AvaloniaProperty.Register<PageCanvas, Bitmap?>(nameof(Page));

    public static readonly StyledProperty<HighlightRegion?> HighlightProperty =
        AvaloniaProperty.Register<PageCanvas, HighlightRegion?>(nameof(Highlight));

    public static readonly StyledProperty<IEnumerable<TimeMarker>?> MarkersProperty =
        AvaloniaProperty.Register<PageCanvas, IEnumerable<TimeMarker>?>(nameof(Markers));

    public static readonly StyledProperty<bool> ShowMarkersProperty =
        AvaloniaProperty.Register<PageCanvas, bool>(nameof(ShowMarkers));

    public static readonly StyledProperty<string?> SelectedMarkerIdProperty =
        AvaloniaProperty.Register<PageCanvas, string?>(nameof(SelectedMarkerId));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<PageCanvas, string?>(nameof(Label));

    public static readonly StyledProperty<ICommand?> TapCommandProperty =
        AvaloniaProperty.Register<PageCanvas, ICommand?>(nameof(TapCommand));

    public static readonly StyledProperty<ICommand?> MarkerMovedCommandProperty =
        AvaloniaProperty.Register<PageCanvas, ICommand?>(nameof(MarkerMovedCommand));

    public static readonly StyledProperty<bool> AllowMarkerDragProperty =
        AvaloniaProperty.Register<PageCanvas, bool>(nameof(AllowMarkerDrag));

    public static readonly StyledProperty<Color> HighlightColorProperty =
        AvaloniaProperty.Register<PageCanvas, Color>(nameof(HighlightColor), Color.FromArgb(90, 255, 200, 0));

    static PageCanvas()
    {
        AffectsRender<PageCanvas>(PageProperty, HighlightProperty, MarkersProperty, ShowMarkersProperty,
            SelectedMarkerIdProperty, LabelProperty, HighlightColorProperty);
        AffectsMeasure<PageCanvas>(PageProperty);
    }

    public PageCanvas()
    {
        ClipToBounds = true;
    }

    public Bitmap? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }
    public HighlightRegion? Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public IEnumerable<TimeMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public bool ShowMarkers { get => GetValue(ShowMarkersProperty); set => SetValue(ShowMarkersProperty, value); }
    public string? SelectedMarkerId { get => GetValue(SelectedMarkerIdProperty); set => SetValue(SelectedMarkerIdProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public ICommand? TapCommand { get => GetValue(TapCommandProperty); set => SetValue(TapCommandProperty, value); }
    public ICommand? MarkerMovedCommand { get => GetValue(MarkerMovedCommandProperty); set => SetValue(MarkerMovedCommandProperty, value); }
    public bool AllowMarkerDrag { get => GetValue(AllowMarkerDragProperty); set => SetValue(AllowMarkerDragProperty, value); }
    public Color HighlightColor { get => GetValue(HighlightColorProperty); set => SetValue(HighlightColorProperty, value); }

    public event EventHandler<PageTap>? PageTapped;

    private Point _pressPoint;
    private bool _pressed;
    private TimeMarker? _dragMarker;
    private Point _dragPoint;

    /// <summary>Rectangle (in control coordinates) that the page occupies.</summary>
    public Rect PageRect
    {
        get
        {
            var page = Page;
            var bounds = Bounds;
            if (page == null || bounds.Width <= 0 || bounds.Height <= 0) return new Rect(0, 0, bounds.Width, bounds.Height);
            var size = page.Size;
            var scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);
            var w = size.Width * scale;
            var h = size.Height * scale;
            return new Rect((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var page = Page;
        if (page == null)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), bounds);
            if (!string.IsNullOrEmpty(Label))
                DrawText(context, Label, bounds.Center, 16, Brushes.Gray, centered: true);
            return;
        }

        var rect = PageRect;
        context.DrawImage(page, new Rect(page.Size), rect);

        if (Highlight is { } hl)
        {
            var hr = new Rect(rect.X + hl.X1 * rect.Width, rect.Y + hl.Y1 * rect.Height,
                Math.Max(2, hl.Width * rect.Width), Math.Max(2, hl.Height * rect.Height));
            context.FillRectangle(new SolidColorBrush(HighlightColor), hr, 4);
            var border = Color.FromArgb(200, HighlightColor.R, HighlightColor.G, HighlightColor.B);
            context.DrawRectangle(new Pen(new SolidColorBrush(border), 2), hr, 4);
        }

        if (ShowMarkers && Markers != null)
        {
            var selected = SelectedMarkerId;
            foreach (var m in Markers)
            {
                var p = new Point(rect.X + m.X * rect.Width, rect.Y + m.Y * rect.Height);
                var isSel = selected != null && m.Id == selected;
                var r = isSel ? 9.0 : 7.0;
                var fill = isSel ? Colors.OrangeRed : Color.FromArgb(220, 30, 120, 255);
                context.DrawEllipse(new SolidColorBrush(fill), new Pen(Brushes.White, 2), p, r, r);
                var text = string.IsNullOrEmpty(m.Label) ? TimingEngine.FormatTime(m.TimeMs) : m.Label;
                DrawText(context, text, new Point(p.X + r + 3, p.Y - 8), 11, new SolidColorBrush(fill), centered: false, background: true);
            }
        }

        if (!string.IsNullOrEmpty(Label))
            DrawText(context, Label, new Point(rect.X + 6, rect.Y + 4), 12, Brushes.Gray, centered: false, background: true);
    }

    private static void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush, bool centered, bool background = false)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter, Segoe UI, Roboto, sans-serif"), size, brush);
        var origin = centered ? new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2) : at;
        if (background)
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
                new Rect(origin.X - 2, origin.Y - 1, ft.Width + 4, ft.Height + 2), 2);
        context.DrawText(ft, origin);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var page = Page;
        if (page == null) return new Size(
            double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height);
        var w = double.IsInfinity(availableSize.Width) ? page.Size.Width : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? page.Size.Height : availableSize.Height;
        if (double.IsInfinity(availableSize.Height) && !double.IsInfinity(availableSize.Width))
            h = w * page.Size.Height / page.Size.Width;
        else if (double.IsInfinity(availableSize.Width) && !double.IsInfinity(availableSize.Height))
            w = h * page.Size.Width / page.Size.Height;
        return new Size(w, h);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Pointer.Type == PointerType.Mouse) return;
        _pressPoint = e.GetPosition(this);
        _pressed = true;
        _dragMarker = null;
        if (AllowMarkerDrag && ShowMarkers && Markers != null)
        {
            var rect = PageRect;
            foreach (var m in Markers)
            {
                var p = new Point(rect.X + m.X * rect.Width, rect.Y + m.Y * rect.Height);
                if ((p - _pressPoint).Length() <= 14)
                {
                    _dragMarker = m;
                    _dragPoint = _pressPoint;
                    e.Pointer.Capture(this);
                    break;
                }
            }
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragMarker == null) return;
        _dragPoint = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        var pos = e.GetPosition(this);
        var rect = PageRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        if (_dragMarker != null)
        {
            e.Pointer.Capture(null);
            var marker = _dragMarker;
            _dragMarker = null;
            if ((pos - _pressPoint).Length() > 6)
            {
                var nx = Math.Clamp((pos.X - rect.X) / rect.Width, 0, 1);
                var ny = Math.Clamp((pos.Y - rect.Y) / rect.Height, 0, 1);
                var arg = new PageTap { X = nx, Y = ny, Tag = Tag, MarkerId = marker.Id };
                if (MarkerMovedCommand?.CanExecute(arg) == true) MarkerMovedCommand.Execute(arg);
                return;
            }
            // a click on a marker without dragging: treat as a tap on the marker
            var tapOnMarker = new PageTap { X = marker.X, Y = marker.Y, Tag = Tag, MarkerId = marker.Id };
            PageTapped?.Invoke(this, tapOnMarker);
            if (TapCommand?.CanExecute(tapOnMarker) == true) TapCommand.Execute(tapOnMarker);
            return;
        }

        if ((pos - _pressPoint).Length() > 12) return; // a scroll/drag, not a tap
        if (!rect.Contains(pos)) return;
        var x = (pos.X - rect.X) / rect.Width;
        var y = (pos.Y - rect.Y) / rect.Height;
        var tap = new PageTap { X = x, Y = y, Tag = Tag };
        PageTapped?.Invoke(this, tap);
        if (TapCommand?.CanExecute(tap) == true) TapCommand.Execute(tap);
    }
}

internal static class PointExtensions
{
    public static double Length(this Point p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
}
