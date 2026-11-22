using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Crest.Services;

namespace Crest.Controls;

/// <summary>
/// The small readouts in the closed island's left and right corners: clock, date, battery, CPU,
/// memory, network speed, a countdown, day progress, uptime and a custom text. Built once per
/// settings change, refreshed once a second, and only the readings that are switched on are sampled.
/// </summary>
internal sealed class IdleWidgets
{
    private sealed record Item(FrameworkElement View, Func<DateTime, bool> Refresh);

    private readonly Panel _left, _right;
    private readonly List<Item> _items = new();
    private readonly SystemStatsService _stats = new();
    private readonly Dictionary<string, SolidColorBrush> _brushes = new();
    private SystemSnapshot _snap;
    private bool _needStats, _needNet;
    private long _rx, _tx, _netAt;
    private double _down, _up, _gap;
    private static readonly DateTime Started = DateTime.Now;
    private static readonly Brush Green = Freeze("#30D158"), Orange = Freeze("#FF9F0A"), Red = Freeze("#FF453A");

    public IdleWidgets(Panel left, Panel right) { _left = left; _right = right; }

    public bool Any => _items.Count > 0;
    public double LeftWidth { get; private set; }
    public double RightWidth { get; private set; }

    public void Rebuild(Settings s)
    {
        _left.Children.Clear();
        _right.Children.Clear();
        _items.Clear();
        _needStats = _needNet = false;
        _gap = Math.Clamp(s.WidgetGap, 0, 40);
        double alpha = Math.Clamp(s.WidgetOpacity, 20, 100) / 100.0;

        Add(s.WClock, Clock);
        Add(s.WDate, Date);
        Add(s.WBattery, Battery);
        Add(s.WCpu, c => Load(c, false));
        Add(s.WRam, c => Load(c, true));
        Add(s.WNet, Net);
        Add(s.WTransfer, Transfer);
        Add(s.WCountdown, Countdown);
        Add(s.WProgress, Progress);
        Add(s.WUptime, Uptime);
        Add(s.WText, Text);

        void Add<T>(T c, Func<T, Item> make) where T : IdleItem
        {
            if (c.Side is not ("Left" or "Right")) return;
            var item = make(c);
            item.View.Opacity = alpha;
            item.View.VerticalAlignment = VerticalAlignment.Center;
            (c.Side == "Left" ? _left : _right).Children.Add(item.View);
            _items.Add(item);
            if (c is BatteryItem or LoadItem) _needStats = true;
            if (c is NetItem or TransferItem) _needNet = true;
        }
    }

    /// <summary>Updates every reading. Returns true if the space they take changed.</summary>
    public bool Refresh(DateTime now)
    {
        if (_items.Count == 0)
        {
            bool had = LeftWidth > 0 || RightWidth > 0;
            LeftWidth = RightWidth = 0;
            return had;
        }
        if (_needStats) _snap = _stats.Sample();
        if (_needNet) SampleNet();
        foreach (var it in _items)
        {
            bool show;
            try { show = it.Refresh(now); } catch { show = false; }
            var v = show ? Visibility.Visible : Visibility.Collapsed;
            if (it.View.Visibility != v) it.View.Visibility = v;
        }
        double lw = Space(_left), rw = Space(_right);
        bool changed = Math.Abs(lw - LeftWidth) > 0.5 || Math.Abs(rw - RightWidth) > 0.5;
        LeftWidth = lw; RightWidth = rw;
        return changed;
    }

    private double Space(Panel p)
    {
        bool first = true;
        foreach (FrameworkElement el in p.Children)
        {
            if (el.Visibility != Visibility.Visible) continue;
            var m = new Thickness(first ? 0 : _gap, 0, 0, 0);
            if (el.Margin != m) el.Margin = m;
            first = false;
        }
        if (first) return 0;
        p.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Whole steps of 4 px, so a reading that grows by one digit doesn't nudge the island every second.
        return Math.Ceiling(p.DesiredSize.Width / 4) * 4;
    }

    // ───────────────────────────── the ten widgets ─────────────────────────────

