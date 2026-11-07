using System;
using System.Windows;
using System.Windows.Media;

namespace Crest.Controls;

/// <summary>
/// A mirrored, liquid-looking spectrum: bass swells from the centre, treble ripples
/// out to the tips. Two layers (a soft echo behind, a vivid gradient in front) with a glow.
/// The glow is two soft strokes drawn under the shape, not a blur effect: an Effect would
/// need its own offscreen surface and a blur pass every frame.
/// </summary>
public sealed class Waveform : FrameworkElement
{
    private float[] _levels = new float[12];
    private Brush _front = Brushes.White, _back = Brushes.White;
    private Pen? _haloWide, _haloNear;
    private bool _glowOn = true;
    private double _time;

    public Waveform()
    {
        IsHitTestVisible = false;
        SetAccent(Color.FromRgb(0x0A, 0x84, 0xFF));
    }

    public void SetLevels(float[] levels, double time)
    {
        _levels = levels;
        _time = time;
        InvalidateVisual();
    }

    public void SetGlow(bool on)
    {
        if (_glowOn == on) return;
        _glowOn = on;
        InvalidateVisual();
    }

    public void SetAccent(Color accent)
    {
        var (h, s, v) = ToHsv(accent);
        s = Math.Clamp(Math.Max(s, 0.5), 0, 0.9);
        v = Math.Max(v, 0.92);
        var mid = FromHsv(h, s * 0.75, 1);
        var left = FromHsv(h - 28, s, v);
        var right = FromHsv(h + 38, s, v);

        var front = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        front.GradientStops.Add(new GradientStop(left, 0));
        front.GradientStops.Add(new GradientStop(mid, 0.5));
        front.GradientStops.Add(new GradientStop(right, 1));
        front.Freeze();
        _front = front;

        var back = new SolidColorBrush(Color.FromArgb(90, right.R, right.G, right.B));
        back.Freeze();
        _back = back;

        _haloWide = Halo(mid, 34, 9);
        _haloNear = Halo(mid, 70, 4.5);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 2) return;
        int bands = _levels.Length;
        int n = bands * 2 - 1;
        double mid = h / 2;

        // Echo layer: one band out of step and a little taller, for depth.
        dc.DrawGeometry(_back, null, Build(w, h, mid, n, bands, shift: 1, gain: 1.12, wobble: 0.6));
        var front = Build(w, h, mid, n, bands, shift: 0, gain: 1.0, wobble: 0);
        if (_glowOn)
        {
            dc.DrawGeometry(null, _haloWide, front);
            dc.DrawGeometry(null, _haloNear, front);
        }
        dc.DrawGeometry(_front, null, front);
    }

    private static Pen Halo(Color c, byte alpha, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)), thickness)
        { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private Geometry Build(double w, double h, double mid, int n, int bands, int shift, double gain, double wobble)
    {
        var top = new Point[n];
        for (int j = 0; j < n; j++)
        {
            double t = (double)j / (n - 1);
            int band = Math.Min(bands - 1, Math.Abs(j - (bands - 1)) + shift);
            double v = _levels[band];
            if (wobble > 0) v *= 0.85 + 0.15 * Math.Sin(_time * 7 + j * 0.9);
            double taper = Math.Pow(Math.Sin(Math.PI * t), 0.7);
            double amp = (0.07 + 0.93 * Math.Clamp(v * gain, 0, 1)) * (mid - 0.5) * taper;
            top[j] = new Point(t * w, mid - Math.Max(0.6, amp));
        }

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, mid), true, true);
            Curve(ctx, top, mirror: false, mid);
            Curve(ctx, top, mirror: true, mid);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Catmull-Rom through the points, as cubic Béziers. Mirrored = bottom half, right to left.</summary>
    private static void Curve(StreamGeometryContext ctx, Point[] pts, bool mirror, double mid)
    {
        int n = pts.Length;
        Point P(int i)
        {
            i = Math.Clamp(i, 0, n - 1);
            var p = mirror ? pts[n - 1 - i] : pts[i];
            return mirror ? new Point(p.X, 2 * mid - p.Y) : p;
        }
        for (int i = 0; i < n - 1; i++)
        {
            Point p0 = P(i - 1), p1 = P(i), p2 = P(i + 1), p3 = P(i + 2);
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            ctx.BezierTo(c1, c2, p2, true, true);
        }
    }

    private static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
