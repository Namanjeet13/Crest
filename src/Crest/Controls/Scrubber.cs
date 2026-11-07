using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Crest.Controls;

/// <summary>
/// The playback bar. It reads the song position every frame (so it glides instead of ticking once a second),
/// jumps to wherever you click, follows the pointer while you hold, and seeks when you let go.
/// Looks: Apple (thick, no knob), Knob, Glow, Wave, Line.
/// </summary>
public sealed class Scrubber : FrameworkElement
{
    /// <summary>Live position as a 0..1 fraction, or null when unknown.</summary>
    public Func<double?>? Source { get; set; }
    public event Action<double>? Scrubbing;
    public event Action<double>? SeekRequested;
    public event Action? ScrubEnded;

    public string Look { get => _look; set { _look = value; InvalidateVisual(); } }
    public bool Playing { get; set; }
    public Color Accent { get => _accent; set { _accent = value; _accentBrush = null; InvalidateVisual(); } }

    private string _look = "Apple";
    private Color _accent = Color.FromRgb(0x0A, 0x84, 0xFF);
    private Brush? _accentBrush;
    private double _shown, _grow, _phase, _amp;
    private bool _drag, _hooked;
    private double _dragFrac;
    private double _hold = -1;
    private DateTime _holdUntil;
    private TimeSpan _last;

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 255, 255, 255)));
    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 255, 255, 255)));
    private static readonly Brush KnobBrush = Brushes.White;

    public Scrubber()
    {
        Height = 22;
        Cursor = Cursors.Hand;
        Focusable = false;
        IsVisibleChanged += (_, _) => Hook(IsVisible);
        Unloaded += (_, _) => Hook(false);
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
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _last).TotalSeconds, 0, 0.1);
        if (now == _last) return;
        _last = now;

        double target;
        if (_drag) target = _dragFrac;
        else
        {
            double live = Source?.Invoke() ?? 0;
            if (_hold >= 0)
            {
                // After a seek the app takes a moment to report the new position: keep the bar where you dropped it.
                if (Math.Abs(live - _hold) < 0.01 || DateTime.Now > _holdUntil) _hold = -1;
                else live = _hold;
            }
            target = live;
        }

        double rate = _drag ? 40 : Math.Abs(target - _shown) > 0.03 ? 11 : 22;
        _shown += (target - _shown) * (1 - Math.Exp(-dt * rate));
        _grow += ((IsMouseOver || _drag ? 1 : 0) - _grow) * (1 - Math.Exp(-dt * 14));
        _amp += ((Playing && !_drag ? 1 : 0) - _amp) * (1 - Math.Exp(-dt * 6));
        _phase += dt * 2 * Math.PI * 0.9;

        // Only repaint when something on screen actually moved: a paused, idle bar costs nothing.
        double w = Math.Max(1, ActualWidth);
        bool waving = _look == "Wave" && _amp > 0.01;
        if (waving || Math.Abs(_shown - _drawnShown) * w >= 0.2 || Math.Abs(_grow - _drawnGrow) > 0.003)
            InvalidateVisual();
    }

    private double _drawnShown = -1, _drawnGrow = -1;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = true;
        CaptureMouse();
        Move(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag) Move(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_drag) return;
        Move(e.GetPosition(this).X);
        Finish(true);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_drag) Finish(true);
    }

    private void Finish(bool seek)
    {
        _drag = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (seek)
        {
            _hold = _dragFrac;
            _holdUntil = DateTime.Now.AddSeconds(3);
            SeekRequested?.Invoke(_dragFrac);
        }
        ScrubEnded?.Invoke();
    }

    private void Move(double x)
    {
        double w = Math.Max(1, ActualWidth);
        _dragFrac = Math.Clamp(x / w, 0, 1);
        Scrubbing?.Invoke(_dragFrac);
    }

    public bool IsScrubbing => _drag;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, cy = h / 2;
        if (w <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // whole strip is grabbable
        double f = Math.Clamp(_shown, 0, 1), g = _grow, x = w * f;
        _drawnShown = _shown;
        _drawnGrow = _grow;

        switch (_look)
        {
            case "Knob":
            {
                double t = 4 + 1.5 * g;
                Bar(dc, 0, w, cy, t, Track);
                Bar(dc, 0, x, cy, t, Fill);
                double r = 5 + 2.5 * g;
                dc.DrawEllipse(Freeze(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0))), null, new Point(x, cy + 1), r + 1, r + 1);
                dc.DrawEllipse(KnobBrush, null, new Point(x, cy), r, r);
                break;
            }
            case "Glow":
            {
                double t = 4 + 2 * g;
                Bar(dc, 0, w, cy, t, Track);
                var a = _accentBrush ??= Freeze(new LinearGradientBrush(Color.FromArgb(0xFF, (byte)(_accent.R / 2 + 64), (byte)(_accent.G / 2 + 64), (byte)(_accent.B / 2 + 64)), _accent, 0));
                Bar(dc, -3, x + 3, cy, t + 8, Freeze(new SolidColorBrush(Color.FromArgb((byte)(40 + 30 * g), _accent.R, _accent.G, _accent.B))));
                Bar(dc, 0, x, cy, t, a);
                double r = 3.5 + 3 * g;
                dc.DrawEllipse(Freeze(new SolidColorBrush(Color.FromArgb(90, _accent.R, _accent.G, _accent.B))), null, new Point(x, cy), r + 4, r + 4);
                dc.DrawEllipse(KnobBrush, null, new Point(x, cy), r, r);
                break;
            }
            case "Wave":
            {
                double t = 3 + g;
                Bar(dc, x + 5, w, cy, t, Track);
                if (x > 2)
                {
                    var geo = new StreamGeometry();
                    using (var c = geo.Open())
                    {
                        double amp = 2.6 * _amp;
                        c.BeginFigure(new Point(0, cy), false, false);
                        for (double px = 2; px <= x; px += 2)
                            c.LineTo(new Point(px, cy + amp * Math.Sin(px / 5.5 - _phase) * Math.Min(1, (x - px) / 8 + 0.15)), true, true);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, new Pen(Fill, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
                }
                double kh = 12 + 4 * g;
                dc.DrawRoundedRectangle(KnobBrush, null, new Rect(x - 2, cy - kh / 2, 4, kh), 2, 2);
                break;
            }
            case "Line":
            {
                double t = 2 + 2.5 * g;
                Bar(dc, 0, w, cy, t, Track);
                Bar(dc, 0, x, cy, t, Fill);
                if (g > 0.02) dc.DrawEllipse(KnobBrush, null, new Point(x, cy), 5 * g, 5 * g);
                break;
            }
            default: // Apple: a soft capsule that swells under the pointer, no knob
            {
                double t = 5 + 5 * g;
                double grow = 4 * g;
                Bar(dc, -grow, w + grow, cy, t, Track);
                Bar(dc, -grow, -grow + (w + 2 * grow) * f, cy, t, Fill, clipRight: w + grow);
                break;
            }
        }
    }

    private static void Bar(DrawingContext dc, double x0, double x1, double cy, double t, Brush b, double clipRight = double.NaN)
    {
        if (x1 - x0 < 0.5) return;
        if (!double.IsNaN(clipRight))
        {
            // The fill stays inside the track's rounded ends.
            var track = new RectangleGeometry(new Rect(x0, cy - t / 2, clipRight - x0, t), t / 2, t / 2);
            dc.PushClip(track);
            dc.DrawRectangle(b, null, new Rect(x0, cy - t / 2, x1 - x0, t));
            dc.Pop();
            return;
        }
        dc.DrawRoundedRectangle(b, null, new Rect(x0, cy - t / 2, x1 - x0, t), t / 2, t / 2);
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