    private Item Clock(ClockItem c)
    {
        var t = Label(c);
        return new(t, now =>
        {
            t.Inlines.Clear();
            t.Inlines.Add(new Run(now.ToString(c.H24 ? "HH" : "%h", CultureInfo.InvariantCulture)));
            var colon = new Run(":");
            if (c.Blink && now.Second % 2 == 1) colon.Foreground = Brushes.Transparent;
            t.Inlines.Add(colon);
            t.Inlines.Add(new Run(now.ToString(c.Seconds ? "mm:ss" : "mm", CultureInfo.InvariantCulture)));
            if (!c.H24 && c.AmPm)
                t.Inlines.Add(new Run(" " + now.ToString("tt", CultureInfo.InvariantCulture)) { FontSize = c.Size * 0.78 });
            return true;
        });
    }

    private Item Date(DateItem c)
    {
        var t = Label(c);
        return new(t, now =>
        {
            string f = c.Format switch { "22 Nov" => "d MMM", "Nov 22" => "MMM d", "Sunday" => "dddd", "22/11" => "dd'/'MM", _ => "ddd d" };
            var s = now.ToString(f, CultureInfo.CurrentCulture);
            t.Text = c.Upper ? s.ToUpper(CultureInfo.CurrentCulture) : s;
            return true;
        });
    }

