using System.Windows;
using System.Windows.Media;

namespace Dropshelf.Activities.Weather;

/// <summary>
/// A small vector weather picture (sun, moon, clouds, rain, snow, thunder, fog) drawn on a 32 x 32 grid and scaled to
/// <see cref="FrameworkElement.Width"/>. Drawn rather than taken from an icon font so every condition has a matching glyph on
/// every Windows version.
/// </summary>
internal sealed class WeatherIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(WeatherIconKind), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(WeatherIconKind.Cloud, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush SunBrush = Frozen(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush MoonBrush = Frozen(Color.FromRgb(0xE2, 0xE8, 0xF0));
    private static readonly Brush CloudBrush = Frozen(Color.FromRgb(0xCB, 0xD5, 0xE1));
    private static readonly Brush DarkCloudBrush = Frozen(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly Brush RainBrush = Frozen(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly Brush SnowBrush = Frozen(Color.FromRgb(0xF1, 0xF5, 0xF9));
    private static readonly Pen SunRay = FrozenPen(SunBrush, 2);
    private static readonly Pen RainPen = FrozenPen(RainBrush, 2);
    private static readonly Pen FogPen = FrozenPen(CloudBrush, 2.4);

    private static readonly Geometry CloudShape = Frozen(new GeometryGroup
    {
        Children =
        {
            new EllipseGeometry(new Point(11, 15), 5.5, 5.5),
            new EllipseGeometry(new Point(17.5, 11.5), 7, 7),
            new EllipseGeometry(new Point(24, 16), 5, 5),
            new RectangleGeometry(new Rect(7, 15, 22, 7), 3.5, 3.5),
        },
    });

    public WeatherIcon(double size = 28)
    {
        Width = size;
        Height = size;
        IsHitTestVisible = false;
    }

    public WeatherIconKind Kind
    {
        get => (WeatherIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double scale = Width / 32.0;
        dc.PushTransform(new ScaleTransform(scale, scale));
        switch (Kind)
        {
            case WeatherIconKind.Sun:
                DrawSun(dc, new Point(16, 16), 6.5, 9.5, 13.5);
                break;
            case WeatherIconKind.Moon:
                DrawMoon(dc, new Point(16, 16));
                break;
            case WeatherIconKind.PartlyDay:
                DrawSun(dc, new Point(11, 11), 5, 7.5, 10);
                DrawCloud(dc, 4, 6, CloudBrush);
                break;
            case WeatherIconKind.PartlyNight:
                DrawMoon(dc, new Point(11, 11), 0.65);
                DrawCloud(dc, 4, 6, CloudBrush);
                break;
            case WeatherIconKind.Fog:
                DrawCloud(dc, 0, -3, CloudBrush);
                dc.DrawLine(FogPen, new Point(7, 23), new Point(25, 23));
                dc.DrawLine(FogPen, new Point(10, 28), new Point(28, 28));
                break;
            case WeatherIconKind.Drizzle:
                DrawCloud(dc, 0, -3, DarkCloudBrush);
                dc.DrawLine(RainPen, new Point(11, 24), new Point(10, 27));
                dc.DrawLine(RainPen, new Point(17, 24), new Point(16, 27));
                dc.DrawLine(RainPen, new Point(23, 24), new Point(22, 27));
                break;
            case WeatherIconKind.Rain:
                DrawCloud(dc, 0, -3, DarkCloudBrush);
                dc.DrawLine(RainPen, new Point(11, 24), new Point(8.5, 30));
                dc.DrawLine(RainPen, new Point(17, 24), new Point(14.5, 30));
                dc.DrawLine(RainPen, new Point(23, 24), new Point(20.5, 30));
                break;
            case WeatherIconKind.Snow:
                DrawCloud(dc, 0, -3, DarkCloudBrush);
                foreach (double x in new[] { 10.0, 16.0, 22.0 })
                {
                    dc.DrawEllipse(SnowBrush, null, new Point(x, 26.5 + (x == 16.0 ? 2.5 : 0)), 1.8, 1.8);
                }

                break;
            case WeatherIconKind.Thunder:
            {
                DrawCloud(dc, 0, -3, DarkCloudBrush);
                var bolt = new StreamGeometry();
                using (StreamGeometryContext ctx = bolt.Open())
                {
                    ctx.BeginFigure(new Point(17.5, 20), true, true);
                    ctx.PolyLineTo([new Point(11.5, 27.5), new Point(15.5, 27.5), new Point(13.5, 32), new Point(21, 24), new Point(17, 24), new Point(19.5, 20)], true, false);
                }

                bolt.Freeze();
                dc.DrawGeometry(SunBrush, null, bolt);
                break;
            }

            default:
                DrawCloud(dc, 0, 0, CloudBrush);
                break;
        }

        dc.Pop();
    }

    private static void DrawSun(DrawingContext dc, Point center, double radius, double rayStart, double rayEnd)
    {
        dc.DrawEllipse(SunBrush, null, center, radius, radius);
        for (int i = 0; i < 8; i++)
        {
            double a = i * System.Math.PI / 4;
            double cos = System.Math.Cos(a), sin = System.Math.Sin(a);
            dc.DrawLine(SunRay, new Point(center.X + cos * rayStart, center.Y + sin * rayStart),
                new Point(center.X + cos * rayEnd, center.Y + sin * rayEnd));
        }
    }

    private static void DrawMoon(DrawingContext dc, Point center, double scale = 1)
    {
        var moon = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new EllipseGeometry(center, 9 * scale, 9 * scale),
            new EllipseGeometry(new Point(center.X + 5 * scale, center.Y - 4 * scale), 8 * scale, 8 * scale));
        dc.DrawGeometry(MoonBrush, null, moon);
    }

    private static void DrawCloud(DrawingContext dc, double dx, double dy, Brush brush)
    {
        dc.PushTransform(new TranslateTransform(dx, dy));
        dc.DrawGeometry(brush, null, CloudShape);
        dc.Pop();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
