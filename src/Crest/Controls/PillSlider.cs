using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Crest.Controls;

/// <summary>
/// Volume and brightness slider in the iOS manner.
/// Tall: a Control Center capsule that fills from the bottom, with its icon inside (tap the icon to mute).
/// Mini: a slim horizontal bar with the icon on the left (tap to mute).
/// Click anywhere to jump there, hold to drag, scroll the wheel over it to nudge finely.
/// </summary>
public sealed class PillSlider : FrameworkElement
{
    public bool Tall { get; set; } = true;
    public string Glyph { get => _glyph; set { _glyph = value; InvalidateVisual(); } }
    public bool Muted { get => _muted; set { _muted = value; InvalidateVisual(); } }
    /// <summary>Has an icon you can tap (mute). Brightness has none.</summary>
    public bool IconClickable { get; set; } = true;
    /// <summary>Percent per wheel notch.</summary>
    public double WheelStep { get; set; } = 2;
    public bool WheelEnabled { get; set; } = true;

    public event Action<double>? ValueChanged;
    public event Action? IconClicked;

    /// <summary>0..1. Setting it from code animates the fill without raising ValueChanged.</summary>
    public double Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (_drag) return; // the finger wins while held
            _value = value;
            Kick();
        }
    }

    private string _glyph = "";
    private bool _muted;
    private double _value, _shown = -1, _grow, _press;
    private bool _drag, _moved, _onIcon, _hooked;
    private Point _downAt;
    private TimeSpan _last;
    private double _wheelRemainder;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public PillSlider()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        Unloaded += (_, _) => Hook(false);
    }

    private void Kick()
    {
        if (_shown < 0) { _shown = _value; InvalidateVisual(); return; }
        Hook(true);
    }

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        _last = TimeSpan.Zero;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _last) return;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _last).TotalSeconds, 0, 0.1);
        _last = now;

        double rate = _drag ? 34 : 16;
        _shown += (_value - _shown) * (1 - Math.Exp(-dt * rate));
        _grow += ((IsMouseOver || _drag ? 1 : 0) - _grow) * (1 - Math.Exp(-dt * 14));
        _press += ((_drag ? 1 : 0) - _press) * (1 - Math.Exp(-dt * 18));
        InvalidateVisual();

        bool settled = Math.Abs(_value - _shown) < 0.0008 &&
                       Math.Abs((IsMouseOver || _drag ? 1 : 0) - _grow) < 0.004 &&
                       Math.Abs((_drag ? 1 : 0) - _press) < 0.004;
        if (settled)
        {
            _shown = _value;
            Hook(false);
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e) => Hook(true);
    protected override void OnMouseLeave(MouseEventArgs e) => Hook(true);

    // ───── pointer ─────

    private Rect IconRect()
    {
        double w = ActualWidth, h = ActualHeight;
        return Tall ? new Rect(0, h - 40, w, 40) : new Rect(0, 0, 26, h);
    }

    private double TrackFrom(Point p)
    {
        double w = ActualWidth, h = ActualHeight;
        if (Tall) return 1 - Math.Clamp(p.Y / Math.Max(1, h), 0, 1);
        double x0 = 30, x1 = w - 4;
        return Math.Clamp((p.X - x0) / Math.Max(1, x1 - x0), 0, 1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        _downAt = p;
        _moved = false;
        _onIcon = IconClickable && IconRect().Contains(p);
        _drag = true;
        CaptureMouse();
        if (!_onIcon) SetFromUser(TrackFrom(p));
        Hook(true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_drag) return;
        var p = e.GetPosition(this);
        if (!_moved && (p - _downAt).Length > 3) _moved = true;
        if (_moved || !_onIcon) SetFromUser(TrackFrom(p));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_drag) return;
        bool tap = _onIcon && !_moved;
        End();
        if (tap) IconClicked?.Invoke();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_drag) End();
    }

    private void End()
    {
        _drag = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Hook(true);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!WheelEnabled) return;
        // 120 is one notch; touchpads send smaller deltas, which nudge proportionally.
        double notches = e.Delta / 120.0 + _wheelRemainder;
        double step = WheelStep / 100.0 * notches;
        _wheelRemainder = 0;
        SetFromUser(_value + step);
        e.Handled = true;
    }

    public void Nudge(double notches) => SetFromUser(_value + WheelStep / 100.0 * notches);

    private void SetFromUser(double v)
    {
        v = Math.Clamp(v, 0, 1);
        if (Math.Abs(v - _value) < 1e-6) return;
        _value = v;
        Hook(true);
        ValueChanged?.Invoke(v);
    }

    // ───── drawing ─────

    protected override void OnRender(DrawingContext dc)
    {
        if (_shown < 0) _shown = _value;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (Tall) RenderTall(dc, w, h);
        else RenderMini(dc, w, h);
    }

    private void RenderTall(DrawingContext dc, double w, double h)
    {
        // A gentle press-in, like Control Center: the capsule swells a little while held.
        double s = 1 + 0.045 * _press;
        dc.PushTransform(new ScaleTransform(s, s, w / 2, h / 2));
        double r = Math.Min(18, w / 2);
        var shape = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        dc.PushClip(shape);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(0x2E + 0x10 * _grow), 255, 255, 255)), null, new Rect(0, 0, w, h));
        double f = Math.Clamp(_muted ? 0 : _shown, 0, 1);
        double fillTop = h * (1 - f);
        dc.DrawRectangle(Brushes.White, null, new Rect(0, fillTop, w, h - fillTop));
        dc.Pop();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), 1), shape);

        // The icon goes dark once the white fill reaches it.
        double iconCy = h - 20;
        bool covered = fillTop < iconCy;
        var brush = covered ? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)) : Brushes.White;
        DrawGlyph(dc, _glyph, 17, brush, new Point(w / 2, iconCy));
        dc.Pop();
    }

    private void RenderMini(DrawingContext dc, double w, double h)
    {
        double cy = h / 2;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var iconBrush = new SolidColorBrush(Color.FromArgb((byte)(_muted ? 0x99 : 0xE6), 255, 255, 255));
        DrawGlyph(dc, _glyph, 13, iconBrush, new Point(12, cy));

        double x0 = 30, x1 = w - 4;
        double t = 4 + 3 * _grow;
        double f = Math.Clamp(_muted ? 0 : _shown, 0, 1);
        var track = new RectangleGeometry(new Rect(x0, cy - t / 2, x1 - x0, t), t / 2, t / 2);
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)), null, track);
        dc.PushClip(track);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xF0, 255, 255, 255)), null, new Rect(x0, cy - t / 2, (x1 - x0) * f, t));
        dc.Pop();
    }

    private void DrawGlyph(DrawingContext dc, string glyph, double size, Brush brush, Point centre)
    {
        var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(centre.X - ft.Width / 2, centre.Y - ft.Height / 2));
    }
}
