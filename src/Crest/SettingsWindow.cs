using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Crest.Services;

namespace Crest;

/// <summary>
/// The full settings app: a sidebar of coloured icons and grouped inset lists, like System Settings on a Mac.
/// Every control writes straight to <see cref="Settings"/> and commits, so the island updates live.
/// </summary>
public sealed class SettingsWindow : Window
{
    private Settings _s;
    /// <summary>A fresh, untouched copy: what "Reset to defaults" puts back.</summary>
    private static readonly Settings Defaults = new();
    /// <summary>One reset per control on the page being built, run by the page's "Reset to defaults".</summary>
    private List<Action>? _resets;
    private readonly StackPanel _side = new() { Margin = new Thickness(10, 12, 10, 12) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(28, 18, 28, 28) };
    private readonly List<(Border row, Action build)> _pages = new();
    private Border? _selected;
    private int _page;
    private Grid _root = new();

    private static readonly Brush Bg = Hex("#1C1C1E"), Side = Hex("#161618"), Card = Hex("#2C2C2E"), Sep = Hex("#3A3A3C"),
        Fg = Brushes.White, Dim = Hex("#98989F"), Faint = Hex("#636366"), Blue = Hex("#0A84FF"), Red = Hex("#FF453A");

    public SettingsWindow(Settings settings)
    {
        _s = settings;
        Title = "Crest Settings";
        Width = 860; Height = 620; MinWidth = 700; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Bg;
        Foreground = Fg;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var root = _root;
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var sideHost = new Border { Background = Side, BorderBrush = Hex("#2A2A2C"), BorderThickness = new Thickness(0, 0, 1, 0) };
        var sideStack = new DockPanel();
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 18, 0, 6) };
        brand.Children.Add(new Border { Width = 26, Height = 12, CornerRadius = new CornerRadius(6), Background = Brushes.Black, BorderBrush = Hex("#48484A"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(new TextBlock { Text = "Crest", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0) });
        DockPanel.SetDock(brand, Dock.Top);
        sideStack.Children.Add(brand);
        var reset = Link("Reset all to defaults", Red, () =>
        {
            Confirm("Reset all settings?",
                "Every page goes back to how Crest shipped. Your music apps and websites are reset too. Start with Windows stays as it is.",
                "Reset all", () =>
                {
                    _s.ResetToDefaults();
                    Show(_page);
                });
        });
        reset.Margin = new Thickness(20, 0, 0, 16);
        DockPanel.SetDock(reset, Dock.Bottom);
        sideStack.Children.Add(reset);
        sideStack.Children.Add(new ScrollViewer { Content = _side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        sideHost.Child = sideStack;
        root.Children.Add(sideHost);
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(_scroll);
        Content = root;

        Page("General", "#8E8E93", "îœ“", General);
        Page("Island", "#0A84FF", "îŸ´", Island);
        Page("Music", "#FF375F", "î£–", Music);
        Page("Media Sources", "#30D158", "î¤¾", Sources);
        Page("Artwork", "#BF5AF2", "î®Ÿ", Artwork);
        Page("Visualizer", "#FF9F0A", "î§™", Visualizer);
        Page("Tabs", "#5E5CE6", "î¢©", Tabs);
        Page("Notifications", "#FF453A", "îª", Notifications);
        Page("Header", "#64D2FF", "î £", Header);
        Page("Motion", "#FFD60A", "î¥…", Motion);
        Page("Shortcuts", "#AC8E68", "î¥", Shortcuts);
        Page("About", "#636366", "î¥†", About);

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

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ pages â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void General()
    {
        Group(null,
            Choice("Shape", new[] { "Notch", "Island" }, () => _s.Style, v => _s.Style = v),
            Toggle("Hide until hovered", "Shrinks to a thin line when nothing is happening.", () => _s.HideUntilHover, v => _s.HideUntilHover = v),
            Toggle("Step aside in fullscreen", "Hides while games, videos and presentations are fullscreen.", () => _s.HideInFullscreen, v => _s.HideInFullscreen = v),
            Toggle("Start with Windows", null, () => _s.StartWithWindows, v => { _s.StartWithWindows = v; StartupService.Apply(v); }, resettable: false));
        Group("Opening",
            Toggle("Open on click only", "Hovering no longer opens the island.", () => _s.OpenOnClickOnly, v => _s.OpenOnClickOnly = v),
            Number("Hover delay", "ms", 0, 2000, () => _s.HoverDelayMs, v => _s.HoverDelayMs = (int)v),
            Number("Close delay", "ms", 0, 5000, () => _s.CloseDelayMs, v => _s.CloseDelayMs = (int)v),
            Number("Stay open after a shortcut", "s", 1, 60, () => _s.PinSeconds, v => _s.PinSeconds = (int)v));
    }

    private void Island()
    {
        Group("Closed size",
            Slider("Idle width", 100, 320, 1, "px", () => _s.IdleWidth, v => _s.IdleWidth = (int)v),
            Slider("Idle height", 20, 44, 1, "px", () => _s.IdleHeight, v => _s.IdleHeight = (int)v),
            Slider("Width while playing", 240, 460, 1, "px", () => _s.MusicWidth, v => _s.MusicWidth = (int)v),
            Slider("Gap from the top (Island shape)", 0, 40, 1, "px", () => _s.IslandOffset, v => _s.IslandOffset = (int)v));
        Group("Look",
            Slider("Corner radius when open", 12, 50, 1, "px", () => _s.ExpandedCorner, v => _s.ExpandedCorner = (int)v),
            Slider("Shoulder curve", 0, 16, 1, "px", () => _s.ShoulderSize, v => _s.ShoulderSize = (int)v),
            Toggle("Drop shadow", null, () => _s.Shadow, v => _s.Shadow = v),
            Toggle("Rim light", "A hairline of light around the glass edge.", () => _s.RimLight, v => _s.RimLight = v),
            Toggle("Liquid glass", "When open, the island is frosted glass: you see the screen behind it, bent at the edge.", () => _s.LiquidGlass, v => _s.LiquidGlass = v),
            Slider("Glass darkness", 0, 90, 1, "%", () => _s.GlassDim, v => _s.GlassDim = (int)v),
            Slider("Frost", 0, 100, 1, "%", () => _s.GlassFrost, v => _s.GlassFrost = (int)v));
    }

    private void Music()
    {
        Group("When paused",
            Number("Keep a paused song on the island for", "min", 0, 1440, () => _s.PausedLingerMinutes, v => _s.PausedLingerMinutes = v, decimals: true),
            Note("Type any time. 0 hides the song as soon as it pauses."));
        Group("Closed island",
            Toggle("Show album art", null, () => _s.ShowArtInCompact, v => _s.ShowArtInCompact = v),
            Toggle("Show artist under the title", null, () => _s.ShowArtistInCompact, v => _s.ShowArtistInCompact = v));
        Group("Open island",
            Toggle("Tap artwork to open the song", "Brings Spotify or the browser tab forward.", () => _s.ArtOpensSource, v => _s.ArtOpensSource = v),
            Toggle("Show elapsed and remaining time", null, () => _s.ShowTimes, v => _s.ShowTimes = v));
        Group("Playback bar",
            Choice("Style", new[] { "Apple", "Knob", "Glow", "Wave", "Line" }, () => _s.ScrubStyle, v => _s.ScrubStyle = v),
            Note("Apple swells under the pointer. Knob has a dot. Glow uses the song's colour. Wave ripples while playing. Line is a hairline."),
            Toggle("Volume bar beside the buttons", "Tap its speaker to mute.", () => _s.MiniVolume, v => _s.MiniVolume = v));
        Group("Volume and brightness",
            Toggle("Scroll over a slider to adjust it", null, () => _s.ScrollAdjustsSliders, v => _s.ScrollAdjustsSliders = v),
            Slider("Each scroll step", 1, 40, 1, "%", () => _s.SliderScrollStep, v => _s.SliderScrollStep = v));
        Group("New song",
            Toggle("Announce new songs", null, () => _s.TrackPeek, v => _s.TrackPeek = v),
            Slider("Announcement length", 0.5, 8, 0.1, "s", () => _s.TrackPeekSeconds, v => _s.TrackPeekSeconds = v));
    }

    private void Sources()
    {
        _resets?.Add(() => _s.MediaSources = new List<string>(Defaults.MediaSources));
        _resets?.Add(() => _s.AnyMediaSource = Defaults.AnyMediaSource);
        Group(null,
            Toggle("Any app", "Show whatever Windows says is playing (WhatsApp, games, videos...).", () => _s.AnyMediaSource, v => { _s.AnyMediaSource = v; Show(_page); }, resettable: false));
        if (_s.AnyMediaSource) return;

        var rows = new List<UIElement>();
        foreach (var src in _s.MediaSources.ToList())
        {
            bool site = src.Contains('.');
            var g = new DockPanel { Margin = new Thickness(14, 0, 8, 0), Height = 44 };
            var remove = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Red, Cursor = Cursors.Hand, ToolTip = "Remove", VerticalAlignment = VerticalAlignment.Center,
                Child = new Border { Width = 10, Height = 2, CornerRadius = new CornerRadius(1), Background = Brushes.White } };
            remove.MouseLeftButtonUp += (_, _) => { _s.MediaSources.RemoveAll(x => x == src); Commit(); Show(_page); };
            DockPanel.SetDock(remove, Dock.Right);
            g.Children.Add(remove);
            var badge = Tile(site ? "#0A84FF" : "#30D158", site ? "î´" : "îŸ¼", 22);
            badge.Margin = new Thickness(0, 0, 12, 0);
            g.Children.Add(badge);
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(new TextBlock { Text = src });
            t.Children.Add(new TextBlock { Text = site ? "Website, in any browser" : "App", Foreground = Dim, FontSize = 11 });
            g.Children.Add(t);
            rows.Add(g);
        }
        if (rows.Count == 0) rows.Add(Note("Nothing will show on the island. Add an app or a website below."));
        Group("Show music from", rows.ToArray());

        var box = new TextBox
        {
            Background = Brushes.Transparent, Foreground = Fg, CaretBrush = Fg, BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13, Padding = new Thickness(0)
        };
        var hint = new TextBlock { Text = "spotify, vlc, music.youtube.com...", Foreground = Faint, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
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
        var row = new DockPanel { Margin = new Thickness(14, 0, 10, 0), Height = 46 };
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        var field = new Grid { Margin = new Thickness(0, 0, 10, 0) };
        field.Children.Add(hint);
        field.Children.Add(box);
        row.Children.Add(field);
        Group("Add an app or website", row,
            Note("Apps: type the app's name, like spotify, vlc or foobar2000.\nWebsites: type the address, like open.spotify.com, and it works in Chrome, Edge, Firefox, Brave and others."));
    }

    private void Artwork()
    {
        Group("Background",
            Toggle("Album art behind everything", "The cover melts into a soft, glowing backdrop when the island opens.", () => _s.ArtBackdrop, v => _s.ArtBackdrop = v),
            Slider("Blur", 0, 100, 1, "", () => _s.BackdropBlur, v => _s.BackdropBlur = (int)v),
            Slider("Darken", 0, 90, 1, "%", () => _s.BackdropDim, v => _s.BackdropDim = (int)v),
            Toggle("Slow drift", "A gentle, living movement, like Apple Music.", () => _s.BackdropMotion, v => _s.BackdropMotion = v),
            Toggle("Cover art in the closed island", "Instead of black, the closed island glows with the same blurred cover while a song is on.", () => _s.ArtInClosedIsland, v => _s.ArtInClosedIsland = v));
        Group("Colour",
            Toggle("Tint controls from the artwork", null, () => _s.ArtAccent, v => _s.ArtAccent = v),
            Swatches("Accent colour", () => _s.AccentHex, v => _s.AccentHex = v));
    }

    private void Visualizer()
    {
        Group(null,
            Toggle("Music visualizer", null, () => _s.Visualizer, v => _s.Visualizer = v),
            Choice("Colour", new[] { "Art", "White", "Accent" }, () => _s.VizColor, v => _s.VizColor = v),
            Slider("Sensitivity", 0.3, 2.5, 0.05, "x", () => _s.VizSensitivity, v => _s.VizSensitivity = v),
            Toggle("Glow", null, () => _s.VizGlow, v => _s.VizGlow = v));
    }

    private void Tabs()
    {
        Group("Show tabs",
            Toggle("Clipboard", null, () => _s.ShowClipTab, v => _s.ShowClipTab = v),
            Toggle("Controls", null, () => _s.ShowControlsTab, v => _s.ShowControlsTab = v),
            Toggle("System", null, () => _s.ShowSystemTab, v => _s.ShowSystemTab = v));
        TabOrder();
        Group(null,
            Choice("Open on", new[] { "Last", "Home", "Clipboard", "Controls", "System" }, () => _s.DefaultTab, v => _s.DefaultTab = v),
            Toggle("Open on what is playing", "While a song is on the island, opening it goes straight to the music, whatever tab you left it on.", () => _s.OpenOnActivity, v => _s.OpenOnActivity = v),
            Toggle("Liquid glass lens", "The selection lifts into a magnifying glass as it moves. You can drag it too.", () => _s.LiquidLens, v => _s.LiquidLens = v),
            Toggle("Scroll to switch tabs", "Scroll anywhere on the open island to move between tabs.", () => _s.ScrollSwitchesTabs, v => _s.ScrollSwitchesTabs = v),
            Slider("Tab scroll speed", 1, 10, 1, "", () => _s.TabScrollSpeed, v => _s.TabScrollSpeed = v),
            Note("Higher moves a tab with every notch of the wheel. Lower needs more scrolling."));
    }

    /// <summary>The tabs, top to bottom in the order they sit on the island, each with arrows to move it.</summary>
    private void TabOrder()
    {
        Resettable(() => _s.TabOrder, v => _s.TabOrder = v);
        var order = _s.TabOrderList();
        var rows = new List<UIElement>();
        for (int i = 0; i < order.Length; i++)
        {
            int at = i;                    // the buttons need this row's own index, not the loop's last value
            string name = order[i];
            bool shown = name switch
            {
                "Clipboard" => _s.ShowClipTab, "Controls" => _s.ShowControlsTab, "System" => _s.ShowSystemTab, _ => true,
            };
            string label = name == "Home" ? "Now playing" : name;
            var arrows = new StackPanel { Orientation = Orientation.Horizontal };
            arrows.Children.Add(MoveButton(true, i > 0, () => MoveTab(order, at, -1), "Move up"));
            arrows.Children.Add(MoveButton(false, i < order.Length - 1, () => MoveTab(order, at, 1), "Move down"));
            var row = Row(label, arrows, shown ? null : "Hidden");
            if (!shown && row is DockPanel d && d.Children[1] is StackPanel t) t.Opacity = 0.55;
            rows.Add(row);
        }
        rows.Add(Note("Top to bottom here is left to right on the island."));
        Group("Tab order", rows.ToArray());
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

    private static UIElement MoveButton(bool up, bool enabled, Action click, string tip)
    {
        var arrow = new Path
        {
            Data = Geometry.Parse(up ? "M 0,6 L 5,1 L 10,6" : "M 0,1 L 5,6 L 10,1"),
            Stroke = Brushes.White, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var idle = Hex("#3A3A3C");
        var b = new Border
        {
            Width = 30, Height = 26, CornerRadius = new CornerRadius(7), Margin = new Thickness(6, 0, 0, 0),
            Background = idle, Child = arrow, ToolTip = tip, Opacity = enabled ? 1 : 0.3,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
        };
        if (enabled)
        {
            b.MouseEnter += (_, _) => b.Background = Hex("#48484A");
            b.MouseLeave += (_, _) => b.Background = idle;
            b.MouseLeftButtonUp += (_, _) => click();
        }
        return b;
    }

    private void Notifications()
    {
        Group("Clipboard",
            Toggle("Announce copies", null, () => _s.ClipboardPeek, v => _s.ClipboardPeek = v),
            Slider("Announcement length", 0.5, 5, 0.1, "s", () => _s.ClipPeekSeconds, v => _s.ClipPeekSeconds = v),
            Number("Remember", "items", 5, 500, () => _s.ClipHistory, v => _s.ClipHistory = (int)v));
        Group("System",
            Toggle("Show volume changes", null, () => _s.VolumePeek, v => _s.VolumePeek = v),
            Toggle("Show when the microphone turns on", null, () => _s.MicPeek, v => _s.MicPeek = v));
    }

    private void Header()
    {
        Group(null,
            Toggle("Clock", null, () => _s.ShowClock, v => _s.ShowClock = v),
            Toggle("24-hour time", null, () => _s.Clock24h, v => _s.Clock24h = v),
            Toggle("Battery", null, () => _s.ShowBattery, v => _s.ShowBattery = v),
            Toggle("Microphone button", null, () => _s.ShowMicButton, v => _s.ShowMicButton = v));
    }

    private void Motion()
    {
        Group(null,
            Slider("Speed", 0.5, 2, 0.05, "x", () => _s.MotionSpeed, v => _s.MotionSpeed = v),
            Slider("Bounce", 0, 0.4, 0.01, "", () => _s.Bounce, v => _s.Bounce = v),
            Note("Bounce 0 is the smooth iOS spring. Raise it for a playful overshoot."),
            Toggle("Reduce motion", "Fades instead of zooming, blurring and lensing.", () => _s.ReduceMotion, v => _s.ReduceMotion = v));
    }

    private void Shortcuts()
    {
        Group("Ctrl + Alt + ...",
            Toggle("N  Open the island", null, () => _s.HotkeyOpen, v => _s.HotkeyOpen = v),
            Toggle("M  Mute the microphone", null, () => _s.HotkeyMic, v => _s.HotkeyMic = v),
            Toggle("C  Pick a colour", null, () => _s.HotkeyColor, v => _s.HotkeyColor = v),
            Toggle("V  Clipboard", null, () => _s.HotkeyClip, v => _s.HotkeyClip = v));
    }

    private void About()
    {
        var v = typeof(SettingsWindow).Assembly.GetName().Version;
        Group(null,
            Row("Version", new TextBlock { Text = $"{v?.Major}.{v?.Minor}.{v?.Build}", Foreground = Dim }),
            Row("Settings file", Link("Show", Blue, () =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", App.DataDir); } catch { }
            })));
        Group(null, Note("Crest is free and open. Made to feel at home on Windows, with the polish of iOS."));
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ navigation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void Page(string name, string color, string glyph, Action build)
    {
        int index = _pages.Count;
        var row = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(Tile(color, glyph, 22));
        sp.Children.Add(new TextBlock { Text = name, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Child = sp;
        row.MouseLeftButtonUp += (_, _) => Show(index);
        row.MouseEnter += (_, _) => { if (row != _selected) row.Background = Hex("#1FFFFFFF"); };
        row.MouseLeave += (_, _) => { if (row != _selected) row.Background = Brushes.Transparent; };
        _side.Children.Add(row);
        _pages.Add((row, build));
        if (name is "Media Sources" or "Tabs" or "Shortcuts") _side.Children.Add(new Border { Height = 10 });
    }

    private StackPanel _body = new();

    private void Show(int index)
    {
        _page = index;
        if (_selected != null) _selected.Background = Brushes.Transparent;
        _selected = _pages[index].row;
        _selected.Background = Hex("#0A84FF");

        double offset = _scroll.VerticalOffset;
        bool same = _body.Tag is int t && t == index;
        _body = new StackPanel { MaxWidth = 620, Tag = index };
        var title = ((TextBlock)((StackPanel)_selected.Child).Children[1]).Text;
        _body.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.Bold, Margin = new Thickness(4, 4, 0, 14) });
        var resets = _resets = new List<Action>();
        _pages[index].build();
        _resets = null;
        if (resets.Count > 0)
        {
            var link = Link($"Reset {title} to defaults", Blue, () =>
            {
                var tile = (Border)((StackPanel)_selected!.Child).Children[0];
                Confirm($"Reset {title}?",
                    $"Only the settings on the {title} page go back to how Crest shipped. Everything else stays as it is.",
                    "Reset", () =>
                    {
                        foreach (var reset in resets) reset();
                        Commit();
                        Show(_page);
                    }, tile.Background);
            });
            link.HorizontalAlignment = HorizontalAlignment.Left;
            link.Margin = new Thickness(16, -4, 0, 8);
            _body.Children.Add(link);
        }
        _scroll.Content = _body;
        if (same) _scroll.ScrollToVerticalOffset(offset);
        else
        {
            _scroll.ScrollToTop();
            _body.Opacity = 0;
            var tt = new TranslateTransform(0, 8);
            _body.RenderTransform = tt;
            var e = new CubicEase { EasingMode = EasingMode.EaseOut };
            _body.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = e });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = e });
        }
    }

    private void Commit() => _s.Commit();

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€ building blocks â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void Group(string? header, params UIElement[] rows)
    {
        if (header != null)
            _body.Children.Add(new TextBlock { Text = header.ToUpperInvariant(), Foreground = Dim, FontSize = 11, Margin = new Thickness(16, 8, 0, 6) });
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0 && rows[i] is not TextBlock { Tag: "note" })
                stack.Children.Add(new Border { Height = 1, Background = Sep, Margin = new Thickness(14, 0, 0, 0), Opacity = 0.7 });
            stack.Children.Add(rows[i]);
        }
        _body.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(12), Child = stack, Margin = new Thickness(0, 0, 0, 18), ClipToBounds = true });
    }

    private UIElement Row(string label, UIElement right, string? sub = null)
    {
        var d = new DockPanel { Margin = new Thickness(16, 0, 14, 0), MinHeight = sub == null ? 44 : 56 };
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
        new TextBlock { Text = text, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 10, 16, 12), Tag = "note" };

    private UIElement Toggle(string label, string? sub, Func<bool> get, Action<bool> set, bool resettable = true)
    {
        if (resettable) Resettable(get, set);
        var c = new CheckBox { Style = (Style)Application.Current.Resources["Switch"], IsChecked = get() };
        c.Click += (_, _) => { set(c.IsChecked == true); Commit(); };
        return Row(label, c, sub);
    }

    private UIElement Choice(string label, string[] options, Func<string> get, Action<string> set)
    {
        Resettable(get, set);
        var bar = new Border { Background = Hex("#1C1C1E"), CornerRadius = new CornerRadius(8), Padding = new Thickness(2) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Child = sp;
        string group = "c" + Guid.NewGuid().ToString("N");
        foreach (var o in options)
        {
            var on = string.Equals(get(), o, StringComparison.OrdinalIgnoreCase);
            var b = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(11, 4, 11, 4), Cursor = Cursors.Hand,
                Background = on ? Hex("#636366") : Brushes.Transparent,
                Child = new TextBlock { Text = o, FontSize = 12, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal }
            };
            b.MouseLeftButtonUp += (_, _) => { set(o); Commit(); Show(_page); };
            sp.Children.Add(b);
        }
        return Row(label, bar);
    }

    private UIElement Slider(string label, double min, double max, double step, string unit, Func<double> get, Action<double> set)
    {
        Resettable(get, set);
        string Fmt(double v) => (step >= 1 ? v.ToString("0") : step >= 0.1 ? v.ToString("0.0") : v.ToString("0.00")) + (unit.Length > 0 ? " " + unit : "");
        var value = new TextBlock { Foreground = Dim, Text = Fmt(get()), MinWidth = 54, TextAlignment = TextAlignment.Right };
        var s = new System.Windows.Controls.Slider
        {
            Minimum = min, Maximum = max, Value = get(), Width = 220, SmallChange = step, LargeChange = step * 10,
            IsSnapToTickEnabled = true, TickFrequency = step, Style = (Style)Application.Current.Resources["ScrubSlider"]
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
        s.VerticalAlignment = VerticalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        value.Margin = new Thickness(8, 0, 0, 0);
        return Row(label, right);
    }

    private UIElement Number(string label, string unit, double min, double max, Func<double> get, Action<double> set, bool decimals = false)
    {
        Resettable(get, set);
        var box = new TextBox
        {
            Text = get().ToString(decimals ? "0.##" : "0", CultureInfo.InvariantCulture), Width = 70,
            Background = Hex("#1C1C1E"), Foreground = Fg, CaretBrush = Fg, BorderBrush = Hex("#48484A"), BorderThickness = new Thickness(1),
            TextAlignment = TextAlignment.Right, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center
        };
        box.Resources.Add(typeof(Border), new Style(typeof(Border)) { Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(6)) } });
        void Apply()
        {
            if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                v = Math.Clamp(decimals ? v : Math.Round(v), min, max);
                set(v);
                Commit();
                box.BorderBrush = Hex("#48484A");
                box.Text = v.ToString(decimals ? "0.##" : "0", CultureInfo.InvariantCulture);
            }
            else box.BorderBrush = Red;
        }
        box.LostKeyboardFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); Keyboard.ClearFocus(); } };
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(ch => char.IsDigit(ch) || (decimals && (ch == '.' || ch == ',')));
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(box);
        right.Children.Add(new TextBlock { Text = unit, Foreground = Dim, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 26 });
        return Row(label, right);
    }

    private UIElement Swatches(string label, Func<string> get, Action<string> set)
    {
        Resettable(get, set);
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hex in new[] { "#0A84FF", "#5E5CE6", "#BF5AF2", "#FF375F", "#FF453A", "#FF9F0A", "#FFD60A", "#30D158", "#64D2FF", "#FFFFFF" })
        {
            bool on = string.Equals(get(), hex, StringComparison.OrdinalIgnoreCase);
            var dot = new Ellipse { Width = 20, Height = 20, Fill = Hex(hex), Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, Stroke = on ? Brushes.White : null, StrokeThickness = on ? 2 : 0 };
            dot.MouseLeftButtonUp += (_, _) => { set(hex); Commit(); Show(_page); };
            sp.Children.Add(dot);
        }
        return Row(label, sp);
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
            Width = 340, Background = Hex("#2C2C2E"), CornerRadius = new CornerRadius(16),
            BorderBrush = Hex("#3FFFFFFF"), BorderThickness = new Thickness(0.6),
            Padding = new Thickness(24, 24, 24, 18),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.92, 0.92),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Opacity = 0.55, Color = Colors.Black }
        };
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;

        var stack = new StackPanel();
        var icon = new Border
        {
            Width = 52, Height = 52, CornerRadius = new CornerRadius(14), Background = tint ?? Red,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14),
            Child = Glyph("îœ¬", 26, Brushes.White)
        };
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = heading, FontSize = 17, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = message, Foreground = Dim, FontSize = 12.5, LineHeight = 18, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 20) });

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
            Background = bg, CornerRadius = new CornerRadius(10), Height = 36, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = bg;
        return b;
    }

    private static TextBlock Link(string text, Brush brush, Action click)
    {
        var t = new TextBlock { Text = text, Foreground = brush, Cursor = Cursors.Hand };
        t.MouseLeftButtonUp += (_, _) => click();
        return t;
    }

    private static Border Pill(string text, Action click)
    {
        var b = new Border { Background = Blue, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 5, 14, 5), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold } };
        b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    private static Style IconButtonStyle() => (Style)Application.Current.Resources["IconButtonSmall"];

    private static SolidColorBrush Hex(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
