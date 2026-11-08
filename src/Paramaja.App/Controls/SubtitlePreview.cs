using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Paramaja.App.Controls;

/// <summary>
/// A line of subtitle as the player will draw it: the font, colours, outline, shadow and box of
/// Settings → Subtitles, over a dark strip standing in for the video. Sizes follow mpv's (an
/// outline of 2.5 on a 44-point font at 720p), scaled to the preview's font.
/// </summary>
public sealed class SubtitlePreview : Control
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<SubtitlePreview, string>(nameof(Text), "The quick brown fox, at the player's size");
    public static readonly StyledProperty<string> FontNameProperty = AvaloniaProperty.Register<SubtitlePreview, string>(nameof(FontName), "sans-serif");
    public static readonly StyledProperty<bool> IsBoldProperty = AvaloniaProperty.Register<SubtitlePreview, bool>(nameof(IsBold));
    public static readonly StyledProperty<string> TextColorProperty = AvaloniaProperty.Register<SubtitlePreview, string>(nameof(TextColor), "#FFFFFF");
    public static readonly StyledProperty<string> OutlineColorProperty = AvaloniaProperty.Register<SubtitlePreview, string>(nameof(OutlineColor), "#000000");
    public static readonly StyledProperty<double> OutlineProperty = AvaloniaProperty.Register<SubtitlePreview, double>(nameof(Outline), 2.5);
    public static readonly StyledProperty<double> ShadowProperty = AvaloniaProperty.Register<SubtitlePreview, double>(nameof(Shadow));
    public static readonly StyledProperty<bool> HasBoxProperty = AvaloniaProperty.Register<SubtitlePreview, bool>(nameof(HasBox));
    public static readonly StyledProperty<double> BoxOpacityProperty = AvaloniaProperty.Register<SubtitlePreview, double>(nameof(BoxOpacity), 0.6);
    public static readonly StyledProperty<double> ScaleProperty = AvaloniaProperty.Register<SubtitlePreview, double>(nameof(Scale), 1);

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string FontName { get => GetValue(FontNameProperty); set => SetValue(FontNameProperty, value); }
    public bool IsBold { get => GetValue(IsBoldProperty); set => SetValue(IsBoldProperty, value); }
    public string TextColor { get => GetValue(TextColorProperty); set => SetValue(TextColorProperty, value); }
    public string OutlineColor { get => GetValue(OutlineColorProperty); set => SetValue(OutlineColorProperty, value); }
    public double Outline { get => GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    public double Shadow { get => GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }
    public bool HasBox { get => GetValue(HasBoxProperty); set => SetValue(HasBoxProperty, value); }
    public double BoxOpacity { get => GetValue(BoxOpacityProperty); set => SetValue(BoxOpacityProperty, value); }
    public double Scale { get => GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    static SubtitlePreview()
    {
        AffectsRender<SubtitlePreview>(TextProperty, FontNameProperty, IsBoldProperty, TextColorProperty, OutlineColorProperty,
            OutlineProperty, ShadowProperty, HasBoxProperty, BoxOpacityProperty, ScaleProperty);
        HeightProperty.OverrideDefaultValue<SubtitlePreview>(96);
    }

    /// <summary>The preview's font at a scale of 1, standing for mpv's 44 at 720p.</summary>
    private const double BaseSize = 24;
    private const double MpvBase = 44;

    private static Color Parse(string hex, Color fallback) => Color.TryParse(hex, out var c) ? c : fallback;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#3A3442"), 0), new GradientStop(Color.Parse("#0E0B12"), 1) },
        }, bounds, 6);

        var size = BaseSize * Math.Clamp(Scale, 0.25, 4);
        var px = size / MpvBase; // one of mpv's units here
        var family = string.IsNullOrWhiteSpace(FontName) || FontName == "sans-serif" ? FontFamily.Default : new FontFamily(FontName);
        var typeface = new Typeface(family, FontStyle.Normal, IsBold ? FontWeight.Bold : FontWeight.Normal);
        var fill = new SolidColorBrush(Parse(TextColor, Colors.White));
        var text = new FormattedText(Text ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, fill)
        {
            MaxTextWidth = Math.Max(10, bounds.Width - 24),
            TextAlignment = TextAlignment.Center,
        };
        var origin = new Point(12, Math.Max(4, bounds.Height - text.Height - 14));
        var geometry = text.BuildGeometry(origin);
        if (geometry is null) return;

        if (HasBox)
        {
            var box = geometry.Bounds.Inflate(new Thickness(10 * px, 4 * px));
            context.FillRectangle(new SolidColorBrush(Colors.Black, Math.Clamp(BoxOpacity, 0, 1)), box);
        }
        if (Shadow > 0)
        {
            using (context.PushTransform(Matrix.CreateTranslation(Shadow * px, Shadow * px)))
                context.DrawGeometry(new SolidColorBrush(Colors.Black, 0.6), null, geometry);
        }
        if (!HasBox && Outline > 0)
        {
            // The stroke is centred on the glyphs' edge: twice the outline, half of it under the fill.
            var pen = new Pen(new SolidColorBrush(Parse(OutlineColor, Colors.Black)), Outline * px * 2, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, geometry);
        }
        context.DrawGeometry(fill, null, geometry);
    }
}
