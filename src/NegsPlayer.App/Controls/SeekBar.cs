using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NegsPlayer.Core.Playback;

namespace NegsPlayer.App.Controls;

/// <summary>Timeline with buffered range, hover time preview and click/drag seeking.</summary>
public sealed class SeekBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SeekBar, double>(nameof(Value));
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<SeekBar, double>(nameof(Maximum));
    public static readonly StyledProperty<double> BufferedProperty =
        AvaloniaProperty.Register<SeekBar, double>(nameof(Buffered));

    static SeekBar()
    {
        AffectsRender<SeekBar>(ValueProperty, MaximumProperty, BufferedProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Buffered { get => GetValue(BufferedProperty); set => SetValue(BufferedProperty, value); }

    /// <summary>Raised with the target time when the user clicks or finishes dragging.</summary>
    public event Action<double>? SeekRequested;

    private double? _hoverX;
    private double? _dragValue;

    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#40FFFFFF"));
    private static readonly IBrush BufferedBrush = new SolidColorBrush(Color.Parse("#70FFFFFF"));
    private static readonly IBrush Progress = new SolidColorBrush(Color.Parse("#C98BDB"));
    private static readonly IBrush Thumb = Brushes.White;
    private static readonly IBrush LabelBg = new SolidColorBrush(Color.Parse("#E6181320"));
    private static readonly Typeface Font = new("Inter");

    public SeekBar()
    {
        Height = 34;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private const double Pad = 8;

    internal double TimeAt(double x)
    {
        var w = Math.Max(1, Bounds.Width - 2 * Pad);
        return Math.Clamp((x - Pad) / w, 0, 1) * Maximum;
    }

    private double XAt(double time) =>
        Pad + (Maximum > 0 ? Math.Clamp(time / Maximum, 0, 1) : 0) * (Bounds.Width - 2 * Pad);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverX = e.GetPosition(this).X;
        if (_dragValue is not null) _dragValue = TimeAt(_hoverX.Value);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverX = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Maximum <= 0) return;
        _dragValue = TimeAt(e.GetPosition(this).X);
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragValue is { } v)
        {
            _dragValue = null;
            e.Pointer.Capture(null);
            SeekRequested?.Invoke(v);
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var hovering = _hoverX is not null || _dragValue is not null;
        var barH = hovering ? 6 : 4;
        var y = Bounds.Height - 12 - barH / 2.0;
        var value = _dragValue ?? Value;

        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // whole area is hit-testable
        ctx.DrawRectangle(Track, null, new RoundedRect(new Rect(Pad, y, w - 2 * Pad, barH), barH / 2.0));
        if (Maximum > 0)
        {
            var bx = XAt(Math.Max(Buffered, value));
            ctx.DrawRectangle(BufferedBrush, null, new RoundedRect(new Rect(Pad, y, Math.Max(0, bx - Pad), barH), barH / 2.0));
            var px = XAt(value);
            ctx.DrawRectangle(Progress, null, new RoundedRect(new Rect(Pad, y, Math.Max(0, px - Pad), barH), barH / 2.0));
            if (hovering) ctx.DrawEllipse(Thumb, null, new Point(px, y + barH / 2.0), 7, 7);
        }

        if (_hoverX is { } hx && Maximum > 0)
        {
            var text = new FormattedText(TimeFormat.Format(TimeAt(hx), Maximum >= 3600), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Font, 12, Brushes.White);
            var lw = text.Width + 12;
            var lx = Math.Clamp(hx - lw / 2, 0, Math.Max(0, w - lw));
            ctx.DrawRectangle(LabelBg, null, new RoundedRect(new Rect(lx, 0, lw, 18), 4));
            ctx.DrawText(text, new Point(lx + 6, 1));
        }
    }
}
