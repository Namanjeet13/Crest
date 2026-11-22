using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Crest.Services;

namespace Crest;

/// <summary>
/// The full settings app: a searchable sidebar of sections and compact grouped lists, like System Settings.
/// Every control writes straight to <see cref="Settings"/> and commits, so the island updates live.
/// </summary>
public sealed class SettingsWindow : Window
{
    private Settings _s;
    /// <summary>A fresh, untouched copy: what "Reset" puts back.</summary>
    private static readonly Settings Defaults = new();
    /// <summary>One reset per control on the page being built, run by the page's "Reset page".</summary>
    private List<Action>? _resets;

    private sealed record PageInfo(string Name, string About, string Color, string Glyph, Action Build, Border Row, Border Marker);

    private readonly StackPanel _side = new() { Margin = new Thickness(8, 0, 8, 8) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(28, 20, 28, 28) };
    private readonly List<PageInfo> _pages = new();
    private readonly TextBox _search = new();
    private readonly Grid _root = new();
    private StackPanel _body = new();
    private int _page, _lastPage;

    // Search state: the query, whether the page being built matched by name, and its heading still to be written.
    private string? _query, _shownQuery;
    private string[] _terms = Array.Empty<string>();
    private bool _pageHit;
    private Action? _pendingHeading;
    private int? _jump;

    private static readonly Brush Bg = Hex("#1C1C1E"), SideBg = Hex("#161618"), Card = Hex("#262628"), CardEdge = Hex("#14FFFFFF"),
        Sep = Hex("#363638"), Field = Hex("#1A1A1C"), Fg = Brushes.White, Dim = Hex("#98989F"), Faint = Hex("#636366"),
        Blue = Hex("#0A84FF"), Red = Hex("#FF453A"), Hover = Hex("#14FFFFFF"), Selected = Hex("#26FFFFFF");