    private Item Battery(BatteryItem c)
    {
        double k = c.Size / 12;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var fill = new Border { CornerRadius = new CornerRadius(1.2 * k), HorizontalAlignment = HorizontalAlignment.Left };
        var shell = new Border
        {
            Width = 20 * k, Height = 10 * k, CornerRadius = new CornerRadius(3 * k), BorderThickness = new Thickness(1),
            Padding = new Thickness(1.2 * k), Child = fill, VerticalAlignment = VerticalAlignment.Center
        };
        var nub = new Border { Width = 1.6 * k, Height = 4 * k, CornerRadius = new CornerRadius(0, 1, 1, 0), Margin = new Thickness(0.6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var bolt = new TextBlock { Text = "", FontFamily = Icons, FontSize = c.Size * 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        var t = Label(c);
        t.Margin = new Thickness(5, 0, 0, 0);
        row.Children.Add(shell);
        row.Children.Add(nub);
        row.Children.Add(bolt);
        row.Children.Add(t);
        bool icon = c.Style != "%", text = c.Style != "Icon";
        shell.Visibility = nub.Visibility = icon ? Visibility.Visible : Visibility.Collapsed;
        if (!icon) t.Margin = new Thickness(0);
        return new(row, _ =>
        {
            if (_snap.Battery is not double pct) return false;     // a desktop: nothing to show
            if (c.Below < 100 && pct > c.Below) return false;
            var tone = !c.LevelColors ? Brush(c.Color) : _snap.Charging ? Green : pct <= 20 ? Red : Brush(c.Color);
            fill.Background = tone;
            shell.BorderBrush = nub.Background = Brush(c.Color, 0.55);
            fill.Width = Math.Max(1, (shell.Width - 2 - 2.4 * k) * pct / 100);
            bolt.Foreground = tone;
            bolt.Visibility = c.Bolt && _snap.Charging ? Visibility.Visible : Visibility.Collapsed;
            t.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
            t.Text = $"{pct:0}%";
            t.Foreground = c.LevelColors && pct <= 20 && !_snap.Charging ? Red : Brush(c.Color);
            return true;
        });
    }

    private Item Load(LoadItem c, bool ram)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var t = Label(c);
        var (track, fill) = Bar(c);
        track.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(t);
        row.Children.Add(track);
        return new(row, _ =>
        {
            double v = ram ? _snap.Ram : _snap.Cpu;
            if (v < c.Above) return false;
            var tone = c.LoadColors ? (v > 85 ? Red : v > 60 ? Orange : Brush(c.Color)) : Brush(c.Color);
            bool bar = c.Style != "Text", text = c.Style != "Bar" || c.Label;
            t.Inlines.Clear();
            if (c.Label) t.Inlines.Add(new Run(ram ? "RAM" : "CPU") { Foreground = Brush(c.Color, 0.6), FontSize = c.Size * 0.82 });
            if (c.Style != "Bar")
            {
                if (c.Label) t.Inlines.Add(new Run(" "));
                t.Inlines.Add(new Run(ram && c.Unit == "GB" ? $"{_snap.RamUsedGb:0.0} GB" : $"{v:0}%") { Foreground = tone });
            }
            t.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
            track.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
            track.Margin = new Thickness(text ? 6 : 0, 0, 0, 0);
            fill.Background = tone;
            fill.Width = track.Width * Math.Clamp(v / 100, 0.04, 1);
            return true;
        });
    }

    private Item Net(NetItem c)
    {
        var t = Label(c);
        return new(t, _ =>
        {
            if (_netAt == 0) return false;
            if (c.HideIdle && _down < 20_000 && _up < 20_000) return false;
            t.Inlines.Clear();
            void Part(string arrow, double rate)
            {
                if (t.Inlines.Count > 0) t.Inlines.Add(new Run("  "));
                if (c.Arrows) t.Inlines.Add(new Run(arrow + " ") { Foreground = Brush(c.Color, 0.6) });
                t.Inlines.Add(new Run(Rate(rate, c.Bits)));
            }
            if (c.Show != "Up") Part("↓", _down);
            if (c.Show != "Down") Part("↑", _up);
            return true;
        });
    }

    private static readonly Brush DownTint = Freeze("#0A84FF"), UpTint = Freeze("#30D158");

    /// <summary>A ring per direction with an arrow inside. It fills with speed on a log scale: 10 KB/s empty, 1 GB/s full.</summary>
    private Item Transfer(TransferItem c)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var parts = new List<(FrameworkElement View, Ring Ring, TextBlock Arrow, TextBlock Speed, bool Down)>();
        foreach (bool down in new[] { true, false })
        {
            if (c.Show == (down ? "Upload" : "Download")) continue;
            double d = Math.Round(Math.Clamp(c.Size, 8, 18) * 1.4);
            var ring = new Ring { Width = d, Height = d, Thickness = Math.Max(1.6, d * 0.13), VerticalAlignment = VerticalAlignment.Center };
            var arrow = new TextBlock
            {
                Text = down ? "\uE74B" : "\uE74A", FontFamily = Icons, FontSize = d * 0.48, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Grid { VerticalAlignment = VerticalAlignment.Center };
            cell.Children.Add(ring);
            cell.Children.Add(arrow);
            var speed = Label(c);
            speed.Margin = new Thickness(5, 0, 0, 0);
            speed.Visibility = c.Speed ? Visibility.Visible : Visibility.Collapsed;
            var pair = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            pair.Children.Add(cell);
            pair.Children.Add(speed);
            row.Children.Add(pair);
            parts.Add((pair, ring, arrow, speed, down));
        }
        var quietSince = new Dictionary<bool, long>();
        return new(row, _ =>
        {
            if (_netAt == 0) return false;
            long now = Environment.TickCount64;
            var web = c.Browser ? BrowserDownloads.Now : default;
            bool first = true;
            foreach (var (view, ring, arrow, speed, down) in parts)
            {
                double rate = down ? _down : _up;
                // The browser's own numbers: a real percentage while it downloads, a tick for a moment when it finishes.
                bool live = down && web.Count > 0, finished = down && !live && web.DoneAt > 0 && now - web.DoneAt < 2500;
                // Stays a few seconds after it goes quiet, so a stuttering download doesn't flicker in and out.
                if (rate >= 50_000) quietSince[down] = 0;
                else if (!quietSince.TryGetValue(down, out var q) || q == 0) quietSince[down] = now;
                bool show = !c.HideIdle || live || finished || rate >= 50_000 || now - quietSince[down] < 4000;
                view.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show) continue;
                view.Margin = new Thickness(first ? 0 : 8, 0, 0, 0);
                first = false;
                ring.Paint(c.Tint ? (down ? DownTint : UpTint) : Brush(c.Color), Brush(c.Color, 0.2));
                arrow.Foreground = Brush(c.Color);
                arrow.Text = finished ? "\uE73E" : down ? "\uE74B" : "\uE74A";
                double f = web.Fraction;
                speed.Visibility = c.Speed || finished || (live && f >= 0) ? Visibility.Visible : Visibility.Collapsed;
                if (finished) { ring.To(1); speed.Text = "Done"; }
                else if (live && f >= 0) { ring.To(Math.Max(0.02, f)); speed.Text = $"{Math.Floor(f * 100):0}%"; }
                else
                {
                    ring.To(rate < 10_000 ? 0 : Math.Clamp(Math.Log10(rate / 10_000) / 5, 0.04, 1));
                    speed.Text = Rate(rate, false);
                }
            }
            return !first;
        });
    }

    /// <summary>A thin circle that fills clockwise from the top, easing to each new value.</summary>
    private sealed class Ring : FrameworkElement
    {
        public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
            nameof(Fraction), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
        public double Thickness { get; set; } = 2;
        private Brush _stroke = Brushes.White, _track = Brushes.Gray;

        /// <summary>Sets the colours, redrawing only if they changed.</summary>
        public void Paint(Brush stroke, Brush track)
        {
            if (ReferenceEquals(stroke, _stroke) && ReferenceEquals(track, _track)) return;
            _stroke = stroke; _track = track;
            InvalidateVisual();
        }

        public void To(double f)
        {
            if (Math.Abs(f - Fraction) < 0.002) return;
            BeginAnimation(FractionProperty, new System.Windows.Media.Animation.DoubleAnimation(f, TimeSpan.FromMilliseconds(700))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
        }

        protected override void OnRender(DrawingContext dc)
        {
            double r = (Math.Min(ActualWidth, ActualHeight) - Thickness) / 2;
            if (r <= 0) return;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            dc.DrawEllipse(null, new Pen(_track, Thickness), c, r, r);
            double f = Math.Clamp(Fraction, 0, 1);
            if (f <= 0.001) return;
            var pen = new Pen(_stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (f >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
            double a = f * 2 * Math.PI;
            var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
                ctx.ArcTo(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }

    private Item Countdown(CountdownItem c)
    {
        var t = Label(c);
        return new(t, now =>
        {
            t.Inlines.Clear();
            if (c.Label.Length > 0) t.Inlines.Add(new Run(c.Label + " ") { Foreground = Brush(c.Color, 0.6) });
            if (!TryDate(c.Target, out var when)) { t.Inlines.Add(new Run("set a date")); return true; }
            var left = when - now;
            string sign = "";
            if (left <= TimeSpan.Zero)
            {
                if (c.After == "Hide") return false;
                if (c.After == "Done") { t.Inlines.Add(new Run("now")); return true; }
                left = now - when; sign = "+";
            }
            string s = c.Format switch
            {
                "Days" => $"{Math.Ceiling(left.TotalDays):0} {(Math.Ceiling(left.TotalDays) == 1 ? "day" : "days")}",
                "Exact" => left.Days > 0 ? $"{left.Days}d {left:hh\\:mm\\:ss}" : $"{left:hh\\:mm\\:ss}",
                _ => left.TotalDays >= 1 ? $"{left.Days}d {left.Hours}h"
                   : left.TotalHours >= 1 ? $"{left.Hours}h {left.Minutes}m"
                   : $"{left.Minutes}:{left.Seconds:00}",
            };
            t.Inlines.Add(new Run(sign + s));
            return true;
        });
    }

    private Item Progress(ProgressItem c)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var t = Label(c);
        var (track, fill) = Bar(c);
        row.Children.Add(t);
        row.Children.Add(track);
        return new(row, now =>
        {
            DateTime a, b;
            switch (c.Of)
            {
                case "Work day":
                    int ws = Math.Clamp(c.WorkStart, 0, 23), we = Math.Clamp(c.WorkEnd, ws + 1, 24);
                    a = now.Date.AddHours(ws); b = now.Date.AddHours(we); break;
                case "Week":
                    a = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)); b = a.AddDays(7); break;
                case "Month":
                    a = new DateTime(now.Year, now.Month, 1); b = a.AddMonths(1); break;
                case "Year":
                    a = new DateTime(now.Year, 1, 1); b = a.AddYears(1); break;
                default:
                    a = now.Date; b = a.AddDays(1); break;
            }
            double p = Math.Clamp((now - a) / (b - a), 0, 1);
            string name = c.Of switch { "Work day" => "Work", "Month" => now.ToString("MMM", CultureInfo.CurrentCulture), "Year" => now.Year.ToString(CultureInfo.InvariantCulture), _ => c.Of };
            bool bar = c.Style != "%", pct = c.Style != "Bar";
            t.Inlines.Clear();
            if (c.Label) t.Inlines.Add(new Run(name) { Foreground = Brush(c.Color, 0.6), FontSize = c.Size * 0.82 });
            if (pct) t.Inlines.Add(new Run((c.Label ? " " : "") + $"{p * 100:0}%"));
            t.Visibility = t.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            track.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
            track.Margin = new Thickness(t.Visibility == Visibility.Visible ? 6 : 0, 0, 0, 0);
            fill.Width = Math.Max(2, track.Width * p);
            fill.Background = Brush(c.Color);
            return true;
        });
    }

    private Item Uptime(UptimeItem c)
    {
        var t = Label(c);
        return new(t, now =>
        {
            var up = c.Since == "Crest start" ? now - Started : TimeSpan.FromMilliseconds(Environment.TickCount64);
            string s = c.Format == "01:23:45"
                ? $"{(int)up.TotalHours:00}:{up.Minutes:00}:{up.Seconds:00}"
                : up.TotalDays >= 1 ? $"{up.Days}d {up.Hours}h" : up.TotalHours >= 1 ? $"{up.Hours}h {up.Minutes}m" : $"{up.Minutes}m";
            t.Inlines.Clear();
            if (c.Label) t.Inlines.Add(new Run("Up ") { Foreground = Brush(c.Color, 0.6), FontSize = c.Size * 0.82 });
            t.Inlines.Add(new Run(s));
            return true;
        });
    }

    private Item Text(TextItem c)
    {
        var t = Label(c);
        t.Text = c.Text;
        t.FontWeight = c.Bold ? FontWeights.Bold : FontWeights.Medium;
        t.FontStyle = c.Italic ? FontStyles.Italic : FontStyles.Normal;
        t.MaxWidth = Math.Clamp(c.MaxWidth, 30, 300);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        return new(t, _ => c.Text.Length > 0);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private TextBlock Label(IdleItem c)
    {
        var t = new TextBlock
        {
            FontSize = Math.Clamp(c.Size, 8, 18), FontWeight = FontWeights.SemiBold, Foreground = Brush(c.Color),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Application.Current.TryFindResource("DisplayFont") is FontFamily f) t.FontFamily = f;
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    private (Border track, Border fill) Bar(IdleItem c)
    {
        double k = c.Size / 12;
        var fill = new Border { CornerRadius = new CornerRadius(2 * k), HorizontalAlignment = HorizontalAlignment.Left, Background = Brush(c.Color) };
        var track = new Border
        {
            Width = 30 * k, Height = 4 * k, CornerRadius = new CornerRadius(2 * k), Background = Brush(c.Color, 0.22),
            Child = fill, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true,
        };
        return (track, fill);
    }

    private SolidColorBrush Brush(string hex, double alpha = 1)
    {
        string key = hex + "|" + alpha.ToString("0.00", CultureInfo.InvariantCulture);
        if (_brushes.TryGetValue(key, out var b)) return b;
        Color col;
        try { col = (Color)ColorConverter.ConvertFromString(hex); } catch { col = Colors.White; }
        col.A = (byte)Math.Round(col.A * alpha);
        b = new SolidColorBrush(col);
        b.Freeze();
        return _brushes[key] = b;
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static string Rate(double bytes, bool bits)
    {
        double v = bits ? bytes * 8 : bytes;
        string u = bits ? "b/s" : "B/s";
        return v >= 1e9 ? $"{v / 1e9:0.0} G{u}" : v >= 1e6 ? $"{v / 1e6:0.0} M{u}" : v >= 1e3 ? $"{v / 1e3:0} K{u}" : $"{v:0} {u}";
    }

    public static bool TryDate(string s, out DateTime when) =>
        DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out when) ||
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out when);

    /// <summary>Bytes in and out over every real, connected adapter (virtual switches would count twice).</summary>
    private void SampleNet()
    {
        long rx = 0, tx = 0;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (n.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) ||
                n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var st = n.GetIPStatistics();
                rx += st.BytesReceived; tx += st.BytesSent;
            }
            catch { }
        }
        long t = Environment.TickCount64;
        if (_netAt != 0 && t > _netAt)
        {
            double dt = (t - _netAt) / 1000.0;
            _down = Math.Max(0, (rx - _rx) / dt);
            _up = Math.Max(0, (tx - _tx) / dt);
        }
        _rx = rx; _tx = tx; _netAt = t;
    }
}
