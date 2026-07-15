using System.Windows.Documents;
using System.Windows.Media;

namespace v2rayN.Base;

/// <summary>
/// Draws a thin horizontal insertion line at the top or bottom edge of the adorned
/// element (typically a DataGridRow), used to indicate where a dragged row will land.
/// </summary>
internal sealed class InsertionAdorner : Adorner
{
    private static readonly Pen LinePen = CreatePen();
    private static readonly Brush DotBrush = CreateDotBrush();

    public bool IsTopEdge { get; }

    public InsertionAdorner(UIElement adornedElement, bool isTopEdge) : base(adornedElement)
    {
        IsTopEdge = isTopEdge;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = AdornedElement.RenderSize.Width;
        if (width <= 0)
        {
            return;
        }

        var y = IsTopEdge ? 0d : AdornedElement.RenderSize.Height;

        drawingContext.DrawLine(LinePen, new Point(0, y), new Point(width, y));

        const double dotRadius = 3;
        drawingContext.DrawEllipse(DotBrush, null, new Point(dotRadius, y), dotRadius, dotRadius);
        drawingContext.DrawEllipse(DotBrush, null, new Point(width - dotRadius, y), dotRadius, dotRadius);
    }

    private static Pen CreatePen()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0, 122, 204));
        brush.Freeze();
        var pen = new Pen(brush, 2)
        {
            DashCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static Brush CreateDotBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0, 122, 204));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// A small translucent "ghost" tooltip that follows the mouse cursor while a routing
/// rule row is being dragged, showing the label of the row being moved.
/// </summary>
internal sealed class DragGhostAdorner : Adorner
{
    private const double OffsetX = 16;
    private const double OffsetY = 10;
    private const double Padding = 6;

    private static readonly Brush BackgroundBrush = CreateBackgroundBrush();
    private static readonly Brush TextBrush = CreateTextBrush();
    private static readonly Pen BorderPen = CreateBorderPen();

    private readonly string _text;
    private Point _position;

    public DragGhostAdorner(UIElement adornedElement, string text) : base(adornedElement)
    {
        _text = text;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Opacity = 0.9;
    }

    public void UpdatePosition(Point position)
    {
        _position = position;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (string.IsNullOrWhiteSpace(_text))
        {
            return;
        }

        var formattedText = new FormattedText(
            _text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            12,
            TextBrush,
            VisualTreeHelper.GetDpi(AdornedElement).PixelsPerDip);

        var rectSize = new Size(formattedText.Width + Padding * 2, formattedText.Height + Padding);
        var origin = new Point(_position.X + OffsetX, _position.Y + OffsetY);
        var rect = new Rect(origin, rectSize);

        drawingContext.DrawRoundedRectangle(BackgroundBrush, BorderPen, rect, 4, 4);
        drawingContext.DrawText(formattedText, new Point(rect.X + Padding, rect.Y + Padding / 2));
    }

    private static Brush CreateBackgroundBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(210, 45, 45, 48));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateTextBrush()
    {
        var brush = new SolidColorBrush(Colors.White);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateBorderPen()
    {
        var brush = new SolidColorBrush(Color.FromArgb(220, 0, 122, 204));
        brush.Freeze();
        var pen = new Pen(brush, 1);
        pen.Freeze();
        return pen;
    }
}