    public SettingsWindow(Settings settings)
    {
        _s = settings;
        Title = "Crest Settings";
        Width = 840; Height = 620; MinWidth = 660; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Foreground = Fg;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(214) });
        _root.ColumnDefinitions.Add(new ColumnDefinition());
        var sideHost = new Border { Background = SideBg, BorderBrush = Hex("#262628"), BorderThickness = new Thickness(0, 0, 1, 0) };
        var sideStack = new DockPanel();

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 16, 0, 12) };
        brand.Children.Add(new Border { Width = 24, Height = 11, CornerRadius = new CornerRadius(5.5), Background = Brushes.Black, BorderBrush = Hex("#48484A"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(new TextBlock { Text = "Crest", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(9, 0, 0, 0) });
        DockPanel.SetDock(brand, Dock.Top);
        sideStack.Children.Add(brand);
        var search = SearchBox();
        DockPanel.SetDock(search, Dock.Top);
        sideStack.Children.Add(search);

        var reset = Link("Reset all settings", Faint, () =>
            Confirm("Reset all settings?",
                "Everything goes back to how Crest shipped. Start with Windows stays as it is.",
                "Reset all", () => { _s.ResetToDefaults(); Show(_page); }), Red);
        reset.FontSize = 12;
        reset.Margin = new Thickness(18, 6, 0, 14);
        DockPanel.SetDock(reset, Dock.Bottom);
        sideStack.Children.Add(reset);
        sideStack.Children.Add(new ScrollViewer { Content = _side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        sideHost.Child = sideStack;
        _root.Children.Add(sideHost);
        Grid.SetColumn(_scroll, 1);
        _root.Children.Add(_scroll);
        Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), Application.Current.Resources["SlimScrollBar"]);
        Content = _root;

        Page("General", "How Crest starts, opens and steps aside.", "#8E8E93", "", General);
        Page("Appearance", "The island's shape, size and glass.", "#0A84FF", "", Appearance);
        Page("Animation", "How fast and how bouncy the island moves.", "#FFB340", "", Animation);
        Section("Closed island");
        Page("Widgets", "Small readouts on the closed island while nothing is playing.", "#30B0C7", "", ClosedIsland);
        Page("Pet", "A tiny pixel friend that lives on the idle island.", "#FF6482", "", Pet);
        Section("Music");
        Page("Now Playing", "What the island shows while a song is on.", "#FF375F", "", NowPlaying);
        Page("Music Apps", "Which apps and websites can show music on the island.", "#30D158", "", Sources);
        Page("Artwork & Colour", "The album art backdrop, accent colour and visualizer.", "#BF5AF2", "", Artwork);
        Section("Open island");
        Page("Tabs & Top Bar", "The tabs, the top bar and scrolling on the open island.", "#5E5CE6", "", OpenIsland);
        Page("Alerts", "Pop-ups for changes, the clipboard and Discord.", "#FF453A", "", Alerts);
        Page("Shortcuts", "Keyboard shortcuts that work anywhere in Windows.", "#AC8E68", "", Shortcuts);
        Section(null);
        Page("About", "Version, updates and where your settings live.", "#636366", "", About);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(h, 33, ref round, sizeof(int));
        };
        Show(0);
    }

    /// <summary>Reads a control's value from the shipped defaults instead of the live settings.</summary>
    private T Default<T>(Func<T> get)
    {
        var live = _s;
        _s = Defaults;
        try { return get(); }
        finally { _s = live; }
    }

    private void Resettable<T>(Func<T> get, Action<T> set) => _resets?.Add(() => set(Default(get)));

    /// <summary>Rows that only show while <paramref name="on"/>. They are built either way, so "Reset page" still covers them.</summary>
    private static UIElement[] If(bool on, params UIElement[] rows) => on ? rows : Array.Empty<UIElement>();

    // ----------------------------- pages -----------------------------

    private void General()
    {
        Group("Opening",
            Choice("Open the island by", new[] { "Hovering", "Clicking" }, () => _s.OpenOnClickOnly ? "Clicking" : "Hovering", v => _s.OpenOnClickOnly = v == "Clicking"),
            Row("Delays", Inline(Labeled("Open", NumberCtl(0, 2000, () => _s.HoverDelayMs, v => _s.HoverDelayMs = (int)v)),
                                 Labeled("Close", NumberCtl(0, 5000, () => _s.CloseDelayMs, v => _s.CloseDelayMs = (int)v)), Unit("ms")),
                "How long the pointer rests before it opens or closes."),
            Row("Stay open after a shortcut", Inline(NumberCtl(1, 60, () => _s.PinSeconds, v => _s.PinSeconds = (int)v), Unit("s"))),
            Toggle("Hide until hovered", "Shrinks to a thin line while nothing is happening.", () => _s.HideUntilHover, v => _s.HideUntilHover = v));
        Group("System",
            Toggle("Start with Windows", null, () => _s.StartWithWindows, v => { _s.StartWithWindows = v; StartupService.Apply(v); }, resettable: false),
            Choice("In fullscreen", new[] { "Show", "Hide", "Hide, hold Esc to show" },
                () => !_s.HideInFullscreen ? "Show" : _s.HoldEscInFullscreen ? "Hide, hold Esc to show" : "Hide",
                v => { _s.HideInFullscreen = v != "Show"; _s.HoldEscInFullscreen = v.Contains("Esc"); },
                "Games, videos and presentations."));
    }

    private void Appearance()
    {
        Group("Shape",
            Row("Style", Inline(ChoiceCtl(new[] { "Notch", "Island" }, () => _s.Style, v => _s.Style = v),
                If(_s.Style == "Island", Labeled("Gap", NumberCtl(0, 40, () => _s.IslandOffset, v => _s.IslandOffset = (int)v)), Unit("px")))),
            Row("Curves", Inline(MiniSlider("Shoulders", 0, 16, 1, "", () => _s.ShoulderSize, v => _s.ShoulderSize = (int)v),
                                 MiniSlider("Open corners", 12, 50, 1, "", () => _s.ExpandedCorner, v => _s.ExpandedCorner = (int)v))),
            Row("Size", Inline(Labeled("Closed", NumberCtl(100, 320, () => _s.IdleWidth, v => _s.IdleWidth = (int)v)),
                               Unit("×"), NumberCtl(20, 44, () => _s.IdleHeight, v => _s.IdleHeight = (int)v),
                               Labeled("Playing", NumberCtl(240, 460, () => _s.MusicWidth, v => _s.MusicWidth = (int)v)), Unit("px")),
                "Width and height when closed, and the width while music plays."));
        Group("Glass and light",
            Toggle("Liquid glass", "Frosted glass that bends the screen at its edge.", () => _s.LiquidGlass, v => _s.LiquidGlass = v, reveal: true),
            If(_s.LiquidGlass,
                Row("Glass", Inline(MiniSlider("Darkness", 0, 90, 1, "%", () => _s.GlassDim, v => _s.GlassDim = (int)v),
                                    MiniSlider("Frost", 0, 100, 1, "%", () => _s.GlassFrost, v => _s.GlassFrost = (int)v)))),
            Row("Edges", Chips(false, C("Rim light", () => _s.RimLight, v => _s.RimLight = v), C("Shadow", () => _s.Shadow, v => _s.Shadow = v))));
    }

    private void Animation()
    {
        Group(null,
            Slider("Speed", 0.5, 2, 0.05, "x", () => _s.MotionSpeed, v => _s.MotionSpeed = v),
            Slider("Bounce", 0, 0.4, 0.01, "", () => _s.Bounce, v => _s.Bounce = v, "0 is the smooth iOS spring; higher overshoots playfully."),
            Row("Duration", Inline(Labeled("Open", NumberCtl(60, 3000, () => _s.OpenAnimMs, v => _s.OpenAnimMs = (int)v)),
                                   Labeled("Close", NumberCtl(60, 3000, () => _s.CloseAnimMs, v => _s.CloseAnimMs = (int)v)), Unit("ms"))),
            Toggle("Reduce motion", "Simple fades instead of zooms, blurs and lens effects.", () => _s.ReduceMotion, v => _s.ReduceMotion = v));
    }

    // -- Closed island widgets --

    private static readonly string[] Sides = { "Off", "Left", "Right" };
    private List<UIElement>? _offWidgets;

    private void ClosedIsland()
    {
        Group("Layout",
            Toggle("Widen the island to fit", "Off keeps the normal width, so widgets may crowd the pet.", () => _s.WidgetsGrow, v => _s.WidgetsGrow = v),
            Row("Look", Inline(MiniSlider("Spacing", 0, 30, 1, "", () => _s.WidgetGap, v => _s.WidgetGap = (int)v),
                               MiniSlider("Brightness", 20, 100, 1, "%", () => _s.WidgetOpacity, v => _s.WidgetOpacity = (int)v))),
            Note("Widgets on one side sit in the order of this page."));

        _offWidgets = new List<UIElement>();
        Widget("Clock", () => _s.WClock,
            Row("Show", Chips(false,
                C("24-hour", () => _s.WClock.H24, v => _s.WClock.H24 = v, reveal: true),
                C("Seconds", () => _s.WClock.Seconds, v => _s.WClock.Seconds = v),
                _s.WClock.H24 ? null : C("AM / PM", () => _s.WClock.AmPm, v => _s.WClock.AmPm = v),
                C("Blinking colon", () => _s.WClock.Blink, v => _s.WClock.Blink = v))));
        Widget("Date", () => _s.WDate,
            Row("Format", Inline(ChoiceCtl(new[] { "Sun 22", "22 Nov", "Nov 22", "Sunday", "22/11" }, () => _s.WDate.Format, v => _s.WDate.Format = v),
                                 Chips(false, C("Capitals", () => _s.WDate.Upper, v => _s.WDate.Upper = v)))));
        Widget("Battery", () => _s.WBattery,
            Choice("Show", new[] { "Icon + %", "%", "Icon" }, () => _s.WBattery.Style, v => _s.WBattery.Style = v),
            Row("Extras", Chips(false, C("Colour by charge", () => _s.WBattery.LevelColors, v => _s.WBattery.LevelColors = v),
                                       C("Charging bolt", () => _s.WBattery.Bolt, v => _s.WBattery.Bolt = v))),
            Slider("Only at or below", 5, 100, 5, "%", () => _s.WBattery.Below, v => _s.WBattery.Below = (int)v, "At 100% it always shows."),
            Note("Hidden on PCs without a battery."));
        Widget("CPU", () => _s.WCpu, LoadRows(() => _s.WCpu, false));
        Widget("Memory", () => _s.WRam, LoadRows(() => _s.WRam, true));
        Widget("Network speed", () => _s.WNet,
            Choice("Show", new[] { "Down", "Up", "Both" }, () => _s.WNet.Show, v => _s.WNet.Show = v),
            Row("Extras", Chips(false, C("Arrows", () => _s.WNet.Arrows, v => _s.WNet.Arrows = v),
                                       C("Bits (Mb/s)", () => _s.WNet.Bits, v => _s.WNet.Bits = v),
                                       C("Hide when quiet", () => _s.WNet.HideIdle, v => _s.WNet.HideIdle = v))));
        Widget("Download circle", () => _s.WTransfer,
            Choice("Show", new[] { "Download", "Upload", "Both" }, () => _s.WTransfer.Show, v => _s.WTransfer.Show = v),
            Row("Extras", Chips(false,
                C("Speed", () => _s.WTransfer.Speed, v => _s.WTransfer.Speed = v),
                C("Blue and green", () => _s.WTransfer.Tint, v => _s.WTransfer.Tint = v),
                C("Only while transferring", () => _s.WTransfer.HideIdle, v => _s.WTransfer.HideIdle = v)), null, "upload transfer ring network"),
            Note("The ring fills with speed: a fifth at 100 KB/s, half near 3 MB/s, full at 1 GB/s. Blue is down, green is up."));
        Widget("Countdown", () => _s.WCountdown,
            Row("Counting down to", Inline(TextCtl(90, () => _s.WCountdown.Label, v => _s.WCountdown.Label = v),
                                           TextCtl(130, () => _s.WCountdown.Target, v => _s.WCountdown.Target = v, v => Crest.Controls.IdleWidgets.TryDate(v, out _))),
                "A name, then the date and time, like 2026-12-25 18:30."),
            Choice("Format", new[] { "Auto", "Days", "Exact" }, () => _s.WCountdown.Format, v => _s.WCountdown.Format = v),
            Choice("When it's reached", new[] { "Done", "Count up", "Hide" }, () => _s.WCountdown.After, v => _s.WCountdown.After = v));
        Widget("Progress", () => _s.WProgress,
            Choice("Through the", new[] { "Day", "Work day", "Week", "Month", "Year" }, () => _s.WProgress.Of, v => _s.WProgress.Of = v),
            Row("Show", Inline(ChoiceCtl(new[] { "%", "Bar", "Both" }, () => _s.WProgress.Style, v => _s.WProgress.Style = v),
                               Chips(false, C("Label", () => _s.WProgress.Label, v => _s.WProgress.Label = v)))),
            If(_s.WProgress.Of == "Work day",
                Row("Work day", Inline(NumberCtl(0, 23, () => _s.WProgress.WorkStart, v => _s.WProgress.WorkStart = (int)v), Unit("to"),
                                       NumberCtl(1, 24, () => _s.WProgress.WorkEnd, v => _s.WProgress.WorkEnd = (int)v), Unit("h")))));
        Widget("Uptime", () => _s.WUptime,
            Choice("Since", new[] { "PC start", "Crest start" }, () => _s.WUptime.Since, v => _s.WUptime.Since = v),
            Row("Format", Inline(ChoiceCtl(new[] { "1h 23m", "01:23:45" }, () => _s.WUptime.Format, v => _s.WUptime.Format = v),
                                 Chips(false, C("“Up” label", () => _s.WUptime.Label, v => _s.WUptime.Label = v)))));
        Widget("Custom text", () => _s.WText,
            Row("Text", TextCtl(180, () => _s.WText.Text, v => _s.WText.Text = v), "Words, a name or an emoji."),
            Row("Style", Inline(Chips(false, C("Bold", () => _s.WText.Bold, v => _s.WText.Bold = v), C("Italic", () => _s.WText.Italic, v => _s.WText.Italic = v)),
                                Labeled("Max width", NumberCtl(30, 300, () => _s.WText.MaxWidth, v => _s.WText.MaxWidth = (int)v)), Unit("px"))));
        var off = _offWidgets;
        _offWidgets = null;
        if (off.Count > 0) Group("More widgets", off, Note("Pick Left or Right to add one to the closed island."));
    }

    private UIElement[] LoadRows(Func<LoadItem> item, bool ram) => new[]
    {
        Row("Show", Inline(ChoiceCtl(new[] { "Text", "Bar", "Both" }, () => item().Style, v => item().Style = v),
                           ram ? ChoiceCtl(new[] { "%", "GB" }, () => item().Unit, v => item().Unit = v) : null)),
        Row("Extras", Chips(false, C("Label", () => item().Label, v => item().Label = v),
                                   C("Colour by load", () => item().LoadColors, v => item().LoadColors = v))),
        Slider("Only at or above", 0, 95, 5, "%", () => item().Above, v => item().Above = (int)v, "0 always shows it."),
    };

    /// <summary>One widget's card: where it sits, its own options, then colour and size. Widgets that are off share one card.</summary>
    private void Widget(string name, Func<IdleItem> item, params object[] options)
    {
        if (item().Side == "Off")
        {
            // Built and thrown away, so "Reset page" still restores the options of widgets that are off.
            Row("", Inline(ColorCtl(() => item().Color, v => item().Color = v), NumberCtl(9, 16, () => item().Size, v => item().Size = v, true)));
            _offWidgets?.Add(Row(name, ChoiceCtl(Sides, () => item().Side, v => item().Side = v), null, "widget"));
            return;
        }
        Group(name,
            Choice("Position", Sides, () => item().Side, v => item().Side = v),
            options.Where(o => o is not UIElement e || !IsNote(e)),
            Row("Colour and size", Inline(ColorCtl(() => item().Color, v => item().Color = v),
                                          NumberCtl(9, 16, () => item().Size, v => item().Size = v, true), Unit("pt"))),
            options.Where(o => o is UIElement e && IsNote(e)));
    }

    private void Pet()
    {
        Group(null,
            Toggle("Pixel pet", "Uses about 10 MB and pauses while hidden.", () => _s.PixelPet, v => _s.PixelPet = v, reveal: true),
            If(_s.PixelPet,
                Choice("Animal", Crest.Controls.PixelPet.Kinds, () => _s.PetKind, v =>
                {
                    if (v == _s.PetKind) return;
                    _s.PetKind = v;
                    _s.PetLook = Crest.Controls.PixelPet.Looks(v)[0];
                }),
                Choice("Look", Crest.Controls.PixelPet.Looks(_s.PetKind), () => _s.PetLook, v => _s.PetLook = v),
                Row("Placement", Inline(ChoiceCtl(new[] { "Left", "Right" }, () => _s.PetCorner, v => _s.PetCorner = v),
                                        ChoiceCtl(new[] { "Large", "Small" }, () => _s.PetSize, v => _s.PetSize = v))),
                Note("It hides for music and calls and naps after a minute alone. Hover it for hearts.")));
    }

    private void NowPlaying()
    {
        Group("What it shows",
            Row("Closed island", Chips(false,
                C("Album art", () => _s.ShowArtInCompact, v => _s.ShowArtInCompact = v),
                C("Artist", () => _s.ShowArtistInCompact, v => _s.ShowArtistInCompact = v),
                C("Artwork glow", () => _s.ArtInClosedIsland, v => _s.ArtInClosedIsland = v))),
            Row("Open island", Chips(false,
                C("Times", () => _s.ShowTimes, v => _s.ShowTimes = v),
                C("Volume slider", () => _s.MiniVolume, v => _s.MiniVolume = v))),
            Choice("Progress bar", new[] { "Apple", "Knob", "Glow", "Wave", "Line" }, () => _s.ScrubStyle, v => _s.ScrubStyle = v),
            Toggle("Click the artwork to open the player", null, () => _s.ArtOpensSource, v => _s.ArtOpensSource = v));
        Group("Songs",
            Row("Pop up new songs", Inline(If(_s.TrackPeek, Labeled("For", NumberCtl(0.5, 8, () => _s.TrackPeekSeconds, v => _s.TrackPeekSeconds = v, true)), Unit("s")),
                                           SwitchCtl(() => _s.TrackPeek, v => _s.TrackPeek = v, true)),
                "The island briefly opens with the new title."),
            Row("Keep a paused song", Inline(NumberCtl(0, 1440, () => _s.PausedLingerMinutes, v => _s.PausedLingerMinutes = v, true), Unit("min")),
                "0 removes it the moment it pauses."));
    }

    private void Sources()
    {
        _resets?.Add(() => _s.MediaSources = new List<string>(Defaults.MediaSources));
        _resets?.Add(() => _s.AnyMediaSource = Defaults.AnyMediaSource);
        Group(null,
            Toggle("Any app", "Anything Windows reports as playing.", () => _s.AnyMediaSource, v => _s.AnyMediaSource = v, resettable: false, reveal: true));
        if (_s.AnyMediaSource) return;

        var rows = new List<UIElement>();
        foreach (var src in _s.MediaSources.ToList())
        {
            bool site = src.Contains('.');
            var g = new DockPanel { Margin = new Thickness(14, 0, 10, 0), Height = 46, Tag = src + (site ? " website" : " app") };
            var remove = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Remove", VerticalAlignment = VerticalAlignment.Center,
                Child = Glyph("", 11, Dim) };
            remove.MouseEnter += (_, _) => remove.Background = Hex("#26FF453A");
            remove.MouseLeave += (_, _) => remove.Background = Brushes.Transparent;
            remove.MouseLeftButtonUp += (_, _) => { _s.MediaSources.RemoveAll(x => x == src); Commit(); Show(_page); };
            DockPanel.SetDock(remove, Dock.Right);
            g.Children.Add(remove);
            var badge = Tile(site ? "#0A84FF" : "#30D158", site ? "" : "", 24);
            badge.Margin = new Thickness(0, 0, 12, 0);
            g.Children.Add(badge);
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(new TextBlock { Text = src });
            t.Children.Add(new TextBlock { Text = site ? "Website, in any browser" : "App", Foreground = Dim, FontSize = 11.5 });
            g.Children.Add(t);
            rows.Add(g);
        }
        if (rows.Count == 0) rows.Add(Note("Nothing can show music on the island yet. Add an app or a website below."));

        var box = new TextBox
        {
            Background = Brushes.Transparent, Foreground = Fg, CaretBrush = Fg, BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13, Padding = new Thickness(0)
        };
        var hint = new TextBlock { Text = "Add an app or website: spotify, vlc, music.youtube.com…", Foreground = Faint, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        void Add()
        {
            var v = box.Text.Trim().ToLowerInvariant().Replace("https://", "").Replace("http://", "").TrimEnd('/');
            if (v.EndsWith(".exe")) v = v[..^4];
            if (v.Length == 0 || _s.MediaSources.Contains(v)) return;
            _s.MediaSources.Add(v);
            Commit();
            Show(_page);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };
        var add = Pill("Add", Add);
        var row = new DockPanel { Margin = new Thickness(14, 0, 10, 0), Height = 46, Tag = "add app website" };
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        var plus = Glyph("", 12, Faint);
        plus.Margin = new Thickness(6, 0, 18, 0);
        DockPanel.SetDock(plus, Dock.Left);
        row.Children.Add(plus);
        var field = new Grid { Margin = new Thickness(0, 0, 10, 0) };
        field.Children.Add(hint);
        field.Children.Add(box);
        row.Children.Add(field);
        Group("Allowed", rows, row,
            Note("For an app, type its name. For a website, type its address. Websites work in any browser."));
    }

    private void Artwork()
    {
        Group("Backdrop",
            Toggle("Album art backdrop", "A soft glow of the cover behind the open island.", () => _s.ArtBackdrop, v => _s.ArtBackdrop = v, reveal: true),
            If(_s.ArtBackdrop,
                Row("Look", Inline(MiniSlider("Blur", 0, 100, 1, "", () => _s.BackdropBlur, v => _s.BackdropBlur = (int)v),
                                   MiniSlider("Darken", 0, 90, 1, "%", () => _s.BackdropDim, v => _s.BackdropDim = (int)v),
                                   Chips(false, C("Drift", () => _s.BackdropMotion, v => _s.BackdropMotion = v))))));
        Group("Accent colour",
            Row("Accent colour", Inline(ColorCtl(() => _s.AccentHex, v => _s.AccentHex = v),
                                        Chips(false, C("Match artwork", () => _s.ArtAccent, v => _s.ArtAccent = v))),
                "Buttons and bars. With Match artwork on, this is used only when a song has no cover."));
        Group("Visualizer",
            Row("Visualizer", Inline(If(_s.Visualizer, ChoiceCtl(new[] { "Art", "White", "Accent" }, () => _s.VizColor, v => _s.VizColor = v)),
                                     SwitchCtl(() => _s.Visualizer, v => _s.Visualizer = v, true)),
                "Bars that move with the music."),
            If(_s.Visualizer,
                Row("Sensitivity", Inline(SliderCtl(0.3, 2.5, 0.05, "x", () => _s.VizSensitivity, v => _s.VizSensitivity = v, 150),
                                          Chips(false, C("Glow", () => _s.VizGlow, v => _s.VizGlow = v))))));
    }

    private void OpenIsland()
    {
        Group("Tabs",
            Row("Show", Chips(true,
                C("Clipboard", () => _s.ShowClipTab, v => _s.ShowClipTab = v),
                C("Controls", () => _s.ShowControlsTab, v => _s.ShowControlsTab = v),
                C("System", () => _s.ShowSystemTab, v => _s.ShowSystemTab = v)), "Now playing and Settings are always there."),
            Row("Order", TabOrderCtl(), "Use the arrows to move a tab.", "tab order move"),
            Choice("Open on", new[] { "Last", "Home", "Clipboard", "Controls", "System" }, () => _s.DefaultTab, v => _s.DefaultTab = v),
            Toggle("Jump to the music", "Opens on the song while one is playing.", () => _s.OpenOnActivity, v => _s.OpenOnActivity = v),
            Toggle("Liquid lens", "The tab highlight magnifies as it moves.", () => _s.LiquidLens, v => _s.LiquidLens = v));
        Group("Top bar",
            Row("Show", Chips(false,
                C("Clock", () => _s.ShowClock, v => _s.ShowClock = v, reveal: true),
                _s.ShowClock ? C("24-hour", () => _s.Clock24h, v => _s.Clock24h = v) : null,
                C("Battery", () => _s.ShowBattery, v => _s.ShowBattery = v),
                C("Mic button", () => _s.ShowMicButton, v => _s.ShowMicButton = v))));
        Group("Scrolling",
            Row("Scroll to switch tabs", Inline(If(_s.ScrollSwitchesTabs, MiniSlider("Speed", 1, 10, 1, "", () => _s.TabScrollSpeed, v => _s.TabScrollSpeed = v)),
                                                SwitchCtl(() => _s.ScrollSwitchesTabs, v => _s.ScrollSwitchesTabs = v, true))),
            Row("Scroll volume and brightness", Inline(If(_s.ScrollAdjustsSliders, MiniSlider("Step", 1, 40, 1, "%", () => _s.SliderScrollStep, v => _s.SliderScrollStep = v)),
                                                       SwitchCtl(() => _s.ScrollAdjustsSliders, v => _s.ScrollAdjustsSliders = v, true))));
    }

    /// <summary>The tabs left to right as on the island, each with arrows to move it. Hidden tabs are dimmed.</summary>
    private UIElement TabOrderCtl()
    {
        Resettable(() => _s.TabOrder, v => _s.TabOrder = v);
        var order = _s.TabOrderList();
        var wrap = new WrapPanel { MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < order.Length; i++)
        {
            int at = i;
            string name = order[i];
            bool shown = name switch { "Clipboard" => _s.ShowClipTab, "Controls" => _s.ShowControlsTab, "System" => _s.ShowSystemTab, _ => true };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Arrow("", i > 0, () => MoveTab(order, at, -1)));
            sp.Children.Add(new TextBlock { Text = name == "Home" ? "Music" : name, FontSize = 12, Margin = new Thickness(3, 0, 3, 1), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(Arrow("", i < order.Length - 1, () => MoveTab(order, at, 1)));
            wrap.Children.Add(new Border
            {
                Background = Field, BorderBrush = Hex("#3A3A3C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(2, 2, 2, 2), Margin = new Thickness(5, 2, 0, 2), Child = sp, Opacity = shown ? 1 : 0.45,
                ToolTip = shown ? null : "Hidden",
            });
        }
        return wrap;
    }

    private static UIElement Arrow(string glyph, bool enabled, Action click)
    {
        var b = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Child = Glyph(glyph, 9, enabled ? Fg : Faint), Cursor = enabled ? Cursors.Hand : Cursors.Arrow };
        if (enabled)
        {
            b.MouseEnter += (_, _) => b.Background = Hex("#38383A");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, _) => click();
        }
        return b;
    }

    private void MoveTab(string[] order, int i, int dir)
    {
        int j = i + dir;
        if (j < 0 || j >= order.Length) return;
        var next = (string[])order.Clone();
        (next[i], next[j]) = (next[j], next[i]);
        _s.TabOrder = string.Join(",", next);
        Commit();
        Show(_page);
    }

    private void Alerts()
    {
        Group("Pop-ups",
            Row("Show a pop-up for", Chips(true,
                C("Volume", () => _s.VolumePeek, v => _s.VolumePeek = v),
                C("Microphone", () => _s.MicPeek, v => _s.MicPeek = v),
                C("Discord mute", () => _s.DiscordPeek, v => _s.DiscordPeek = v),
                C("Copies", () => _s.ClipboardPeek, v => _s.ClipboardPeek = v))),
            If(_s.ClipboardPeek, Row("Show copies for", Inline(NumberCtl(0.5, 5, () => _s.ClipPeekSeconds, v => _s.ClipPeekSeconds = v, true), Unit("s")))),
            Note("New-song pop-ups are on the Now Playing page."));
        Group("Clipboard and Discord",
            Row("Clipboard history", Inline(NumberCtl(5, 500, () => _s.ClipHistory, v => _s.ClipHistory = (int)v), Unit("items"))),
            Toggle("Discord call controls", "Mute, deafen and leave calls from the island.", () => _s.DiscordControls, v => _s.DiscordControls = v));
    }

    private void Shortcuts()
    {
        Group(null,
            Shortcut("N", "Open the island", () => _s.HotkeyOpen, v => _s.HotkeyOpen = v),
            Shortcut("M", "Mute the microphone", () => _s.HotkeyMic, v => _s.HotkeyMic = v),
            Shortcut("C", "Pick a colour from the screen", () => _s.HotkeyColor, v => _s.HotkeyColor = v),
            Shortcut("V", "Clipboard history", () => _s.HotkeyClip, v => _s.HotkeyClip = v));
    }

    private UIElement Shortcut(string key, string label, Func<bool> get, Action<bool> set)
    {
        var row = (DockPanel)Toggle(label, null, get, set);
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var k in new[] { "Ctrl", "Alt", key })
            keys.Children.Add(new Border
            {
                Background = Field, BorderBrush = Hex("#3A3A3C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 4, 0), MinWidth = 22,
                Child = new TextBlock { Text = k, FontSize = 11, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center }
            });
        DockPanel.SetDock(keys, Dock.Left);
        row.Children.Insert(1, keys);
        row.Tag = $"{label} ctrl alt {key} shortcut hotkey";
        return row;
    }

    private void About()
    {
        Group(null,
            Row("Version", new TextBlock { Text = UpdateService.Current.ToString(3), Foreground = Dim }),
            Row("Settings folder", Link("Open", Blue, () =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", App.DataDir); } catch { }
            })));
        Group("Updates", UpdateRow(),
            Toggle("Check automatically", "Every six hours. Nothing installs without you.",
                () => _s.AutoUpdateCheck, v => _s.AutoUpdateCheck = v));
        Group(null, Note("Crest is free and open source at github.com/Namanjeet13/Crest."));
    }

    private static bool _updating;

    private UIElement UpdateRow()
    {
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var sub = new TextBlock { Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var slot = new Border { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        var d = new DockPanel { Margin = new Thickness(14, 0, 12, 0), MinHeight = 54, Tag = "update check install version" };
        DockPanel.SetDock(slot, Dock.Right);
        d.Children.Add(slot);
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 7, 0, 7) };
        t.Children.Add(status);
        t.Children.Add(sub);
        d.Children.Add(t);

        void Say(string title, string detail, UIElement? button = null)
        {
            status.Text = title;
            sub.Text = detail;
            sub.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            slot.Child = button;
        }

        void Idle()
        {
            var r = UpdateService.Available;
            if (r != null)
                Say($"Crest {r.Version.ToString(3)} is available", $"{r.Size / 1048576.0:0} MB. Crest closes, updates and reopens by itself.", Pill("Install", () => Install(r)));
            else if (UpdateService.LastChecked == default)
                Say("Check for updates", "", Pill("Check now", Check));
            else
                Say("Crest is up to date", $"Checked at {UpdateService.LastChecked:t}", Pill("Check now", Check));
        }

        async void Check()
        {
            Say("Checking…", "");
            try { await UpdateService.CheckAsync(); Idle(); }
            catch (Exception) { Say("Couldn't reach GitHub", "Check your internet connection and try again.", Pill("Try again", Check)); }
        }

        async void Install(UpdateService.Release r)
        {
            if (_updating) return;
            _updating = true;
            Say($"Downloading Crest {r.Version.ToString(3)}…", "0%");
            var progress = new Progress<double>(p => { if (status.Text.StartsWith("Downloading")) sub.Text = $"{p * 100:0}%"; });
            try
            {
                await UpdateService.InstallAsync(r, progress);
                Say("Installing…", "Crest will reopen in a moment.");
            }
            catch (Exception ex)
            {
                _updating = false;
                App.Log(ex);
                Say("The update didn't install",
                    ex is InvalidOperationException ? ex.Message : "The download failed. Check your internet connection and try again.",
                    Pill("Try again", () => Install(r)));
            }
        }

        if (_updating) Say("Installing…", "Crest will reopen in a moment.");
        else Idle();
        return d;
    }

    // ----------------------------- navigation -----------------------------

    private void Section(string? name)
    {
        if (name == null) { _side.Children.Add(new Border { Height = 12 }); return; }
        _side.Children.Add(new TextBlock { Text = name, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Faint, Margin = new Thickness(10, 16, 0, 5) });
    }

    private void Page(string name, string about, string color, string glyph, Action build)
    {
        int index = _pages.Count;
        var marker = new Border { Width = 3, Height = 14, CornerRadius = new CornerRadius(1.5), Background = Blue, Visibility = Visibility.Hidden, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-6, 0, 3, 0) };
        var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 5, 8, 5), Margin = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(marker);
        sp.Children.Add(Tile(color, glyph, 20));
        sp.Children.Add(new TextBlock { Text = name, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Child = sp;
        var info = new PageInfo(name, about, color, glyph, build, row, marker);
        row.MouseLeftButtonUp += (_, _) => Open(index);
        row.MouseEnter += (_, _) => { if (_page != index) row.Background = Hover; };
        row.MouseLeave += (_, _) => { if (_page != index) row.Background = Brushes.Transparent; };
        _side.Children.Add(row);
        _pages.Add(info);
    }

    /// <summary>Goes to a page, leaving search if it is on.</summary>
    private void Open(int index)
    {
        if (_search.Text.Length > 0) { _jump = index; _search.Text = ""; }
        else Show(index);
    }

    private UIElement SearchBox()
    {
        var host = new Border { Background = Hex("#232325"), BorderBrush = Hex("#2E2E30"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Height = 30, Margin = new Thickness(10, 0, 10, 6) };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Glyph("", 12, Faint);
        icon.Margin = new Thickness(9, 0, 7, 0);
        g.Children.Add(icon);
        _search.Background = Brushes.Transparent; _search.Foreground = Fg; _search.CaretBrush = Fg;
        _search.BorderThickness = new Thickness(0); _search.VerticalContentAlignment = VerticalAlignment.Center; _search.Padding = new Thickness(0);
        var hint = new TextBlock { Text = "Search settings", Foreground = Faint, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(hint, 1); Grid.SetColumn(_search, 1);
        g.Children.Add(hint);
        g.Children.Add(_search);
        var clear = new Border { Width = 22, Height = 22, Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(11), Background = Brushes.Transparent, Cursor = Cursors.Hand, Visibility = Visibility.Collapsed, Child = Glyph("", 9, Dim) };
        clear.MouseLeftButtonUp += (_, _) => _search.Text = "";
        Grid.SetColumn(clear, 2);
        g.Children.Add(clear);
        host.Child = g;
        _search.GotKeyboardFocus += (_, _) => host.BorderBrush = Blue;
        _search.LostKeyboardFocus += (_, _) => host.BorderBrush = Hex("#2E2E30");
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _search.Text = ""; e.Handled = true; } };
        _search.TextChanged += (_, _) =>
        {
            bool empty = _search.Text.Length == 0;
            hint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            clear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            var q = _search.Text.Trim();
            _query = q.Length == 0 ? null : q;
            _terms = _query?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            if (_query != null) Show(-1);
            else { Show(_jump ?? _lastPage); _jump = null; }
        };
        return host;
    }

    private bool Hit(string? text) => text != null && _terms.Length > 0 && _terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Shows a page, or the search results when <paramref name="index"/> is -1.</summary>
    private void Show(int index)
    {
        bool search = index < 0;
        bool same = _body.Tag is int old && old == index && (!search || _shownQuery == _query);
        double offset = _scroll.VerticalOffset;
        _page = index;
        if (!search) _lastPage = index;
        for (int i = 0; i < _pages.Count; i++)
        {
            bool on = i == index;
            _pages[i].Row.Background = on ? Selected : Brushes.Transparent;
            _pages[i].Marker.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }

        _body = new StackPanel { MaxWidth = 660, Tag = index };
        var resets = _resets = search ? null : new List<Action>();
        if (search) BuildSearch();
        else _pages[index].Build();
        _resets = null;

        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 20) };
        if (resets is { Count: > 0 })
        {
            var p = _pages[index];
            var link = Link("Reset page", Dim, () =>
                Confirm($"Reset {p.Name}?", $"Only the settings on this page go back to how Crest shipped.", "Reset", () =>
                {
                    foreach (var reset in resets) reset();
                    Commit();
                    Show(_page);
                }, Hex(p.Color)), Blue);
            link.FontSize = 12;
            link.VerticalAlignment = VerticalAlignment.Top;
            link.Margin = new Thickness(12, 8, 2, 0);
            DockPanel.SetDock(link, Dock.Right);
            head.Children.Add(link);
        }
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = search ? "Search" : _pages[index].Name, FontSize = 22, FontWeight = FontWeights.Bold });
        titles.Children.Add(new TextBlock
        {
            Text = search ? $"Settings matching “{_query}”" : _pages[index].About,
            Foreground = Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0)
        });
        head.Children.Add(titles);
        _body.Children.Insert(0, head);

        _scroll.Content = _body;
        if (same) _scroll.ScrollToVerticalOffset(offset);
        else
        {
            _scroll.ScrollToTop();
            if (search) return;
            _body.Opacity = 0;
            var tt = new TranslateTransform(0, 6);
            _body.RenderTransform = tt;
            var e = new CubicEase { EasingMode = EasingMode.EaseOut };
            _body.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = e });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = e });
        }
    }

    /// <summary>Builds every page, keeping only the groups and rows that match the search, under each page's name.</summary>
    private void BuildSearch()
    {
        _shownQuery = _query;
        for (int i = 0; i < _pages.Count; i++)
        {
            var p = _pages[i];
            int at = i;
            _pageHit = Hit(p.Name);
            _pendingHeading = () => _body.Children.Add(ResultHeading(p, at));
            p.Build();
        }
        _pendingHeading = null;
        _pageHit = false;
        if (_body.Children.Count == 0)
            _body.Children.Add(new TextBlock { Text = "No settings match. Try another word, like “clock” or “glass”.", Foreground = Dim, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
    }

    private UIElement ResultHeading(PageInfo p, int index)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 0, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(Tile(p.Color, p.Glyph, 18));
        var name = new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(name);
        sp.Children.Add(new TextBlock { Text = "›", Foreground = Dim, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) });
        sp.MouseEnter += (_, _) => name.Foreground = Blue;
        sp.MouseLeave += (_, _) => name.Foreground = Fg;
        sp.MouseLeftButtonUp += (_, _) => Open(index);
        return sp;
    }

    private void Commit() => _s.Commit();

    // ----------------------------- building blocks -----------------------------

    private static bool IsNote(UIElement e) => e is TextBlock { Tag: "note" };

    /// <summary>Words from the controls built for the next row (choice options, captions, chips), so search finds them.</summary>
    private readonly List<string> _kw = new();

    /// <summary>A titled card of rows. Items may be rows or lists of rows (from <see cref="If"/>). In search, keeps only the matches.</summary>
    private void Group(string? header, params object[] items)
    {
        var rows = new List<UIElement>();
        foreach (var item in items)
        {
            if (item is UIElement e) rows.Add(e);
            else if (item is IEnumerable list) rows.AddRange(list.OfType<UIElement>());
        }
        if (_query != null)
        {
            if (!_pageHit && !Hit(header))
                rows = rows.Where(r => r is FrameworkElement { Tag: string s } && s != "note" && Hit(s)).ToList();
            if (rows.Count == 0) return;
            _pendingHeading?.Invoke();
            _pendingHeading = null;
        }
        if (header != null)
            _body.Children.Add(new TextBlock { Text = header, Foreground = Dim, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 7) });
        var stack = new StackPanel();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0 && !IsNote(rows[i]))
                stack.Children.Add(new Border { Height = 1, Background = Sep, Margin = new Thickness(14, 0, 0, 0) });
            stack.Children.Add(rows[i]);
        }
        _body.Children.Add(new Border { Background = Card, BorderBrush = CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = stack, Margin = new Thickness(0, 0, 0, 20), ClipToBounds = true });
    }

    private UIElement Row(string label, UIElement right, string? sub = null, string? keywords = null)
    {
        var d = new DockPanel { Margin = new Thickness(14, 0, 14, 0), MinHeight = sub == null ? 44 : 54, Tag = $"{label} {sub} {keywords} {string.Join(" ", _kw)}" };
        _kw.Clear();
        if (right is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(16, 0, 0, 0); }
        DockPanel.SetDock(right, Dock.Right);
        d.Children.Add(right);
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
        t.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (sub != null) t.Children.Add(new TextBlock { Text = sub, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        d.Children.Add(t);
        return d;
    }

    private static UIElement Note(string text) =>
        new TextBlock { Text = text, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 14, 12), Tag = "note" };

    /// <summary>Controls side by side on the right of a row. Takes controls, lists of controls (from <see cref="If"/>) and nulls.</summary>
    private static UIElement Inline(params object?[] parts)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var p in parts)
        {
            IEnumerable<UIElement> els = p switch { UIElement e => new[] { e }, IEnumerable l => l.OfType<UIElement>(), _ => Array.Empty<UIElement>() };
            foreach (var e in els)
            {
                if (e is FrameworkElement fe)
                {
                    fe.VerticalAlignment = VerticalAlignment.Center;
                    if (sp.Children.Count > 0) fe.Margin = new Thickness(fe.Tag is "unit" ? 6 : 14, 0, 0, 0);
                }
                sp.Children.Add(e);
            }
        }
        return sp;
    }

    private UIElement Labeled(string caption, UIElement control)
    {
        _kw.Add(caption);
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = caption, Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(control);
        return sp;
    }

    private static UIElement Unit(string text) =>
        new TextBlock { Text = text, Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Tag = "unit" };

    /// <param name="reveal">Re-draws the page after a change, for switches that show or hide other rows.</param>
    private UIElement Toggle(string label, string? sub, Func<bool> get, Action<bool> set, bool resettable = true, bool reveal = false) =>
        Row(label, SwitchCtl(get, set, reveal, resettable), sub);

    private UIElement SwitchCtl(Func<bool> get, Action<bool> set, bool reveal = false, bool resettable = true)
    {
        if (resettable) Resettable(get, set);
        var c = new CheckBox { Style = (Style)Application.Current.Resources["Switch"], IsChecked = get() };
        c.Click += (_, _) =>
        {
            set(c.IsChecked == true);
            Commit();
            if (reveal) Dispatcher.BeginInvoke(() => Show(_page));
        };
        return c;
    }

    private sealed record ChipDef(string Name, Func<bool> Get, Action<bool> Set, bool Reveal);

    private static ChipDef C(string name, Func<bool> get, Action<bool> set, bool reveal = false) => new(name, get, set, reveal);

    /// <summary>A set of on/off pills: several small switches in one row. Blue ones are on.</summary>
    private UIElement Chips(bool reveal, params ChipDef?[] chips)
    {
        var wrap = new WrapPanel { MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var chip in chips)
        {
            if (chip == null) continue;
            Resettable(chip.Get, chip.Set);
            _kw.Add(chip.Name);
            var text = new TextBlock { Text = chip.Name, FontSize = 12 };
            var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 4, 11, 5), Margin = new Thickness(6, 3, 0, 3), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = text };
            void Paint()
            {
                bool on = chip.Get();
                b.Background = on ? Hex("#330A84FF") : Field;
                b.BorderBrush = on ? Blue : Hex("#3A3A3C");
                text.Foreground = on ? Fg : Hex("#C7C7CC");
            }
            Paint();
            b.MouseLeftButtonUp += (_, _) =>
            {
                chip.Set(!chip.Get());
                Commit();
                if (reveal || chip.Reveal) Dispatcher.BeginInvoke(() => Show(_page));
                else Paint();
            };
            wrap.Children.Add(b);
        }
        return wrap;
    }

    private UIElement Choice(string label, string[] options, Func<string> get, Action<string> set, string? sub = null) =>
        Row(label, ChoiceCtl(options, get, set), sub);

    private UIElement ChoiceCtl(string[] options, Func<string> get, Action<string> set)
    {
        Resettable(get, set);
        _kw.AddRange(options);
        var bar = new Border { Background = Field, CornerRadius = new CornerRadius(7), Padding = new Thickness(2) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Child = sp;
        foreach (var o in options)
        {
            var on = string.Equals(get(), o, StringComparison.OrdinalIgnoreCase);
            var b = new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 3, 10, 4), Cursor = Cursors.Hand,
                Background = on ? Hex("#56565A") : Brushes.Transparent,
                Child = new TextBlock { Text = o, FontSize = 12, Foreground = on ? Fg : Hex("#C7C7CC"), FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal }
            };
            if (!on)
            {
                b.MouseEnter += (_, _) => b.Background = Hover;
                b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            }
            b.MouseLeftButtonUp += (_, _) => { set(o); Commit(); Show(_page); };
            sp.Children.Add(b);
        }
        return bar;
    }

    private UIElement Slider(string label, double min, double max, double step, string unit, Func<double> get, Action<double> set, string? sub = null) =>
        Row(label, SliderCtl(min, max, step, unit, get, set, 160), sub);

    /// <summary>A caption, a short slider and its value, to sit beside others in one row.</summary>
    private UIElement MiniSlider(string caption, double min, double max, double step, string unit, Func<double> get, Action<double> set) =>
        Labeled(caption, SliderCtl(min, max, step, unit, get, set, 84));

    private UIElement SliderCtl(double min, double max, double step, string unit, Func<double> get, Action<double> set, double width)
    {
        Resettable(get, set);
        string Fmt(double v) => (step >= 1 ? v.ToString("0") : step >= 0.1 ? v.ToString("0.0") : v.ToString("0.00")) + unit;
        var value = new TextBlock { Foreground = Dim, Text = Fmt(get()), MinWidth = 36, TextAlignment = TextAlignment.Right, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var s = new System.Windows.Controls.Slider
        {
            Minimum = min, Maximum = max, Value = get(), Width = width, SmallChange = step, LargeChange = step * 10,
            IsSnapToTickEnabled = true, TickFrequency = step, Style = (Style)Application.Current.Resources["ScrubSlider"],
            VerticalAlignment = VerticalAlignment.Center
        };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) => { timer.Stop(); Commit(); };
        s.ValueChanged += (_, e) =>
        {
            value.Text = Fmt(e.NewValue);
            set(e.NewValue);
            timer.Stop(); timer.Start();    // live, but not on every pixel of the drag
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(s);
        right.Children.Add(value);
        return right;
    }

    private static TextBox InputBox(string text, double width)
    {
        var box = new TextBox
        {
            Text = text, Width = width, Background = Field, Foreground = Fg, CaretBrush = Fg, BorderBrush = Hex("#3A3A3C"), BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center
        };
        box.Resources.Add(typeof(Border), new Style(typeof(Border)) { Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(6)) } });
        return box;
    }

    private UIElement NumberCtl(double min, double max, Func<double> get, Action<double> set, bool decimals = false, double width = 56)
    {
        Resettable(get, set);
        var box = InputBox(get().ToString(decimals ? "0.##" : "0", CultureInfo.InvariantCulture), width);
        box.TextAlignment = TextAlignment.Right;
        void Apply()
        {
            if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                v = Math.Clamp(decimals ? v : Math.Round(v), min, max);
                set(v);
                Commit();
                box.BorderBrush = Hex("#3A3A3C");
                box.Text = v.ToString(decimals ? "0.##" : "0", CultureInfo.InvariantCulture);
            }
            else box.BorderBrush = Red;
        }
        box.LostKeyboardFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); Keyboard.ClearFocus(); } };
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(ch => char.IsDigit(ch) || (decimals && (ch == '.' || ch == ',')));
        return box;
    }

    private UIElement TextCtl(double width, Func<string> get, Action<string> set, Func<string, bool>? valid = null)
    {
        Resettable(get, set);
        var box = InputBox(get(), width);
        void Apply()
        {
            var v = box.Text.Trim();
            if (valid != null && !valid(v)) { box.BorderBrush = Red; return; }
            box.BorderBrush = Hex("#3A3A3C");
            if (v == get()) return;
            set(v);
            Commit();
        }
        box.LostKeyboardFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); Keyboard.ClearFocus(); } };
        return box;
    }

    private UIElement ColorCtl(Func<string> get, Action<string> set)
    {
        Resettable(get, set);
        _kw.Add("color colour");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hex in new[] { "#0A84FF", "#5E5CE6", "#BF5AF2", "#FF375F", "#FF453A", "#FF9F0A", "#FFD60A", "#30D158", "#64D2FF", "#FFFFFF" })
        {
            bool on = string.Equals(get(), hex, StringComparison.OrdinalIgnoreCase);
            var dot = new Ellipse { Width = 17, Height = 17, Fill = Hex(hex), Margin = new Thickness(2.5, 0, 2.5, 0), Cursor = Cursors.Hand, Stroke = on ? Brushes.White : Hex("#30000000"), StrokeThickness = on ? 2 : 1, VerticalAlignment = VerticalAlignment.Center };
            dot.MouseLeftButtonUp += (_, _) => { set(hex); Commit(); Show(_page); };
            sp.Children.Add(dot);
        }
        return sp;
    }

    private static Border Tile(string color, string glyph, double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.26), Background = Hex(color),
        Child = Glyph(glyph, size * 0.55, Brushes.White)
    };

    private static TextBlock Glyph(string g, double size, Brush brush) => new()
    {
        Text = g, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private Grid? _sheet;

    /// <summary>
    /// A dark sheet over the settings window: dimmed backdrop, rounded card with an icon, a short explanation,
    /// and Cancel / Reset buttons. Esc or a click outside cancels, Enter confirms.
    /// </summary>
    private void Confirm(string heading, string message, string action, Action ok, Brush? tint = null)
    {
        if (_sheet != null) return;
        var sheet = new Grid { Background = Hex("#99000000"), Opacity = 0 };
        Grid.SetColumnSpan(sheet, 2);

        var card = new Border
        {
            Width = 320, Background = Hex("#2C2C2E"), CornerRadius = new CornerRadius(14),
            BorderBrush = Hex("#3FFFFFFF"), BorderThickness = new Thickness(0.6),
            Padding = new Thickness(22, 22, 22, 16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.92, 0.92),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Opacity = 0.55, Color = Colors.Black }
        };
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;

        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(12), Background = tint ?? Red,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12),
            Child = Glyph("", 22, Brushes.White)
        });
        stack.Children.Add(new TextBlock { Text = heading, FontSize = 16, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = message, Foreground = Dim, FontSize = 12.5, LineHeight = 18, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18) });

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var cancel = SheetButton("Cancel", Hex("#48484A"), Hex("#5A5A5E"));
        var confirm = SheetButton(action, Red, Hex("#FF6259"));
        Grid.SetColumn(confirm, 2);
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        stack.Children.Add(buttons);
        card.Child = stack;
        sheet.Children.Add(card);

        void Close(bool yes)
        {
            if (_sheet != sheet) return;
            _sheet = null;
            PreviewKeyDown -= Keys;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            fade.Completed += (_, _) => _root.Children.Remove(sheet);
            sheet.BeginAnimation(OpacityProperty, fade);
            var shrink = new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
            card.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            card.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            if (yes) ok();
        }
        void Keys(object? _, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
            else if (e.Key == Key.Enter) { Close(true); e.Handled = true; }
        }

        cancel.MouseLeftButtonUp += (_, _) => Close(false);
        confirm.MouseLeftButtonUp += (_, _) => Close(true);
        sheet.MouseLeftButtonDown += (_, _) => Close(false);
        PreviewKeyDown += Keys;

        _sheet = sheet;
        _root.Children.Add(sheet);
        var outEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        sheet.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = outEase });
        var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 } };
        card.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        card.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private static Border SheetButton(string text, Brush bg, Brush hover)
    {
        var b = new Border
        {
            Background = bg, CornerRadius = new CornerRadius(9), Height = 34, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = bg;
        return b;
    }

    private static TextBlock Link(string text, Brush brush, Action click, Brush? hover = null)
    {
        var t = new TextBlock { Text = text, Foreground = brush, Cursor = Cursors.Hand };
        t.MouseEnter += (_, _) => t.Foreground = hover ?? brush;
        t.MouseLeave += (_, _) => t.Foreground = brush;
        t.MouseLeftButtonUp += (_, _) => click();
        return t;
    }

    private static Border Pill(string text, Action click)
    {
        var b = new Border { Background = Blue, CornerRadius = new CornerRadius(13), Padding = new Thickness(13, 4, 13, 5), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12.5 } };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    private static SolidColorBrush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
