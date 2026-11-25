using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Crest.Animation;
using Crest.Interop;
using Crest.Controls;
using Crest.Services;

namespace Crest;

public partial class NotchWindow : Window
{
    private enum Mode { Compact, Peek, Expanded }

    private const double ExpandedW = 500, ExpandedH = 218;
    private const double CompactMusicH = 40;
    private const double TabW = 34;
    private const double K = 0.5523; // cubic Bézier quarter-circle constant

    // Glyphs (Segoe Fluent / MDL2)
    private const string GMic = "", GMicOff = "",
                         GMusic = "", GCopy = "", GCheck = "", GColor = "",
                         GHeadphones = "", GHangUp = "", GWarn = "";

    // Geometry springs: width, height, bottom radius, top offset, shoulder size, hero progress
    private readonly Spring _w = new(190), _h = new(32), _r = new(15), _top = new(0), _s = new(8), _p = new(0);
    // Liquid tab droplet: left and right edges move at different speeds so it stretches
    private readonly Spring _tabL = new(0), _tabR = new(TabW);
    // Lens lift (0 = resting pill, 1 = clear magnifying glass) and artwork backdrop presence
    private readonly Spring _lift = new(0), _bd = new(0);
    private double? _dragStartX;
    private bool _dragging;
    private DateTime? _pausedAt;
    private readonly DispatcherTimer _lingerTimer = new();
    private readonly float[] _scaled = new float[SpectrumService.Bands];
    private SettingsWindow? _settingsWindow;
    private bool _applying;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _animating;
    private const double WinW = 720;
    /// <summary>The island's current outline bounds in window coordinates (for hover tests).</summary>
    private Rect _shapeRect;
    private readonly TranslateTransform _islandShift = new();
    // Page transition between tabs: one spring drives both the leaving and the arriving page.
    private readonly Spring _pg = new(1);
    private FrameworkElement? _pgIn, _pgOut;
    private bool _pgHero;
    private int _pgDir = 1;
    private bool _quietTabs;

    private Mode _mode = Mode.Compact;
    private double _peekWidth = 340;
    private bool _heroOn;

    private readonly Settings _settings = Settings.Load();
    private readonly SystemStatsService _stats = new();
    private readonly SpectrumService _spectrum = new();
    private AudioService _audio = null!;
    private MediaService _media = null!;
    private PrivacyService _privacy = null!;
    private readonly DiscordVoiceService _discord = new();
    private DateTime _callStart;
    private bool _dcWasInCall, _dcWasMuted, _dcWasDeaf;
    private ClipboardService _clipboard = null!;
    private TrayIcon _tray = null!;
    private IntPtr _hwnd;

    private readonly DispatcherTimer _hoverTimer = new();
    private readonly DispatcherTimer _leaveTimer = new();
    private readonly DispatcherTimer _peekTimer = new();
    private readonly DispatcherTimer _vizTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(2) };

    private double _wheelAcc;
    private DateTime _wheelLast, _wheelCool, _listWheelAt;
    private int _lastTab;
    private readonly float[] _synth = new float[SpectrumService.Bands];
    private readonly float[] _flat = new float[SpectrumService.Bands];
    private double _lastViz;
    private bool _hiddenForFullscreen;
    private DateTime _pinnedUntil;
    private DateTime? _outsideSince;

    public NotchWindow()
    {
        InitializeComponent();
        WireSliders();
        PreviewMouseWheel += Island_PreviewWheel;

        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); if (!PointerOnPet()) SetMode(Mode.Expanded); };
        _leaveTimer.Interval = TimeSpan.FromMilliseconds(25); // the close delay lands within a frame or two
        _leaveTimer.Tick += (_, _) => LeaveTick();
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); if (_mode == Mode.Peek) SetMode(Mode.Compact); };
        _vizTimer.Tick += (_, _) => VizTick();
        _tick.Tick += (_, _) => SecondTick();
        _watch.Tick += (_, _) => WatchTick();

        _tabL.Configure(420, 34);
        _tabR.Configure(420, 34);
        Ios(_lift, 0.34, 0.7);
        Ios(_bd, 0.7, 1);
        Island.RenderTransform = _islandShift;
        ShadowShape.RenderTransform = _islandShift;
        // The song has been paused too long: the island goes back to its normal, music-free look.
        _lingerTimer.Tick += (_, _) => { _lingerTimer.Stop(); OnMediaChanged(); UpdateTargets(); };
        _settings.Changed += ApplySettings;

        TabBar.PreviewMouseLeftButtonDown += (_, e) => _dragStartX = e.GetPosition(TabIcons).X;
        TabBar.PreviewMouseMove += TabBar_Move;
        TabBar.PreviewMouseLeftButtonUp += TabBar_Up;
        // LostMouseCapture bubbles: the tab button giving up its own capture when the bar takes it must not end the drag.
        TabBar.LostMouseCapture += (_, e) => { if (e.OriginalSource == TabBar) CancelTabDrag(); };

        Position();
        WireMove();
        WirePet();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Position);
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    // ───────────────────────────── setup ─────────────────────────────

    private void Position()
    {
        Width = 720;
        Height = 360;
        Left = ClampCenter(SystemParameters.PrimaryScreenWidth / 2 + _settings.IslandX) - WinW / 2;
        Top = 0;
    }

    // ───────────────────────────── moving the island ─────────────────────────────
    // Hold the island still for a moment, then drag it along the top edge. Near the middle it
    // snaps to the exact centre and shows a guide, like aligning a layer in a photo editor.

    private const double SnapZone = 18;          // DIPs from the centre that snap to it
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private Point _holdStart;                    // screen DIPs
    private bool _moving, _snapped;
    private double _grab;                        // cursor x minus island centre when the move began
    private readonly ScaleTransform _liftScale = new(1, 1, WinW / 2, 0);

    /// <summary>
    /// Keeps the island's centre where even the open island fits on screen, so opening it
    /// never has to jump. The widest it gets is the expanded panel plus its shoulders.
    /// </summary>
    private static double ClampCenter(double x)
    {
        double screen = SystemParameters.PrimaryScreenWidth;
        double half = ExpandedW / 2 + 24 * 1.75 + 6;
        if (screen <= half * 2) return screen / 2;
        return Math.Clamp(x, half, screen - half);
    }

    private Point CursorDip()
    {
        if (!Native.GetCursorPos(out var p)) return new Point(Left + WinW / 2, 0);
        var src = PresentationSource.FromVisual(this);
        var pt = new Point(p.X, p.Y);
        return src?.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice.Transform(pt) : pt;
    }

    private void WireMove()
    {
        ((FrameworkElement)Content).RenderTransform = _liftScale;
        Island.PreviewMouseLeftButtonDown += Move_Down;
        Island.PreviewMouseMove += Move_Move;
        Island.PreviewMouseLeftButtonUp += Move_Up;
        Island.LostMouseCapture += (_, _) => { if (_moving) EndMove(); };
        _holdTimer.Tick += (_, _) => { _holdTimer.Stop(); BeginMove(); };
    }

    /// <summary>Things you press and drag to use keep their own drag.</summary>
    private bool OnOwnDragControl(DependencyObject? d)
    {
        while (d != null)
        {
            if (d == TabBar || d is Controls.PillSlider || d is Controls.Scrubber ||
                d is System.Windows.Controls.Primitives.ScrollBar || d is System.Windows.Controls.Primitives.Thumb ||
                d is TextBox || d is Slider)
                return true;
            if (d == Island) return false;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Move_Down(object sender, MouseButtonEventArgs e)
    {
        if (_moving || OnOwnDragControl(e.OriginalSource as DependencyObject)) return;
        _holdStart = CursorDip();
        _holdTimer.Stop();
        _holdTimer.Start();
    }

    private void Move_Move(object sender, MouseEventArgs e)
    {
        if (_holdTimer.IsEnabled)
        {
            // Moved before the hold finished: an ordinary press or swipe, not a move.
            var c = CursorDip();
            if (Math.Abs(c.X - _holdStart.X) > 6 || Math.Abs(c.Y - _holdStart.Y) > 6) _holdTimer.Stop();
            return;
        }
        if (!_moving) return;
        Drag(CursorDip().X);
        e.Handled = true;
    }

    private void Move_Up(object sender, MouseButtonEventArgs e)
    {
        _holdTimer.Stop();
        if (!_moving) return;
        e.Handled = true;            // the drop is not a click: don't open the island
        EndMove();
    }

    private void BeginMove()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        _moving = true;
        _hoverTimer.Stop();
        if (_mode == Mode.Expanded) SetMode(Mode.Compact);   // you carry the small pill, not the panel
        _grab = CursorDip().X - (Left + WinW / 2);
        _snapped = Math.Abs(Left + WinW / 2 - SystemParameters.PrimaryScreenWidth / 2) < 0.5;
        Island.CaptureMouse();      // takes the press away from any button under it, so nothing clicks
        Lift(1.045);
        ShowGuide(_snapped);
    }

    private void Drag(double cursorX)
    {
        double mid = SystemParameters.PrimaryScreenWidth / 2;
        double x = ClampCenter(cursorX - _grab);
        bool snap = Math.Abs(x - mid) <= SnapZone;
        if (snap) x = mid;
        if (snap != _snapped)
        {
            _snapped = snap;
            ShowGuide(snap);
        }
        double left = Math.Round(x - WinW / 2, 2);
        if (Left != left) Left = left;
    }

    private void EndMove()
    {
        if (!_moving) return;
        _moving = false;
        if (Island.IsMouseCaptured) Island.ReleaseMouseCapture();
        _hoverTimer.Stop();
        Lift(1);
        ShowGuide(false);
        _settings.IslandX = _snapped ? 0 : Math.Round(Left + WinW / 2 - SystemParameters.PrimaryScreenWidth / 2, 1);
        _settings.Save();
    }

    private void Lift(double scale)
    {
        var a = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(scale > 1 ? 160 : 220))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _liftScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        _liftScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void ShowGuide(bool on)
    {
        SnapGuide.Margin = new Thickness(WinW / 2 - 1, Math.Max(0, _top.Value) + Math.Max(2, _h.Value) + 10, 0, 0);
        SnapGuide.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 90 : 180)));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Native.AddClipboardFormatListener(_hwnd);
        RegisterHotkeys();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _audio = new AudioService(Dispatcher);
        _audio.VolumeChanged += OnVolumeChanged;
        _audio.MicMuteChanged += _ => SyncMicUi();

        _privacy = new PrivacyService();
        _privacy.Changed += OnPrivacyChanged;
        _privacy.Start();

        _discord.Changed += () => Dispatcher.BeginInvoke(OnDiscordChanged);
        if (_settings.DiscordControls) _discord.Start();

        _clipboard = new ClipboardService();
        _clipboard.Copied += OnCopied;
        ClipList.ItemsSource = _clipboard.Items;
        _clipboard.Items.CollectionChanged += (_, _) => SyncClipEmpty();
        SyncClipEmpty();

        _tray = new TrayIcon(
            open: () => { TabHome.IsChecked = true; OpenPinned(); },
            openSettings: OpenSettingsWindow,
            quit: Quit);

        _media = new MediaService(Dispatcher, _settings);
        _media.Changed += OnMediaChanged;
        _media.TrackChanged += OnTrackChanged;

        ApplySettings();
        ApplyShape(immediate: true);
        SyncMicUi();
        SecondTick();
        _tick.Start();
        _watch.Start();
        StartUpdateChecks();
        VersionText.Text = "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        Viz.SetLevels(_flat, 0);

        await _media.InitAsync();

        // Shed start-up allocations once the song, its art and the first frames have settled.
        ScheduleTrim(8);
    }

    private const int HkMic = 1, HkColor = 2, HkClip = 3, HkOpen = 4;

    private void RegisterHotkeys()
    {
        for (int id = 1; id <= 4; id++) Native.UnregisterHotKey(_hwnd, id);
        Register(HkMic, _settings.HotkeyMic, _settings.KeyMic, "Ctrl+Alt+M");
        Register(HkColor, _settings.HotkeyColor, _settings.KeyColor, "Ctrl+Alt+C");
        Register(HkClip, _settings.HotkeyClip, _settings.KeyClip, "Ctrl+Alt+V");
        Register(HkOpen, _settings.HotkeyOpen, _settings.KeyOpen, "Ctrl+Alt+N");
    }

    /// <summary>Stops the shortcuts while Settings records new keys, so pressing one isn't swallowed.</summary>
    public void PauseHotkeys(bool paused)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (paused) for (int id = 1; id <= 4; id++) Native.UnregisterHotKey(_hwnd, id);
        else RegisterHotkeys();
    }

    /// <summary>Registers one shortcut from its keys as text; keys that can't be read fall back to the default.</summary>
    private void Register(int id, bool on, string keys, string fallback)
    {
        if (!on) return;
        if (!HotkeyText.TryParse(keys, out var m, out var k)) HotkeyText.TryParse(fallback, out m, out k);
        uint mods = Native.MOD_NOREPEAT;
        if (m.HasFlag(ModifierKeys.Control)) mods |= Native.MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= Native.MOD_ALT;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= Native.MOD_SHIFT;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= Native.MOD_WIN;
        Native.RegisterHotKey(_hwnd, id, mods, (uint)KeyInterop.VirtualKeyFromKey(k));
    }

    /// <summary>The shortcut keys as the island lists them: only the ones that are on.</summary>
    private void UpdateHotkeyHint()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (_settings.HotkeyOpen) parts.Add(HotkeyText.Normal(_settings.KeyOpen) + " open");
        if (_settings.HotkeyMic) parts.Add(HotkeyText.Normal(_settings.KeyMic) + " mute mic");
        if (_settings.HotkeyColor) parts.Add(HotkeyText.Normal(_settings.KeyColor) + " pick colour");
        if (_settings.HotkeyClip) parts.Add(HotkeyText.Normal(_settings.KeyClip) + " clipboard");
        HotkeyHint.Text = string.Join("  ·  ", parts);
        HotkeyHint.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderMic.ToolTip = "Mute microphone everywhere" + (_settings.HotkeyMic ? $" ({HotkeyText.Normal(_settings.KeyMic)})" : "");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_HOTKEY:
                OnHotkey(wParam.ToInt32());
                handled = true;
                break;
            case Native.WM_CLIPBOARDUPDATE:
                Dispatcher.BeginInvoke(() => _clipboard?.OnClipboardUpdate(), DispatcherPriority.Background);
                break;
            case Native.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            case 0x020E: // WM_MOUSEHWHEEL: sideways scroll on touchpads and tilt wheels
                HorizontalWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                handled = true;
                return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HkMic: ToggleMic(); break;
            case HkColor: PickColor(); break;
            case HkClip: TabClip.IsChecked = true; OpenPinned(); break;
            case HkOpen:
                if (_mode == Mode.Expanded) SetMode(Mode.Compact);
                else { TabHome.IsChecked = true; OpenPinned(); }
                break;
        }
    }

    // ───────────────────────────── state & animation ─────────────────────────────

    /// <summary>Open from a hotkey or tray: stays open until the pointer visits and leaves, or 6 s pass.</summary>
    private void OpenPinned()
    {
        if (_hiddenForFullscreen) ShowFromFullscreen();
        SetMode(Mode.Expanded);
        _pinnedUntil = DateTime.Now.AddSeconds(Math.Max(1, _settings.PinSeconds));
    }

    private void SetMode(Mode mode)
    {
        var previous = _mode;
        _mode = mode;
        if (mode != Mode.Expanded) CancelTabDrag();

        if (mode == Mode.Expanded)
        {
            if (previous != Mode.Expanded)
            {
                _quietTabs = true;      // the page is chosen before the island opens: no slide
                try { ApplyDefaultTab(); } finally { _quietTabs = false; }
                StartGlass();
            }
            _peekTimer.Stop();
            _outsideSince = null;
            _leaveTimer.Start();
            RefreshExpanded();
        }
        else
        {
            _leaveTimer.Stop();
            _glassTimer?.Stop();
            _live?.Stop();
            _pinnedUntil = default;
            FinishPage();
            if (previous == Mode.Expanded) ScheduleTrim(60);
        }

        Fade(CompactView, mode == Mode.Compact);
        Fade(PeekView, mode == Mode.Peek);
        // The expanded content "materializes" (blur, scale, fade) from the island's own spring in ApplyGeometry.
        ExpandedView.IsHitTestVisible = mode == Mode.Expanded;

        if (mode == Mode.Expanded && previous != Mode.Expanded)
            SnapTabDrop();

        UpdateDrift();
        // The shadow's 40 px blur is redrawn on every frame its outline changes, which dropped
        // frames while the island opened and closed. It stays off while the island moves and
        // fades in once it has opened.
        HideShadow();
        _shadowAfterOpen = mode == Mode.Expanded && _settings.Shadow;

        UpdateTargets();
        UpdateHero();
    }

    private DispatcherTimer? _trimTimer;

    /// <summary>Hand back pages touched by the expanded UI once the island is idle again.</summary>
    private void ScheduleTrim(double seconds = 3)
    {
        _trimTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.ApplicationIdle, (_, _) =>
        {
            _trimTimer!.Stop();
            if (_mode == Mode.Expanded) return;
            ReleaseGlass();
            GC.Collect(2, GCCollectionMode.Optimized, false);
            Native.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }, Dispatcher);
        _trimTimer.Stop();
        _trimTimer.Interval = TimeSpan.FromSeconds(seconds);
        _trimTimer.Start();
    }

    private static void Fade(UIElement el, bool show, int delayMs = 80)
    {
        var a = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 260 : 110))
        {
            BeginTime = show ? TimeSpan.FromMilliseconds(delayMs) : TimeSpan.Zero,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        el.BeginAnimation(OpacityProperty, a);
        el.IsHitTestVisible = show;
    }

    private bool IsNotch => _settings.Style != "Island";
    private bool MusicPlaying => _media?.HasSession == true && _media.IsPlaying;
    private TimeSpan Linger => TimeSpan.FromMinutes(Math.Max(0, _settings.PausedLingerMinutes));
    /// <summary>Playing, or paused recently enough that the song still belongs on the island.</summary>
    private bool MusicVisible => _media?.HasSession == true &&
        (_media.IsPlaying || (_pausedAt.HasValue && DateTime.Now - _pausedAt.Value < Linger));
    private double CompactMusicW => Math.Clamp(_settings.MusicWidth, 240, 460);
    private bool LensOn => _settings.LiquidLens && !_settings.ReduceMotion;
    private bool DiscordCall => _settings.DiscordControls && _discord.InCall;
    private bool OnCall => _privacy?.MicInUse == true || DiscordCall;

    private void UpdateTargets()
    {
        bool widgets = false;
        double w, h, r, s, top = IsNotch ? 0 : Math.Clamp(_settings.IslandOffset, 0, 40);
        switch (_mode)
        {
            case Mode.Expanded:
                w = ExpandedW; h = ExpandedH; r = Math.Clamp(_settings.ExpandedCorner, 8, 60);
                s = Math.Clamp(_settings.ShoulderSize, 0, 24) * 1.75;
                ConfigureMs(_settings.OpenAnimMs, 0.88 - 0.8 * _settings.Bounce);   // a breath of life, no visible wobble
                break;
            case Mode.Peek:
                w = _peekWidth; h = 50; r = IsNotch ? 22 : 25; s = 10;
                Configure(0.42, 0.9 - 0.7 * _settings.Bounce);
                break;
            default:
                bool call = OnCall, music = MusicVisible;
                widgets = !call && !music && !_settings.HideUntilHover && Widgets.Any;
                if (call && music) { w = 260; h = 34; }
                else if (call) { w = 240; h = 34; }
                else if (music) { w = CompactMusicW; h = CompactMusicH; }
                else if (_settings.HideUntilHover) { w = 160; h = 6; top = 0; }
                else
                {
                    w = Math.Clamp(_settings.IdleWidth, 60, 400); h = Math.Clamp(_settings.IdleHeight, 6, 44);
                    if (PetOn)
                    {
                        // Room for the cat: tall enough to stand in, wide enough to go for a walk.
                        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                        h = Math.Max(h, Math.Min(44, Math.Ceiling(Pet.CatHeight(dpi) + 4)));
                        w = Math.Max(w, Pet.CatWidth(dpi) + 100);
                    }
                    if (widgets && _settings.WidgetsGrow)
                    {
                        // Room for the corner widgets, and between them for the pet (or a small gap).
                        double lw = Widgets.LeftWidth, rw = Widgets.RightWidth;
                        double middle = PetOn ? Pet.CatWidth(VisualTreeHelper.GetDpi(this).DpiScaleX) + 40 : lw > 0 && rw > 0 ? 20 : 0;
                        w = Math.Max(w, Math.Ceiling(lw + rw + 24 + middle));
                    }
                }
                if (_privacy?.CameraInUse == true && !call) w += 16;
                r = IsNotch ? Math.Min(h / 2, 15) : h / 2;
                s = h < 10 ? 3 : Math.Clamp(_settings.ShoulderSize, 0, 24);
                ConfigureMs(_settings.CloseAnimMs, 1.0);    // closing settles without bounce
                break;
        }
        if (!IsNotch) s = 0;
        _w.Target = w; _h.Target = h; _r.Target = r; _top.Target = top; _s.Target = s;
        _p.Target = _mode == Mode.Expanded ? 1 : 0;
        StartAnimating();
        ShowWidgets(widgets && _mode == Mode.Compact && !_hiddenForFullscreen);
        UpdatePet();
    }

    // ───────────────────────────── closed-island widgets ─────────────────────────────

    private IdleWidgets? _widgets;
    private IdleWidgets Widgets => _widgets ??= new IdleWidgets(IdleLeft, IdleRight);
    private bool? _widgetsShown;
    private bool WidgetsShown => _widgetsShown == true;

    private void ShowWidgets(bool show)
    {
        if (show == _widgetsShown) return;
        _widgetsShown = show;
        foreach (var p in new UIElement[] { IdleLeft, IdleRight })
            p.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 260 : 110))
            { BeginTime = TimeSpan.FromMilliseconds(show ? 100 : 0), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ───────────────────────────── pixel pet ─────────────────────────────

    private bool PetOn => _settings.PixelPet && !_settings.HideUntilHover;
    private bool _petShown;

    private void WirePet()
    {
        Pet.Area = () =>
        {
            double w = Math.Max(20, _w.Value), h = Math.Max(2, _h.Value);
            double inset = Math.Max(Math.Clamp(_r.Value, 0, h / 2), 10) + 2;
            double cam = _privacy?.CameraInUse == true ? 16 : 0;
            // The pet walks between the corner widgets.
            double lw = WidgetsShown ? Widgets.LeftWidth : 0, rw = WidgetsShown ? Widgets.RightWidth : 0;
            double left = Math.Max(inset, lw > 0 ? 12 + lw + 8 : 0), right = Math.Max(inset, rw > 0 ? 12 + rw + 8 : 0) + cam;
            return ((WinW - w) / 2 + left, (WinW + w) / 2 - right, h - 1.5);
        };
        Pet.Petted = PointerOnPet;
        // Resting on the cat pets it; the rest of the island still opens on hover as before.
        Island.MouseMove += (_, _) =>
        {
            if (_mode == Mode.Expanded || _settings.OpenOnClickOnly || _moving) return;
            if (PointerOnPet()) _hoverTimer.Stop();
            else if (!_hoverTimer.IsEnabled)
            {
                _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.HoverDelayMs));
                _hoverTimer.Start();
                PrepareGlass();
                WarmUp(Math.Max(0, _settings.HoverDelayMs) + 300, content: true);
            }
        };
    }

    private bool PointerOnPet() =>
        _petShown && Native.GetCursorPos(out var p) && Pet.IsOnCat(new Point(p.X, p.Y));

    /// <summary>The cat only shows while the island is idle: no call, no music, closed, not fullscreen.</summary>
    private void UpdatePet()
    {
        Pet.Cell = _settings.PetSize == "Small" ? 1 : 2;
        Pet.SetPet(_settings.PetKind, _settings.PetLook);
        Pet.RightCorner = _settings.PetCorner == "Right";
        bool show = PetOn && _mode == Mode.Compact && !OnCall && !MusicVisible && !_hiddenForFullscreen;
        if (show == _petShown) return;
        _petShown = show;
        Pet.Running = show;
        Pet.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 260 : 110))
        { BeginTime = TimeSpan.FromMilliseconds(show ? 120 : 0) });
    }

    /// <summary>
    /// iOS-style content reveal: the panels come into focus (blur → sharp), grow from 0.92 and
    /// fade in, all locked to the island's open progress so shape and content move as one.
    /// </summary>
    private void Materialize()
    {
        double p = Math.Clamp(_p.Value, 0, 1);
        double t = Math.Clamp((p - 0.3) / 0.6, 0, 1);
        double q = t * t * (3 - 2 * t);
        ExpandedView.Opacity = q;
        // Fully closed: hide the panel (keeping its layout) so nothing inside keeps animating unseen.
        var shown = q > 0.001 ? Visibility.Visible : Visibility.Hidden;
        if (ExpandedView.Visibility != shown) ExpandedView.Visibility = shown;
        // The panel drops out of the notch: a touch small and a touch high, settling as it sharpens.
        bool still = _settings.ReduceMotion;
        double sc = still ? 1 : 0.94 + 0.06 * q;
        ExpandedScale.ScaleX = sc;
        ExpandedScale.ScaleY = sc;
        ExpandedShift.Y = still ? 0 : -10 * (1 - q);

        // The artwork glow blooms first and leaves last: it is the room the content sits in.
        // With "cover art in the closed island" it is already there when closed, so it simply stays.
        double reveal = _settings.ArtInClosedIsland ? 1 : p * p * (3 - 2 * p);
        double b = reveal * Math.Clamp(_bd.Value, 0, 1);
        // The cover grows with the island: smaller in the closed pill, full size once open.
        double grow = still ? 1 : 0.74 + 0.26 * (p * p * (3 - 2 * p));
        BackdropGrow.ScaleX = grow;
        BackdropGrow.ScaleY = grow;
        Backdrop.Opacity = b;
        var vis = b > 0.002 ? Visibility.Visible : Visibility.Collapsed;
        if (Backdrop.Visibility != vis) Backdrop.Visibility = vis;

        // The glass clears as the island opens, a little ahead of the content, and hides behind a full cover glow.
        double g = 0;
        if (GlassOn && GlassImage.Source != null && b < 0.995)
        {
            double gt = Math.Clamp(p / 0.45, 0, 1);
            g = gt * gt * (3 - 2 * gt);
        }
        if (Glass.Opacity != g) Glass.Opacity = g;
        if (_live != null) _live.Paused = b >= 0.995 || _animating; // hidden, or moving: hold the picture
        var gvis = g > 0.002 ? Visibility.Visible : Visibility.Collapsed;
        if (Glass.Visibility != gvis) Glass.Visibility = gvis;

        // Text that is moving uses animation hinting so glyphs glide instead of snapping pixel to pixel.
        var hint = (p > 0.002 && p < 0.998) || _pgIn != null ? TextHintingMode.Animated : TextHintingMode.Auto;
        if (TextOptions.GetTextHintingMode(IslandContent) != hint) TextOptions.SetTextHintingMode(IslandContent, hint);
    }

    // ── Liquid glass ──
    private GlassEffect? _glass;
    private ImageBrush? _frost;
    private DispatcherTimer? _glassTimer;
    private bool _glassBusy;
    private (double w, double h, double r, double s, double top) _glassShape;

    private bool GlassOn => _settings.LiquidGlass;

    /// <summary>Photographs the screen behind the island and processes it off the UI thread.</summary>
    private async void CaptureGlass()
    {
        if (!GlassOn || _hwnd == IntPtr.Zero || _glassBusy || _live?.Running == true) return;
        var grab = GlassCapture.Grab(_hwnd);
        if (grab is not { } g) return;
        GlassCapture.Shot shot;
        _glassBusy = true;
        try { shot = await GlassCapture.ProcessAsync(g.px, g.w, g.h); }
        finally { _glassBusy = false; }
        if (_mode != Mode.Expanded) return;
        ShowGlass(shot);
    }

    private void EnsureGlass()
    {
        if (_glass != null) return;
        _frost = new ImageBrush { Stretch = Stretch.Fill };
        _glass = new GlassEffect { Frost = _frost };
        GlassImage.Effect = _glass;
        // The glass only changes when the island moves or a new picture arrives: keep the shaded result.
        Glass.CacheMode = new BitmapCache();
        _glassShape = default;
        UpdateGlassLook();
    }

    private void ShowGlass(GlassCapture.Shot shot)
    {
        EnsureGlass();
        if (Environment.GetEnvironmentVariable("CREST_GLASS_DUMP") is { Length: > 0 } dump)
        { SavePng(shot.Sharp, dump); SavePng(shot.Frost, dump + ".frost.png"); }
        GlassImage.Source = shot.Sharp;
        _frost!.ImageSource = shot.Frost;
        Materialize();
    }

    // The picture for the glass is taken while the pointer rests on the closed island (the hover
    // delay), when nothing is moving. Grabbing it as the island starts to open blocked the UI
    // thread for 45-70 ms and dropped the first frames of the animation.
    private System.Threading.Tasks.Task<GlassCapture.Shot>? _preShot;
    private DateTime _preShotAt;
    private bool _glassAfterOpen;

    private void PrepareGlass()
    {
        if (!GlassOn || _hwnd == IntPtr.Zero || _mode == Mode.Expanded || _live?.Running == true) return;
        if (_preShot != null && DateTime.Now - _preShotAt < TimeSpan.FromMilliseconds(700)) return;
        EnsureGlass();
        var grab = GlassCapture.Grab(_hwnd);
        if (grab is not { } g) return;
        _preShot = GlassCapture.ProcessAsync(g.px, g.w, g.h);
        _preShotAt = DateTime.Now;
    }

    private async void UsePreparedGlass(System.Threading.Tasks.Task<GlassCapture.Shot> pre)
    {
        GlassCapture.Shot shot;
        try { shot = await pre; } catch { return; }
        if (_mode == Mode.Expanded) ShowGlass(shot);
    }

    private static void SavePng(BitmapSource src, string path)
    {
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var f = System.IO.File.Create(path);
            enc.Save(f);
        }
        catch { }
    }

    private void StartGlass()
    {
        if (!GlassOn) return;
        var pre = _preShot;
        _preShot = null;
        if (pre != null && DateTime.Now - _preShotAt < TimeSpan.FromSeconds(3)) UsePreparedGlass(pre);
        else _glassAfterOpen = true; // opened without a hover (shortcut, tray): photograph once it has opened
        // Live: keep up with what is behind (a video, a page scrolling) once per screen refresh.
        if (_hwnd != IntPtr.Zero)
        {
            _live ??= new GlassLive(_hwnd, Dispatcher, ApplyLive);
            _live.Paused = true; // the opening animation runs first
            if (_live.Start()) { _glassAfterOpen = false; return; }
        }
        // Fallback: a new picture now and then, but never mid-animation.
        _glassTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Background, (_, _) =>
        {
            if (_mode != Mode.Expanded) { _glassTimer!.Stop(); return; }
            if (!_animating && Glass.Visibility == Visibility.Visible) CaptureGlass();
        }, Dispatcher);
        _glassTimer.Start();
    }

    private GlassLive? _live;
    private WriteableBitmap? _liveSharp, _liveFrost;

    /// <summary>Puts a live frame into the glass. Reuses the same two bitmaps, so nothing is allocated per frame.</summary>
    private void ApplyLive(GlassLive.Frame f)
    {
        if (_mode != Mode.Expanded || !GlassOn) return;
        EnsureGlass();
        if (_liveSharp is not { } sb || sb.PixelWidth != f.W || sb.PixelHeight != f.H)
            _liveSharp = sb = new WriteableBitmap(f.W, f.H, 96, 96, PixelFormats.Bgr32, null);
        if (_liveFrost is not { } fb || fb.PixelWidth != f.FW || fb.PixelHeight != f.FH)
            _liveFrost = fb = new WriteableBitmap(f.FW, f.FH, 96, 96, PixelFormats.Bgr32, null);
        sb.WritePixels(new Int32Rect(0, 0, f.W, f.H), f.Sharp, f.W * 4, 0);
        fb.WritePixels(new Int32Rect(0, 0, f.FW, f.FH), f.Frost, f.FW * 4, 0);
        bool first = GlassImage.Source != sb;
        if (first) GlassImage.Source = sb;
        if (_frost!.ImageSource != fb) _frost.ImageSource = fb;
        if (first) Materialize();
    }

    private void ReleaseGlass()
    {
        _live?.Stop();
        _glassTimer?.Stop();
        GlassImage.Source = null;
        if (_frost != null) _frost.ImageSource = null;
        Glass.Visibility = Visibility.Collapsed;
    }

    /// <summary>Tells the shader the island's outline in window coordinates: the body and its shoulders.</summary>
    private void UpdateGlassShape(double w, double h, double r, double s, double top)
    {
        if (_glass == null) return;
        var key = (w, h, r, s, top);
        if (key == _glassShape) return;
        _glassShape = key;
        if (GlassShift.Y != -top) GlassShift.Y = -top;
        double L = (WinW - w) / 2;
        // In notch style the top is the screen edge, so it gets no lens.
        _glass.Box = new System.Windows.Media.Media3D.Point4D(L, IsNotch ? -400 : top, L + w, top + h);
        _glass.Look = new System.Windows.Media.Media3D.Point4D(r, Math.Clamp(_settings.GlassDim, 0, 90) / 100.0, 1.35, 0.16);
        // The shoulders are part of the glass too: same frost, and the lens follows their curve.
        _glass.Misc = new System.Windows.Media.Media3D.Point4D(Math.Clamp(_settings.GlassFrost, 0, 100) / 100.0, 0.06, IsNotch ? s : 0, top);
    }

    private void UpdateGlassLook()
    {
        if (_glass == null) return;
        _glass.SizeLens = new System.Windows.Media.Media3D.Point4D(WinW, 360, 20, 16);
        _glassShape = default;
        ApplyGeometry();
    }

    /// <summary>Spring set from iOS terms: response (s) and damping fraction.</summary>
    private static void Ios(Spring s, double response, double zeta)
    {
        double k = Math.Pow(2 * Math.PI / response, 2);
        s.Configure(k, 4 * Math.PI * zeta / response);
    }

    private void Configure(double response, double zeta)
    {
        // Every channel shares one response and damping so the shape moves as a single solid body:
        // channels out of phase with each other were what read as a shiver on open.
        response /= Math.Clamp(_settings.MotionSpeed, 0.4, 2.5);
        zeta = Math.Clamp(zeta, 0.55, 1);
        Ios(_w, response, zeta); Ios(_h, response, zeta); Ios(_r, response, zeta);
        Ios(_s, response, zeta); Ios(_p, response, zeta);
        Ios(_top, response, 1);
    }

    /// <summary>
    /// Springs that arrive in the given time: within 0.1%, a fraction of a pixel, so no slow crawl
    /// is left after it. Critical damping gets there at omega*t = 9.23; an underdamped spring when
    /// its envelope e^(-zeta*omega*t) falls to 0.1%.
    /// </summary>
    private void ConfigureMs(int ms, double zeta)
    {
        double d = Math.Clamp(ms, 60, 3000) / 1000.0;
        zeta = Math.Clamp(zeta, 0.55, 1);
        double response = zeta >= 0.999 ? 2 * Math.PI * d / 9.23 : 2 * Math.PI * zeta * d / 6.91;
        Configure(response * Math.Clamp(_settings.MotionSpeed, 0.4, 2.5), zeta); // exact: Configure divides by speed
    }

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        // If the render loop is already ticking (warmed up during the hover wait), continue from its last frame.
        _startFrames = 0;
        _lastRender = _clock.Elapsed.TotalMilliseconds - _lastWarmTick < 2 * FramePeriodMs ? _lastWarmRender : TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    // ── Frame pacing ──
    // WPF's render loop sleeps while nothing changes, and its first frames after waking are late:
    // that was the hitch at the very start of every open and close. Listening for frames is not
    // enough to keep it awake (unchanged frames are never drawn), so during the hover and leave
    // waits, and while the island is open, one invisible pixel under the island changes every
    // frame. The pipeline is then already running when the animation starts.
    private static readonly double FramePeriodMs = 1000.0 / Native.RefreshRate();
    private TimeSpan _lastRender, _lastWarmRender;
    private double _lastWarmTick = -1e9, _warmUntil;
    private int _startFrames;
    private System.Windows.Shapes.Rectangle? _keepAwake;
    private bool _warming;

    /// <summary>Starts the render loop a little before an animation so its first frame lands on time.</summary>
    private void WarmUp(double ms = 400, bool content = false)
    {
        _warmUntil = Math.Max(_warmUntil, _clock.Elapsed.TotalMilliseconds + ms);
        // Show the panel (still transparent) so it is drawn once now, not in the middle of opening.
        if (content && ExpandedView.Visibility != Visibility.Visible) ExpandedView.Visibility = Visibility.Visible;
        if (_warming) return;
        _warming = true;
        CompositionTarget.Rendering += WarmTick;
    }

    private void WarmTick(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        _lastWarmTick = now;
        _lastWarmRender = ((RenderingEventArgs)e).RenderingTime;
        if (_keepAwake == null)
        {
            // Under the top centre of the island, which is always black, at 1-2% opacity: never visible.
            _keepAwake = new System.Windows.Shapes.Rectangle
            {
                Width = 2, Height = 2, Fill = Brushes.Black, Opacity = 0.01, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(WinW / 2 - 1, 0, 0, 0),
            };
            ((Grid)Content).Children.Insert(0, _keepAwake);
        }
        _keepAwake.Opacity = _keepAwake.Opacity > 0.015 ? 0.01 : 0.02;
        if (now < _warmUntil || _animating) return;
        CompositionTarget.Rendering -= WarmTick;
        _warming = false;
        if (!_animating) Materialize();   // hides the panel again if it never opened
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // Step by the frame's presentation time, not by when this callback happened to run:
        // wall-clock gaps jitter by several ms, which made the motion stutter.
        var rt = ((RenderingEventArgs)e).RenderingTime;
        if (_warming) { _lastWarmTick = _clock.Elapsed.TotalMilliseconds; _lastWarmRender = rt; }
        if (rt == _lastRender) return;
        double dt = _lastRender == TimeSpan.Zero ? FramePeriodMs / 1000.0 : Math.Clamp((rt - _lastRender).TotalSeconds, 1 / 500.0, 1 / 20.0);
        _lastRender = rt;
        // The first frames after the loop wakes are often late. Stepping by the full gap made the
        // island jump right at the start of every open and close; there it only ever advances one
        // refresh per shown frame, so the start is always continuous.
        if (_startFrames < 10) { _startFrames++; dt = Math.Min(dt, FramePeriodMs / 1000.0); }

        int steps = Math.Max(1, (int)Math.Ceiling(dt * 240));
        double h = dt / steps;
        bool moving = false;
        for (int i = 0; i < steps; i++)
        {
            moving |= _w.Step(h);
            moving |= _h.Step(h);
            moving |= _r.Step(h);
            moving |= _top.Step(h);
            moving |= _s.Step(h);
            moving |= _p.Step(h);
            moving |= _tabL.Step(h);
            moving |= _tabR.Step(h);
            moving |= _lift.Step(h);
            moving |= _bd.Step(h);
            moving |= _pg.Step(h);
        }
        // The lens settles back into a pill once the bubble has arrived.
        if (_lift.Target > 0 && !_dragging &&
            Math.Abs(_tabL.Target - _tabL.Value) < 2.5 && Math.Abs(_tabR.Target - _tabR.Value) < 2.5)
        {
            _lift.Target = 0;
            moving = true;
        }
        ApplyGeometry();
        LayoutPages();

        if (!moving)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
            if (_live != null) _live.Paused = Backdrop.Opacity >= 0.995;
            if (_shadowAfterOpen) { _shadowAfterOpen = false; if (_mode == Mode.Expanded) ShowShadow(); }
            if (_glassAfterOpen) { _glassAfterOpen = false; if (_mode == Mode.Expanded) CaptureGlass(); }
        }
    }

    private bool _shadowAfterOpen;

    private void HideShadow()
    {
        ShadowShape.BeginAnimation(OpacityProperty, null);
        ShadowShape.Opacity = 0;
        ShadowShape.Visibility = Visibility.Collapsed;
    }

    private void ShowShadow()
    {
        if (ShadowShape.Visibility == Visibility.Visible) return;
        ShadowShape.Visibility = Visibility.Visible;
        ShadowShape.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase() });
    }

    private (double w, double h, double r, double s, bool notch) _builtShape;

    private void ApplyGeometry()
    {
        double w = Math.Max(20, _w.Value);
        double h = Math.Max(2, _h.Value);
        double r = Math.Clamp(_r.Value, 0, Math.Min(h / 2, w / 2));
        double s = Math.Clamp(_s.Value, 0, Math.Max(0, h - r));
        double top = Math.Max(0, _top.Value);

        // The island and its content are fixed, window-sized layers. Only the outline moves, at
        // sub-pixel precision and centred exactly, so nothing snaps from pixel to pixel as it grows.
        double x0 = (WinW - w) / 2 - s;
        // Rebuild the outline only when it really changed. A new outline re-renders the cached
        // shadow blur and re-clips the whole island, so redoing it on frames where only a tab,
        // the lens or a page moves was most of a tab switch's GPU cost.
        var shape = (w, h, r, s, IsNotch);
        if (shape != _builtShape)
        {
            _builtShape = shape;
            var body = BuildOutline(w, h, r, s, IsNotch, x0);
            IslandShape.Data = body;
            ShadowShape.Data = body;
            // Clip to the whole outline, curved shoulders included, so the cover art reaches them seamlessly.
            IslandContent.Clip = body;
        }
        if (_islandShift.Y != top) _islandShift.Y = top;
        _shapeRect = new Rect(x0, 0, w + 2 * s, top + h);
        UpdateGlassShape(w, h, r, s, top);

        // Only the small compact rows follow the live size.
        double cw = Math.Max(0, w - 24), pw = Math.Max(0, w - 28);
        if (CompactView.Width != cw) CompactView.Width = cw;
        if (CompactView.Height != h) CompactView.Height = h;
        if (PeekView.Width != pw) PeekView.Width = pw;
        if (PeekView.Height != h) PeekView.Height = h;

        Materialize();
        LayoutHero();
        LayoutTabDrop();
    }

    /// <summary>
    /// The island outline. In notch style the top corners flare outwards into concave
    /// shoulders (size s) so the shape melts into the top of the screen instead of being cut.
    /// </summary>
    private static Geometry BuildOutline(double w, double h, double r, double s, bool notch, double x0 = 0)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double ox = s;
            if (notch)
            {
                c.BeginFigure(new Point(0, 0), true, true);
                c.LineTo(new Point(ox + w + s, 0), false, false);
                if (s > 0.01)
                    c.BezierTo(new Point(ox + w + s - K * s, 0), new Point(ox + w, s - K * s), new Point(ox + w, s), true, true);
            }
            else
            {
                c.BeginFigure(new Point(r, 0), true, true);
                c.LineTo(new Point(w - r, 0), true, true);
                c.BezierTo(new Point(w - r + K * r, 0), new Point(w, r - K * r), new Point(w, r), true, true);
            }

            c.LineTo(new Point(ox + w, h - r), true, true);
            c.BezierTo(new Point(ox + w, h - r + K * r), new Point(ox + w - r + K * r, h), new Point(ox + w - r, h), true, true);
            c.LineTo(new Point(ox + r, h), true, true);
            c.BezierTo(new Point(ox + r - K * r, h), new Point(ox, h - r + K * r), new Point(ox, h - r), true, true);

            if (notch)
            {
                c.LineTo(new Point(ox, s), true, true);
                if (s > 0.01)
                    c.BezierTo(new Point(ox, s - K * s), new Point(K * s, 0), new Point(0, 0), true, true);
            }
            else
            {
                c.LineTo(new Point(0, r), true, true);
                c.BezierTo(new Point(0, r - K * r), new Point(r - K * r, 0), new Point(r, 0), true, true);
            }
        }
        if (x0 != 0) g.Transform = new TranslateTransform(x0, 0);
        g.Freeze();
        return g;
    }

    private void ApplyShape(bool immediate = false)
    {
        UpdateTargets();
        if (immediate)
        {
            foreach (var s in new[] { _w, _h, _r, _top, _s, _p }) { s.Value = s.Target; s.Velocity = 0; }
            ApplyGeometry();
        }
    }

    // ───────────────────────────── hero morph ─────────────────────────────

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// Art, title, artist and waveform live in one layer and glide between their
    /// closed and open spots, driven by the same spring as the island itself.
    /// Coordinates are measured from the island's centre so both layouts stay aligned.
    /// </summary>
    private void LayoutHero()
    {
        double p = _p.Value;            // may overshoot a touch: that is the physical feel
        double pc = Math.Clamp(p, 0, 1);
        double wc = CompactMusicW, we = ExpandedW;
        bool artC = _settings.ShowArtInCompact, artistC = _settings.ShowArtistInCompact;
        double textC = artC ? 42 : 14;
        double X(double xc, double xe) => Lerp(xc - wc / 2, xe - we / 2, p) + WinW / 2;

        // Cover art
        double art = Lerp(26, 64, pc);
        HeroArt.Width = HeroArt.Height = art;
        HeroArt.CornerRadius = new CornerRadius(Lerp(8, 15, pc));
        Canvas.SetLeft(HeroArt, X(8, 18));
        Canvas.SetTop(HeroArt, Lerp(7, 54, p));
        HeroArt.Opacity = artC ? 1 : pc;
        HeroPlaceholder.FontSize = Lerp(12, 24, pc);

        // Title and artist share a left edge in both states
        double titleScale = Lerp(12.5 / 15, 1, pc);
        double titleAvail = Lerp(wc - textC - 8 - 34 - 10, we - 96 - 18 - 60 - 14, pc);
        HeroTitleScale.ScaleX = HeroTitleScale.ScaleY = titleScale;
        HeroTitle.Width = Math.Max(10, titleAvail / titleScale);
        Canvas.SetLeft(HeroTitle, X(textC, 96));
        Canvas.SetTop(HeroTitle, Lerp(artistC ? 3.5 : 11.5, 58, p));

        double artistScale = Lerp(11 / 12.5, 1, pc);
        HeroArtistScale.ScaleX = HeroArtistScale.ScaleY = artistScale;
        HeroArtist.Width = Math.Max(10, titleAvail / artistScale);
        Canvas.SetLeft(HeroArtist, X(textC, 96));
        HeroArtist.Opacity = artistC ? 1 : pc;
        Canvas.SetTop(HeroArtist, Lerp(19.5, 81, p));

        // Waveform
        double vw = Lerp(34, 64, pc), vh = Lerp(18, 30, pc);
        Viz.Width = vw;
        Viz.Height = vh;
        Canvas.SetLeft(Viz, X(wc - 8 - 34, we - 18 - 64));
        Canvas.SetTop(Viz, Lerp(11, 62, p));
    }

    private void UpdateHero()
    {
        bool on = _mode switch
        {
            Mode.Compact => MusicVisible && !OnCall,
            Mode.Expanded => TabHome.IsChecked == true,
            _ => false,
        };
        if (on != _heroOn)
        {
            _heroOn = on;
            if (_pgHero) HeroLayer.BeginAnimation(OpacityProperty, null);
            else HeroLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 260 : 120))
            {
                BeginTime = on && _mode == Mode.Compact ? TimeSpan.FromMilliseconds(90) : TimeSpan.Zero,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        // Only the cover art is clickable, and only when the island is open.
        HeroLayer.IsHitTestVisible = _mode == Mode.Expanded && on;
        HeroArt.ToolTip = MusicVisible && _media!.Source.Length > 0 ? "Open in " + _media.Source : null;
        UpdateVisualizerState();
    }

    // ───────────────────────────── liquid glass tabs ─────────────────────────────

    private int CurrentTab =>
        TabClip.IsChecked == true ? 1 : TabControls.IsChecked == true ? 2 :
        TabSystem.IsChecked == true ? 3 : TabSettings.IsChecked == true ? 4 : 0;

    /// <summary>A tab by its fixed id (0 Home, 1 Clipboard, 2 Controls, 3 System, 4 Settings), the same id its page uses.</summary>
    private RadioButton TabById(int id) => id switch
    {
        1 => TabClip, 2 => TabControls, 3 => TabSystem, 4 => TabSettings, _ => TabHome,
    };

    private RadioButton? TabByName(string name) => name switch
    {
        "Home" => TabHome, "Clipboard" => TabClip, "Controls" => TabControls, "System" => TabSystem, "Settings" => TabSettings, _ => null,
    };

    private RadioButton[]? _tabs;
    /// <summary>The tabs in the order they are shown, left to right (the user can change it).</summary>
    private RadioButton[] Tabs => _tabs ??= new[] { TabHome, TabClip, TabControls, TabSystem, TabSettings };

    /// <summary>Puts the tab buttons in the saved order. Only touches the bar when the order really changed.</summary>
    private void ApplyTabOrder()
    {
        var order = Array.ConvertAll(_settings.TabOrderList(), n => TabByName(n)!);
        bool same = TabIcons.Children.Count == order.Length;
        for (int i = 0; same && i < order.Length; i++) same = TabIcons.Children[i] == order[i];
        if (same) { _tabs = order; return; }
        TabIcons.Children.Clear();
        foreach (var t in order) TabIcons.Children.Add(t);
        _tabs = order;
    }

    /// <summary>Where a tab sits among the shown tabs, left to right.</summary>
    private int TabPos(int id)
    {
        var target = TabById(id);
        int n = 0;
        foreach (var t in Tabs)
        {
            if (t == target) return n;
            if (t.Visibility == Visibility.Visible) n++;
        }
        return n;
    }

    private RadioButton[] VisibleTabs => Array.FindAll(Tabs, t => t.Visibility == Visibility.Visible);

    /// <summary>Left edge of a tab, counting only the tabs that are shown.</summary>
    private double TabSlotX(int id) => TabPos(id) * TabW;

    private void MoveTabDrop()
    {
        double l = TabSlotX(CurrentTab), r = l + TabW;
        bool right = l > _tabL.Target;
        // The leading edge darts ahead, the trailing edge catches up: the droplet stretches, then settles.
        (right ? _tabR : _tabL).Configure(480, 34);
        (right ? _tabL : _tabR).Configure(210, 24);
        _tabL.Target = l;
        _tabR.Target = r;
        // It lifts off the bar as clear glass for the journey.
        if (LensOn && Math.Abs(l - _tabL.Value) > 2) _lift.Target = 1;
        StartAnimating();
    }

    private void SnapTabDrop()
    {
        _tabL.Target = _tabL.Value = TabSlotX(CurrentTab);
        _lift.Target = _lift.Value = 0;
        _lift.Velocity = 0;
        _tabR.Target = _tabR.Value = _tabL.Value + TabW;
        _tabL.Velocity = _tabR.Velocity = 0;
        LayoutTabDrop();
    }

    private void LayoutTabDrop()
    {
        double l = _tabL.Value, r = _tabR.Value;
        if (r < l + 8) r = l + 8;
        double width = r - l;
        Canvas.SetLeft(TabDrop, l);
        TabDrop.Width = width;
        double stretch = Math.Clamp((width - TabW) / TabW, 0, 1);

        if (!LensOn)
        {
            double v = Math.Clamp(Math.Abs(_tabL.Velocity + _tabR.Velocity) / 2 / 500, 0, 1);
            TabDropScale.ScaleY = 1 + 0.22 * v - 0.1 * stretch;
            TabDropScale.ScaleX = 1 + 0.06 * v;
            TabDrop.Opacity = 1 - 0.35 * v;
            TabDropShadow.BlurRadius = 8 + 10 * v;
            TabDropShadow.ShadowDepth = 1 + 3 * v;
            TabLens.Visibility = Visibility.Hidden;
            TabLensShade.Visibility = Visibility.Hidden;
            if (TabIconsHost.OpacityMask != null) TabIconsHost.OpacityMask = null;
            return;
        }

        double lift = Math.Max(0, _lift.Value);       // may overshoot a little: the glass "pops"
        double a = Math.Clamp(lift, 0, 1);
        TabDropScale.ScaleX = TabDropScale.ScaleY = 1;
        TabDrop.Opacity = 1 - a;
        TabDropShadow.BlurRadius = 8;
        TabDropShadow.ShadowDepth = 1;

        // The lens shares the pill's footprint, then swells past the bar's top and bottom.
        double speed = Math.Clamp(Math.Abs(_tabL.Velocity + _tabR.Velocity) / 2 / 1100, 0, 1);
        double sx = 1 + 0.16 * lift - 0.04 * stretch + 0.07 * speed, sy = 1 + 0.38 * lift - 0.12 * stretch - 0.1 * speed;
        Canvas.SetLeft(TabLens, l);
        TabLens.Width = width;
        TabLensScale.ScaleX = sx;
        TabLensScale.ScaleY = sy;
        TabLens.Opacity = a;
        var shown = a > 0.003 ? Visibility.Visible : Visibility.Hidden;
        TabLens.Visibility = shown;
        TabLensShade.Visibility = shown;
        Canvas.SetLeft(TabLensShade, l + width / 2 - 17);
        TabLensShadeScale.ScaleX = width * sx / 34;
        TabLensShadeScale.ScaleY = sy;
        TabLensShade.Opacity = 0.4 * a;

        // Magnify what is under the glass (a uniform 1.22x around the lens centre).
        const double mag = 1.22;
        double cx = l + width / 2, cy = 14;
        double vw = width * sx / mag, vh = 28 * sy / mag;
        TabLensBrush.Viewbox = new Rect(cx - vw / 2, cy - vh / 2, vw, vh);

        // Fade the real icons under the lens so the magnified copy reads as refraction, not a double image.
        double W = TabIconsHost.ActualWidth;
        if (a > 0.003 && W > 0)
        {
            double half = width * sx / 2 - 1.5;
            double f0 = Math.Clamp((cx - half) / W, 0, 1), f1 = Math.Clamp((cx + half) / W, 0, 1);
            double soft = 3 / W;
            byte mid = (byte)Math.Round(255 * (1 - a));
            // One brush, reused every frame: only its stops move.
            if (_lensMask == null)
            {
                _lensMask = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                for (int i = 0; i < 4; i++) _lensMask.GradientStops.Add(new GradientStop());
            }
            var st = _lensMask.GradientStops;
            var dim = Color.FromArgb(mid, 0, 0, 0);
            st[0].Color = Colors.Black; st[0].Offset = Math.Max(0, f0 - soft);
            st[1].Color = dim;          st[1].Offset = f0;
            st[2].Color = dim;          st[2].Offset = f1;
            st[3].Color = Colors.Black; st[3].Offset = Math.Min(1, f1 + soft);
            if (TabIconsHost.OpacityMask != _lensMask) TabIconsHost.OpacityMask = _lensMask;
        }
        else if (TabIconsHost.OpacityMask != null) TabIconsHost.OpacityMask = null;
    }

    private LinearGradientBrush? _lensMask;

    // Drag the glass across the bar, like iOS: it follows the finger and drops into the nearest tab.
    private void TabBar_Move(object sender, MouseEventArgs e)
    {
        if (_dragStartX == null || e.LeftButton != MouseButtonState.Pressed || !LensOn || _mode != Mode.Expanded) return;
        double x = e.GetPosition(TabIcons).X;
        if (!_dragging)
        {
            if (Math.Abs(x - _dragStartX.Value) < 5) return;
            _dragging = true;
            if (!TabBar.CaptureMouse()) { _dragging = false; _dragStartX = null; return; }
        }
        double max = VisibleTabs.Length * TabW;
        double l = Math.Clamp(x - TabW / 2, -4, max - TabW + 4);
        _tabL.Configure(700, 52);
        _tabR.Configure(700, 52);
        _tabL.Target = l;
        _tabR.Target = l + TabW;
        _lift.Target = 1;
        StartAnimating();
        e.Handled = true;
    }

    /// <summary>The drag ended some other way (the island closed, capture was taken): settle on the current tab.</summary>
    private void CancelTabDrag()
    {
        _dragStartX = null;
        if (!_dragging) return;
        _dragging = false;
        if (TabBar.IsMouseCaptured) TabBar.ReleaseMouseCapture();
        MoveTabDrop();
    }

    private void TabBar_Up(object sender, MouseButtonEventArgs e)
    {
        _dragStartX = null;
        if (!_dragging) { if (TabBar.IsMouseCaptured) TabBar.ReleaseMouseCapture(); return; }
        _dragging = false;
        double x = e.GetPosition(TabIcons).X;
        if (TabBar.IsMouseCaptured) TabBar.ReleaseMouseCapture();
        e.Handled = true;
        var visible = VisibleTabs;
        int slot = (int)Math.Clamp(Math.Floor(x / TabW), 0, visible.Length - 1);
        if (visible[slot].IsChecked == true) MoveTabDrop();
        else visible[slot].IsChecked = true;
    }

    private void Island_PreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode != Mode.Expanded || !_settings.ScrollSwitchesTabs) return;
        var owner = WheelOwner(e.OriginalSource as DependencyObject);
        var now = DateTime.Now;
        if (owner is ScrollViewer sv)
        {
            // A list scrolls first. Once it is at its end, a new scroll (after a short pause) changes the tab.
            bool canMove = e.Delta > 0 ? sv.VerticalOffset > 0.5 : sv.VerticalOffset < sv.ScrollableHeight - 0.5;
            if (canMove || (now - _listWheelAt).TotalMilliseconds < 450)
            {
                _listWheelAt = now;
                if (!canMove) e.Handled = true;
                return;
            }
        }
        else if (owner != null) return;
        e.Handled = true;
        WheelTabs(-e.Delta);
    }

    /// <summary>A slider or a scrolling list under the pointer keeps the wheel for itself.</summary>
    private object? WheelOwner(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is PillSlider p) return p.WheelEnabled ? p : null;
            if (d is ScrollViewer sv && sv.ScrollableHeight > 0.5) return sv;
            if (d is TextBox) return d;
            if (d == this) return null;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Down or right moves to the next tab, up or left to the previous one.</summary>
    private void WheelTabs(double delta)
    {
        var now = DateTime.Now;
        // Speed 1..10: how much wheel makes one tab, and how soon the next tab may follow.
        double speed = Math.Clamp(_settings.TabScrollSpeed, 1, 10);
        double need = 120 * (1.55 - 0.12 * speed);      // 1 → ~1.7 notches, 7 → ~0.7, 10 → ~0.4 (touchpads)
        double cool = 300 - 30 * speed;                  // 1 → 270 ms, 7 → 90 ms, 10 → 0 ms
        if ((now - _wheelLast).TotalMilliseconds > 400) _wheelAcc = 0;
        _wheelLast = now;
        if (Math.Sign(delta) != Math.Sign(_wheelAcc)) _wheelAcc = 0;
        _wheelAcc += delta;
        if (now < _wheelCool) { _wheelAcc = Math.Clamp(_wheelAcc, -need, need); return; }
        if (Math.Abs(_wheelAcc) < need) return;
        int dir = Math.Sign(_wheelAcc);
        _wheelAcc -= dir * need;
        _wheelAcc = Math.Clamp(_wheelAcc, -need, need);
        _wheelCool = now.AddMilliseconds(cool);

        var visible = VisibleTabs;
        int at = Array.FindIndex(visible, t => t.IsChecked == true);
        int next = at + dir;
        // Already at the first or last tab: nothing to move to, so nothing moves.
        if (at < 0 || next < 0 || next >= visible.Length) return;
        visible[next].IsChecked = true;
    }

    private void HorizontalWheel(int delta)
    {
        if (_mode != Mode.Expanded) return;
        if (Mouse.DirectlyOver is DependencyObject over && WheelOwner(over) is PillSlider p)
        {
            p.Nudge(delta / 120.0);
            return;
        }
        if (_settings.ScrollSwitchesTabs) WheelTabs(delta);
    }

    private void ApplyDefaultTab()
    {
        // Whatever is live on the island is what you came for.
        if (_settings.OpenOnActivity && MusicVisible && TabHome.Visibility == Visibility.Visible)
        {
            TabHome.IsChecked = true;
            return;
        }
        var pick = _settings.DefaultTab switch
        {
            "Home" => TabHome,
            "Clipboard" => TabClip,
            "Controls" => TabControls,
            "System" => TabSystem,
            _ => null,
        };
        if (pick != null && pick.Visibility == Visibility.Visible) pick.IsChecked = true;
    }

    // ───────────────────────────── cover art → source ─────────────────────────────

    private static void PressTo(ScaleTransform t, double to, bool spring)
    {
        IEasingFunction ease = spring ? new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut }
                                      : new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut };
        var ms = TimeSpan.FromMilliseconds(spring ? 420 : 140);
        t.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, ms) { EasingFunction = ease });
        t.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, ms) { EasingFunction = ease });
    }

    private bool _heroPressed;

    private void Hero_Down(object sender, MouseButtonEventArgs e)
    {
        if (_mode != Mode.Expanded || !HeroArt.IsMouseOver || !_settings.ArtOpensSource) return;
        _heroPressed = true;
        PressTo(HeroArtPress, 0.9, spring: false);
        e.Handled = true;
    }

    private void Hero_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_heroPressed) return;
        _heroPressed = false;
        PressTo(HeroArtPress, 1, spring: true);
        e.Handled = true;
        if (MusicVisible && HeroArt.IsMouseOver)
        {
            _ = SourceLauncher.OpenAsync(_media.SourceId, _media.Title);
            SetMode(Mode.Compact);
        }
    }

    private void Hero_Leave(object sender, MouseEventArgs e)
    {
        if (!_heroPressed) return;
        _heroPressed = false;
        PressTo(HeroArtPress, 1, spring: true);
    }

    // ───────────────────────────── hover ─────────────────────────────

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_mode != Mode.Expanded && !_settings.OpenOnClickOnly && !_moving && !PointerOnPet())
        {
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.HoverDelayMs));
            _hoverTimer.Start();
            PrepareGlass();
            WarmUp(Math.Max(0, _settings.HoverDelayMs) + 300, content: true);
        }
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e) => _hoverTimer.Stop();

    /// <summary>Real cursor position; WPF's IsMouseOver goes stale on a window that never activates.</summary>
    private bool PointerOverIsland()
    {
        if (!Native.GetCursorPos(out var p)) return Island.IsMouseOver;
        var local = PointFromScreen(new Point(p.X, p.Y));
        var area = _shapeRect;
        area.Inflate(6, 6);
        return area.Contains(local);
    }

    private void LeaveTick()
    {
        if (_mode != Mode.Expanded) { _leaveTimer.Stop(); return; }
        WarmUp(150); // open: keep the render loop awake so closing starts on time, whatever the close delay
        if (Mouse.Captured != null || PointerOverIsland())
        {
            _outsideSince = null;
            _pinnedUntil = default; // visited: now normal hover rules apply
            return;
        }
        if (DateTime.Now < _pinnedUntil) return;
        if (_outsideSince == null) { _outsideSince = DateTime.Now; WarmUp(Math.Max(0, _settings.CloseDelayMs) + 300); }
        if (DateTime.Now - _outsideSince.Value > TimeSpan.FromMilliseconds(Math.Max(0, _settings.CloseDelayMs)))
            SetMode(Mode.Compact);
    }

    private void Island_Click(object sender, MouseButtonEventArgs e)
    {
        if (_mode != Mode.Expanded)
        {
            _hoverTimer.Stop();
            SetMode(Mode.Expanded);
        }
    }

    // ───────────────────────────── peek ─────────────────────────────

    private void ShowPeek(string glyph, string title, string? sub = null, double? bar = null, string? value = null,
                          ImageSource? art = null, Color? swatch = null, double width = 340, int ms = 1600, Brush? iconBrush = null)
    {
        if (_mode == Mode.Expanded || _hiddenForFullscreen) return;

        PeekIcon.Text = glyph;
        PeekIcon.Foreground = iconBrush ?? (Brush)FindResource("Fg");
        PeekIcon.Visibility = art == null && swatch == null ? Visibility.Visible : Visibility.Collapsed;
        PeekArtBrush.ImageSource = art;
        PeekArt.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
        PeekSwatch.Background = swatch.HasValue ? new SolidColorBrush(swatch.Value) : null;
        PeekSwatch.Visibility = swatch.HasValue ? Visibility.Visible : Visibility.Collapsed;

        PeekTitle.Text = title;
        PeekSub.Text = sub ?? "";
        PeekSub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;

        PeekBarTrack.Visibility = bar.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (bar.HasValue)
        {
            PeekBarScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(Math.Clamp(bar.Value, 0, 1), TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        PeekValue.Text = value ?? "";
        PeekValue.Visibility = value != null && !bar.HasValue ? Visibility.Visible : Visibility.Collapsed;

        _peekWidth = width;
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _peekTimer.Start();
        if (_mode != Mode.Peek) SetMode(Mode.Peek);
        else UpdateTargets();
    }

    // ───────────────────────────── media ─────────────────────────────

    private void OnMediaChanged()
    {
        TrackPause();
        var fallback = ParseHex(_settings.AccentHex);
        var accent = MusicVisible && _media.Art != null && _settings.ArtAccent ? _media.ArtColor : fallback;
        Application.Current.Resources["Accent"] = new SolidColorBrush(accent);
        Scrub.Accent = accent;
        Viz.SetAccent(_settings.VizColor switch { "White" => Colors.White, "Accent" => fallback, _ => accent });
        UpdateBackdrop();

        RefreshMedia();
        if (_mode == Mode.Compact) UpdateTargets();
        UpdateHero();
    }

    /// <summary>Remember when the music paused, so the song can stay on the island for a while.</summary>
    private void TrackPause()
    {
        _lingerTimer.Stop();
        if (_media?.HasSession != true || _media.IsPlaying) { _pausedAt = null; return; }
        _pausedAt ??= DateTime.Now;
        var left = _pausedAt.Value + Linger - DateTime.Now;
        if (left > TimeSpan.Zero)
        {
            _lingerTimer.Interval = left + TimeSpan.FromMilliseconds(250);
            _lingerTimer.Start();
        }
    }

    private static Color ParseHex(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0x0A, 0x84, 0xFF); }
    }

    // ───────────────────────────── artwork backdrop ─────────────────────────────

    private ImageSource? _backdropSrc;

    /// <summary>The cover glows behind the island only while its song is on the island:
    /// playing, or paused within the linger time. Otherwise the island is plain black glass.</summary>
    private void UpdateBackdrop() => SetBackdropArt(MusicVisible ? _media!.Art : null);

    private void SetBackdropArt(ImageSource? art)
    {
        double target = art != null && _settings.ArtBackdrop ? 1 : 0;
        if (_bd.Target != target) { _bd.Target = target; StartAnimating(); }
        if (art == null || ReferenceEquals(art, _backdropSrc)) return;
        _backdropSrc = art;

        // Cross-dissolve: the new cover rises over the old one, which melts away after it.
        var incoming = BackdropImageA.Opacity < 0.5 ? BackdropImageA : BackdropImageB;
        var outgoing = ReferenceEquals(incoming, BackdropImageA) ? BackdropImageB : BackdropImageA;
        incoming.Source = art;
        Panel.SetZIndex(incoming, 1);
        Panel.SetZIndex(outgoing, 0);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { EasingFunction = ease });
        outgoing.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(500), EasingFunction = ease });
    }

    private void ApplyBackdropLook()
    {
        BackdropBlur.Radius = Math.Clamp(_settings.BackdropBlur, 0, 120);
        double dim = Math.Clamp(_settings.BackdropDim, 0, 95) / 100.0;
        var scrim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * dim * 0.8), 0, 0, 0), 0));
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * dim), 0, 0, 0), 0.5));
        scrim.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * Math.Min(1, dim * 1.2)), 0, 0, 0), 1));
        scrim.Freeze();
        BackdropScrim.Background = scrim;
    }

    /// <summary>A slow, barely-there drift so the backdrop feels alive, like Apple Music's lyrics view.</summary>
    private void UpdateDrift()
    {
        bool on = _mode == Mode.Expanded && _settings.BackdropMotion && _settings.ArtBackdrop && !_settings.ReduceMotion;
        if (on == _drifting) return;
        _drifting = on;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        if (!on)
        {
            // Glide back to rest from wherever the drift is, never snap.
            var settle = TimeSpan.FromMilliseconds(650);
            DoubleAnimation Home(double to) => new(to, settle) { EasingFunction = ease };
            BackdropScale.BeginAnimation(ScaleTransform.ScaleXProperty, Home(1.05));
            BackdropScale.BeginAnimation(ScaleTransform.ScaleYProperty, Home(1.05));
            BackdropTurn.BeginAnimation(RotateTransform.AngleProperty, Home(0));
            BackdropShift.BeginAnimation(TranslateTransform.XProperty, Home(0));
            return;
        }
        // Start from the resting pose (no From), so the drift eases out of stillness.
        // 20 fps is plenty for a drift this slow under this much blur, and it means the whole
        // window is redrawn a third as often: most of the expanded island's GPU time.
        DoubleAnimation Loop(double to, double seconds)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            Timeline.SetDesiredFrameRate(a, 20);
            return a;
        }
        var grow = Loop(1.22, 14);
        BackdropScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        BackdropScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        BackdropTurn.BeginAnimation(RotateTransform.AngleProperty, Loop(6, 19));
        BackdropShift.BeginAnimation(TranslateTransform.XProperty, Loop(24, 23));
    }

    private bool _drifting;

    private void OnTrackChanged()
    {
        if (!_settings.TrackPeek || !_media.IsPlaying) return;
        // The closed island already shows the song while it plays, so only peek when it would not.
        if (_mode == Mode.Compact && !OnCall) return;
        ShowPeek(GMusic, _media.Title, _media.Artist, art: _media.Art, width: 380, ms: (int)(Math.Clamp(_settings.TrackPeekSeconds, 0.5, 15) * 1000));
    }

    private void RefreshMedia()
    {
        // A song paused longer than the linger time is shown as nothing playing,
        // but Play still works: it resumes that song, which brings it back.
        bool has = MusicVisible;
        HeroTitle.Text = has && _media!.Title.Length > 0 ? _media.Title : "Nothing playing";
        HeroArtist.Text = has ? (_media!.Artist.Length > 0 ? _media.Artist : _media.Source)
                        : _media?.Starting == true ? $"Opening {_media.StartingName}…" : "Play something in any app";
        HeroArtBrush.ImageSource = has ? _media!.Art : null;
        HeroArtImage.Visibility = has && _media!.Art != null ? Visibility.Visible : Visibility.Collapsed;
        HeroPlaceholder.Visibility = has && _media!.Art != null ? Visibility.Collapsed : Visibility.Visible;
        PlayIcon.Data = (Geometry)FindResource(has && _media!.IsPlaying ? "GeoPause" : "GeoPlay");
        BtnPrev.IsEnabled = BtnNext.IsEnabled = has;
        // With nothing at all to resume, Play opens the music app chosen in Settings and resumes its last song.
        BtnPlay.IsEnabled = _media?.HasSession == true || (_media != null && MediaService.CanStart(_settings.PlayOpens, _settings.PlayTarget));
        Scrub.Visibility = has && _media!.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Hidden;
        Scrub.Playing = has && _media!.IsPlaying;
        Scrub.Cursor = has && _media!.CanSeek ? Cursors.Hand : Cursors.Arrow;
        Scrub.IsHitTestVisible = has && _media!.CanSeek;
        SourceText.Text = has ? _media!.Source : "";
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        if (_media == null || Scrub.IsScrubbing) return;
        var dur = _media.Duration;
        var pos = _media.Position;
        bool times = MusicVisible && dur > TimeSpan.Zero && _settings.ShowTimes;
        PosText.Text = times ? Fmt(pos) : "";
        DurText.Text = times ? "-" + Fmt(dur - pos) : "";
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_media == null) return;
        if (_media.HasSession) { _media.TogglePlay(); return; }
        if (_media.Starting) return;
        string service = _settings.PlayOpens, target = _settings.PlayTarget;
        var start = _media.ResumeAsync(service, target);
        RefreshMedia();
        bool ok = await start;
        RefreshMedia();
        if (!ok) ShowPeek(GWarn, $"Couldn't start {MediaService.ServiceName(service, target)}", width: 300);
    }
    private void Next_Click(object sender, RoutedEventArgs e) => _media.Next();
    private void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();

    /// <summary>Hooks up the playback bar and the volume and brightness sliders.</summary>
    private void WireSliders()
    {
        // The bar reads the position itself every frame, so it glides instead of ticking once a second.
        Scrub.Source = () =>
        {
            if (_media == null || !_media.HasSession || _media.Duration <= TimeSpan.Zero) return 0;
            return _media.Position.TotalSeconds / _media.Duration.TotalSeconds;
        };
        Scrub.Scrubbing += f =>
        {
            if (_media == null || _media.Duration <= TimeSpan.Zero || !_settings.ShowTimes) return;
            var dur = _media.Duration;
            var at = TimeSpan.FromSeconds(f * dur.TotalSeconds);
            PosText.Text = Fmt(at);
            DurText.Text = "-" + Fmt(dur - at);
        };
        Scrub.SeekRequested += f =>
        {
            if (_media != null && _media.Duration > TimeSpan.Zero)
                _media.Seek(TimeSpan.FromSeconds(f * _media.Duration.TotalSeconds));
        };
        Scrub.ScrubEnded += UpdateProgress;

        foreach (var v in new[] { VolumeSlider, MiniVolume })
        {
            v.ValueChanged += x =>
            {
                if (_audio == null) return;
                _audio.Volume = (float)x;
                if (x > 0.001 && _audio.IsOutputMuted) _audio.IsOutputMuted = false;
                ShowVolume((float)x, _audio.IsOutputMuted && x <= 0.001);
            };
            v.IconClicked += () =>
            {
                if (_audio == null) return;
                bool muted = _audio.ToggleOutputMute();
                ShowVolume(_audio.Volume, muted);
            };
        }
        BrightnessSlider.ValueChanged += x => BrightnessService.Set((int)Math.Round(x * 100));
    }

    private void ShowVolume(float volume, bool muted)
    {
        foreach (var v in new[] { VolumeSlider, MiniVolume })
        {
            v.Value = volume;
            v.Muted = muted;
            v.Glyph = VolumeGlyph(volume, muted);
        }
    }

    private async void LoadBrightness()
    {
        int b = await BrightnessService.GetAsync();
        BrightnessSlider.Value = b / 100.0;
    }

    // ───────────────────────────── visualizer ─────────────────────────────

    private void UpdateVisualizerState()
    {
        bool playing = MusicPlaying;
        bool on = _settings.Visualizer && _heroOn && playing && !_hiddenForFullscreen;
        Viz.Visibility = MusicVisible && _settings.Visualizer ? Visibility.Visible : Visibility.Hidden;

        if (on)
        {
            if (!_spectrum.IsRunning) _spectrum.Start();
            if (!_vizTimer.IsEnabled)
            {
                _lastViz = _clock.Elapsed.TotalSeconds;
                _vizTimer.Start();
            }
        }
        else if (_vizTimer.IsEnabled || _spectrum.IsRunning)
        {
            _vizTimer.Stop();
            _spectrum.Stop();
            Viz.SetLevels(_flat, _clock.Elapsed.TotalSeconds);
        }
    }

    private void VizTick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastViz, 0.001, 0.1);
        _lastViz = now;

        _spectrum.Update(dt);
        if (_spectrum.IsRunning && _spectrum.HasSignal)
        {
            float g = (float)Math.Clamp(_settings.VizSensitivity, 0.2, 3);
            var lv = _spectrum.Levels;
            for (int b = 0; b < _scaled.Length && b < lv.Length; b++) _scaled[b] = Math.Min(1f, lv[b] * g);
            Viz.SetLevels(_scaled, now);
            return;
        }

        // No loopback (exclusive-mode device, etc.): shape the meter peak into a believable spectrum.
        double peak = Math.Sqrt(Math.Clamp((_audio?.Peak ?? 0) * _settings.VizSensitivity, 0, 1));
        for (int b = 0; b < _synth.Length; b++)
        {
            double shape = 1 - b * 0.045;
            double wobble = 0.6 + 0.4 * Math.Sin(now * (4.1 + b * 1.3) + b * 0.8);
            float target = (float)Math.Clamp(peak * shape * wobble * 1.2, 0, 1);
            float k = (float)Math.Min(1, dt * (target > _synth[b] ? 24 : 7));
            _synth[b] += (target - _synth[b]) * k;
        }
        Viz.SetLevels(_synth, now);
    }

    // ───────────────────────────── audio & mic ─────────────────────────────

    private void OnVolumeChanged(float volume, bool muted)
    {
        if (_mode == Mode.Expanded)
        {
            ShowVolume(volume, muted);
            return;
        }
        if (!_settings.VolumePeek) return;
        ShowPeek(VolumeGlyph(volume, muted), muted ? "Muted" : "Volume", bar: muted ? 0 : volume, width: 320, ms: 1500);
    }

    private static string VolumeGlyph(float v, bool muted) =>
        muted || v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";

    private void Mic_Click(object sender, RoutedEventArgs e) => ToggleMic();

    private void ToggleMic()
    {
        if (!_audio.HasMicrophone)
        {
            ShowPeek(GWarn, "No microphone found", width: 300);
            return;
        }
        bool muted = _audio.ToggleMic();
        SyncMicUi();
        ShowPeek(muted ? GMicOff : GMic, muted ? "Microphone muted" : "Microphone on",
                 "In every app", width: 300, ms: 1400,
                 iconBrush: (Brush)FindResource(muted ? "Red" : "Green"));
    }

    private void SyncMicUi()
    {
        if (_audio == null) return;
        bool muted = _audio.IsMicMuted;
        HeaderMic.Content = muted ? GMicOff : GMic;
        HeaderMic.Foreground = (Brush)FindResource(muted ? "Red" : "Fg");
        TileMic.IsChecked = !muted;
        TileMicIcon.Text = muted ? GMicOff : GMic;
        SyncCallPill();
    }

    private void OnPrivacyChanged()
    {
        bool call = _privacy.MicInUse;
        CamDot.Visibility = _privacy.CameraInUse ? Visibility.Visible : Visibility.Collapsed;
        MicDot.Visibility = Visibility.Collapsed; // the call pill already says it
        SyncCallUi();
        OnMediaChanged();
        SecondTick();
        if (call && _settings.MicPeek)
            ShowPeek(GMic, "Microphone in use", _privacy.MicApp.Length > 0 ? _privacy.MicApp : null,
                     width: 300, ms: 1800, iconBrush: (Brush)FindResource("Green"));
        else if (_mode == Mode.Compact) UpdateTargets();
    }

    private void SyncCallUi()
    {
        bool call = OnCall, dc = DiscordCall;
        CallPill.Visibility = call ? Visibility.Visible : Visibility.Collapsed;
        HomeCall.Visibility = call ? Visibility.Visible : Visibility.Collapsed;
        DiscordBar.Visibility = dc ? Visibility.Visible : Visibility.Collapsed;
        // The Discord buttons need the room the battery reading takes.
        HeaderBattery.Visibility = _settings.ShowBattery && !dc ? Visibility.Visible : Visibility.Collapsed;
        if (!call) _callStart = default;
        SyncCallPill();
    }

    /// <summary>The compact pill's badge: Discord's own state in a Discord call, else the system microphone.</summary>
    private void SyncCallPill()
    {
        if (_audio == null) return;
        bool dc = DiscordCall;
        bool deaf = dc && _discord.Deafened;
        bool muted = dc ? _discord.Muted : _audio.IsMicMuted;
        CallPillIcon.Text = deaf ? GHeadphones : muted ? GMicOff : GMic;
        CallPillSlash.Visibility = deaf ? Visibility.Visible : Visibility.Collapsed;
        CallPillBadge.Background = (Brush)FindResource(dc && (muted || deaf) ? "Red" : "Green");
        CallPillTime.Foreground = CallPillBadge.Background;
        if (!dc) return;

        DcMute.Content = muted ? GMicOff : GMic;
        DcMute.Foreground = (Brush)FindResource(muted ? "Red" : "Fg");
        DcMute.ToolTip = muted ? "Unmute in Discord" : "Mute in Discord";
        DcDeafenIcon.Foreground = (Brush)FindResource(deaf ? "Red" : "Fg");
        DcDeafenSlash.Visibility = deaf ? Visibility.Visible : Visibility.Collapsed;
        DcDeafen.ToolTip = deaf ? "Undeafen in Discord" : "Deafen in Discord";
        string where = _discord.Channel.Length > 0
            ? _discord.Channel + (_discord.Server.Length > 0 ? " · " + _discord.Server : "")
            : "Discord voice";
        HomeCall.ToolTip = where;
    }

    private void OnDiscordChanged()
    {
        bool inCall = DiscordCall;
        if (inCall && _dcWasInCall && _settings.DiscordPeek)
        {
            // Deafen implies mute in Discord, so speak about deafen first.
            if (_discord.Deafened != _dcWasDeaf)
                ShowPeek(GHeadphones, _discord.Deafened ? "Deafened" : "Undeafened", "Discord", width: 280, ms: 1300,
                         iconBrush: (Brush)FindResource(_discord.Deafened ? "Red" : "Green"));
            else if (_discord.Muted != _dcWasMuted)
                ShowPeek(_discord.Muted ? GMicOff : GMic, _discord.Muted ? "Muted" : "Unmuted", "Discord", width: 280, ms: 1300,
                         iconBrush: (Brush)FindResource(_discord.Muted ? "Red" : "Green"));
        }
        _dcWasInCall = inCall;
        _dcWasMuted = _discord.Muted;
        _dcWasDeaf = _discord.Deafened;
        SyncCallUi();
        OnMediaChanged();
        SecondTick();
        if (_mode == Mode.Compact) UpdateTargets();
    }

    private void DcMute_Click(object sender, RoutedEventArgs e) => _discord.ToggleMute();
    private void DcDeafen_Click(object sender, RoutedEventArgs e) => _discord.ToggleDeafen();

    private void DcLeave_Click(object sender, RoutedEventArgs e)
    {
        _discord.Disconnect();
        ShowPeek(GHangUp, "Left the voice channel", _discord.Channel.Length > 0 ? _discord.Channel : "Discord",
                 width: 300, ms: 1400, iconBrush: (Brush)FindResource("Red"));
    }

    // ───────────────────────────── clipboard ─────────────────────────────

    private void OnCopied(string text)
    {
        if (!_settings.ClipboardPeek) return;
        string preview = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (preview.Length > 60) preview = preview[..60] + "…";
        ShowPeek(GCopy, "Copied", preview, width: 360, ms: (int)(Math.Clamp(_settings.ClipPeekSeconds, 0.5, 10) * 1000));
    }

    private void SyncClipEmpty() =>
        ClipEmpty.Visibility = _clipboard.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ClipItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipItem item)
        {
            _clipboard.Copy(item);
            SetMode(Mode.Compact);
            ShowPeek(GCheck, "Copied to clipboard", item.Preview.Length > 50 ? item.Preview[..50] + "…" : item.Preview,
                     width: 360, ms: 1200, iconBrush: (Brush)FindResource("Green"));
        }
    }

    private void ClipPin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipItem item) _clipboard.TogglePin(item);
        e.Handled = true;
    }

    private void ClipClear_Click(object sender, RoutedEventArgs e) => _clipboard.ClearUnpinned();

    // ───────────────────────────── controls ─────────────────────────────

    private async void Wifi_Click(object sender, RoutedEventArgs e)
    {
        var on = await RadioService.ToggleAsync(Windows.Devices.Radios.RadioKind.WiFi);
        TileWifi.IsChecked = on ?? false;
        if (on == null) OpenUri("ms-settings:network-wifi");
    }

    private async void Bluetooth_Click(object sender, RoutedEventArgs e)
    {
        var on = await RadioService.ToggleAsync(Windows.Devices.Radios.RadioKind.Bluetooth);
        TileBt.IsChecked = on ?? false;
        if (on == null) OpenUri("ms-settings:bluetooth");
    }

    /// <summary>Labels the two shortcut buttons on the Controls tab with what they're set to do.</summary>
    private void ApplyQuickButtons()
    {
        void Paint(Button b, TextBlock icon, TextBlock text, string action, string target)
        {
            var a = QuickAction.Find(action);
            var (label, glyph) = a.Name == QuickAction.Custom ? QuickAction.ForTarget(target) : (a.Label, a.Glyph);
            icon.Text = glyph;
            text.Text = label;
            b.ToolTip = a.Name == QuickAction.Custom ? (target.Trim().Length > 0 ? target : "Choose what it opens in Settings › Shortcuts")
                      : a.Name == "Colour" && _settings.HotkeyColor ? $"Pick a colour ({HotkeyText.Normal(_settings.KeyColor)})"
                      : a.Name;
        }
        Paint(Quick1, Quick1Icon, Quick1Text, _settings.Button1, _settings.Button1Target);
        Paint(Quick2, Quick2Icon, Quick2Text, _settings.Button2, _settings.Button2Target);
    }

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        bool first = sender == Quick1;
        string action = first ? _settings.Button1 : _settings.Button2;
        string target = first ? _settings.Button1Target : _settings.Button2Target;
        switch (QuickAction.Find(action).Name)
        {
            case "Snip": Snip_Click(sender, e); break;
            case "Colour": Picker_Click(sender, e); break;
            case "Clipboard": TabClip.IsChecked = true; break;
            case "Calculator": SetMode(Mode.Compact); OpenUri("calculator:"); break;
            case "Files": SetMode(Mode.Compact); OpenUri("explorer.exe"); break;
            case "Task Manager": SetMode(Mode.Compact); OpenUri("taskmgr.exe"); break;
            case "Settings": SetMode(Mode.Compact); OpenUri("ms-settings:"); break;
            case "Lock": SetMode(Mode.Compact); LockWorkStation(); break;
            default:
                if (target.Trim().Length == 0) { ShowPeek(GWarn, "Nothing chosen yet", "Pick an app or website in Settings › Shortcuts", width: 340); break; }
                SetMode(Mode.Compact);
                if (!Launcher.Open(target)) ShowPeek(GWarn, "Couldn't open " + QuickAction.ForTarget(target).Label, width: 300);
                break;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool LockWorkStation();

    private void Snip_Click(object sender, RoutedEventArgs e)
    {
        SetMode(Mode.Compact);
        Dispatcher.BeginInvoke(() => OpenUri("ms-screenclip:"), DispatcherPriority.Background);
    }

    private void NightLight_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton t) t.IsChecked = false; // it only opens settings
        OpenUri("ms-settings:nightlight");
    }

    private void Picker_Click(object sender, RoutedEventArgs e)
    {
        SetMode(Mode.Compact);
        ShowPeek(GColor, "Point at a colour", _settings.HotkeyColor ? $"Press {HotkeyText.Normal(_settings.KeyColor)} to pick it" : "Turn on its shortcut in Settings › Shortcuts", width: 340, ms: 2200);
    }

    private void PickColor()
    {
        Native.GetCursorPos(out var p);
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        uint px = Native.GetPixel(dc, p.X, p.Y);
        Native.ReleaseDC(IntPtr.Zero, dc);
        var c = Color.FromRgb((byte)(px & 0xFF), (byte)((px >> 8) & 0xFF), (byte)((px >> 16) & 0xFF));
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        try { Clipboard.SetDataObject(hex, true); } catch { }
        ShowPeek("", hex, $"rgb({c.R}, {c.G}, {c.B}) · copied", swatch: c, width: 300, ms: 2000);
    }

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }

    private async void RefreshRadios()
    {
        var wifi = await RadioService.FindAsync(Windows.Devices.Radios.RadioKind.WiFi);
        var bt = await RadioService.FindAsync(Windows.Devices.Radios.RadioKind.Bluetooth);
        TileWifi.IsChecked = wifi?.State == Windows.Devices.Radios.RadioState.On;
        TileBt.IsChecked = bt?.State == Windows.Devices.Radios.RadioState.On;
    }

    // ───────────────────────────── expanded refresh ─────────────────────────────

    private FrameworkElement PanelAt(int i) => i switch
    {
        1 => PanelClip, 2 => PanelControls, 3 => PanelSystem, 4 => PanelSettings, _ => PanelHome,
    };

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelHome == null || TabDrop == null) return; // during InitializeComponent
        int idx = CurrentTab, prev = _lastTab;
        _lastTab = idx;

        bool slide = _mode == Mode.Expanded && !_quietTabs && !_settings.ReduceMotion && prev != idx;
        if (slide) StartPage(PanelAt(prev), PanelAt(idx), Math.Sign(TabPos(idx) - TabPos(prev)));
        else { FinishPage(); ShowOnly(PanelAt(idx)); }

        if (_mode == Mode.Expanded)
        {
            if (_quietTabs) SnapTabDrop(); else MoveTabDrop();
            RefreshExpanded();
            UpdateHero();
        }
        else SnapTabDrop();
    }

    private void ShowOnly(FrameworkElement keep)
    {
        for (int i = 0; i < 5; i++)
        {
            var el = PanelAt(i);
            el.Visibility = el == keep ? Visibility.Visible : Visibility.Collapsed;
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
            el.RenderTransform = Transform.Identity;
            el.CacheMode = null;
        }
    }

    /// <summary>
    /// iOS page change: the new page glides in from the side you are heading to while the old one
    /// eases the other way and fades. One spring drives both, so they never drift apart, and a
    /// change mid-flight just carries on from where things are.
    /// </summary>
    private void StartPage(FrameworkElement from, FrameworkElement to, int dir)
    {
        FinishPage();
        for (int i = 0; i < 5; i++)
        {
            var el = PanelAt(i);
            el.BeginAnimation(OpacityProperty, null);
            el.Visibility = el == from || el == to ? Visibility.Visible : Visibility.Collapsed;
        }
        _pgOut = from == to ? null : from;
        _pgIn = to;
        _pgDir = dir == 0 ? 1 : dir;
        to.RenderTransform = new TranslateTransform();
        if (_pgOut != null) _pgOut.RenderTransform = new TranslateTransform();
        // While they slide and fade, each page is drawn once into a bitmap and that picture is
        // moved, instead of re-rendering every control (and its shadows) on every frame.
        to.CacheMode = PageCache();
        if (_pgOut != null) _pgOut.CacheMode = PageCache();
        _pgHero = from == PanelHome || to == PanelHome;
        if (_pgHero) HeroLayer.BeginAnimation(OpacityProperty, null);

        Ios(_pg, 0.5 / Math.Clamp(_settings.MotionSpeed, 0.4, 2.5), 0.9);
        _pg.Value = 0;
        _pg.Velocity = 0;
        _pg.Target = 1;
        LayoutPages();
        StartAnimating();
    }

    private BitmapCache? _pageCache;

    /// <summary>A shared page cache at the screen's real pixel density, so text stays sharp mid-slide.</summary>
    private BitmapCache PageCache()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_pageCache == null || _pageCache.RenderAtScale != scale)
        {
            _pageCache = new BitmapCache(scale) { SnapsToDevicePixels = true };
            _pageCache.Freeze();
        }
        return _pageCache;
    }

    private void LayoutPages()
    {
        if (_pgIn == null) return;
        double q = _pg.Value, qc = Math.Clamp(q, 0, 1);
        const double D = 64;

        static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
        double inX = _pgDir * D * (1 - q);
        double inA = Smooth((qc - 0.06) / 0.5);
        double outX = -_pgDir * D * 0.5 * qc;
        double outA = 1 - Smooth(qc / 0.4);

        ((TranslateTransform)_pgIn.RenderTransform).X = inX;
        _pgIn.Opacity = inA;
        if (_pgOut?.RenderTransform is TranslateTransform ot)
        {
            ot.X = outX;
            _pgOut.Opacity = outA;
        }
        if (_pgHero)
        {
            bool heroIn = _pgIn == PanelHome;
            HeroShift.X = heroIn ? inX : outX;
            HeroLayer.Opacity = heroIn ? inA : outA;
        }
        if (_pg.IsResting) FinishPage();
    }

    private void FinishPage()
    {
        if (_pgIn == null) return;
        if (_pgOut != null)
        {
            _pgOut.Visibility = Visibility.Collapsed;
            _pgOut.Opacity = 1;
            _pgOut.RenderTransform = Transform.Identity;
            _pgOut.CacheMode = null;
        }
        _pgIn.Visibility = Visibility.Visible;
        _pgIn.Opacity = 1;
        _pgIn.RenderTransform = Transform.Identity;
        _pgIn.CacheMode = null;
        if (_pgHero)
        {
            HeroShift.X = 0;
            HeroLayer.Opacity = _heroOn ? 1 : 0;
        }
        _pgIn = _pgOut = null;
        _pgHero = false;
        _pg.Value = _pg.Target = 1;
        _pg.Velocity = 0;
    }

    private void RefreshExpanded()
    {
        SecondTick();
        if (_audio != null) ShowVolume(_audio.Volume, _audio.IsOutputMuted);
        if (TabHome.IsChecked == true) RefreshMedia();
        if (TabControls.IsChecked == true)
        {
            LoadBrightness();
            SyncMicUi();
            RefreshRadios();
        }
        if (TabSystem.IsChecked == true) RefreshStats();
    }

    private void SecondTick()
    {
        var now = DateTime.Now;
        if (OnCall)
        {
            // A Discord call outlives the microphone (Discord can close it while muted),
            // so the call keeps the earliest start either one has seen.
            DateTime start = _callStart;
            if (_privacy?.MicInUse == true && _privacy.MicSince != default && (start == default || _privacy.MicSince < start)) start = _privacy.MicSince;
            if (DiscordCall && _discord.Since != default && (start == default || _discord.Since < start)) start = _discord.Since;
            if (start == default) start = now;
            _callStart = start;
            var span = now - start;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            string callTime = Fmt(span);
            CallPillTime.Text = callTime;
            HomeCallText.Text = DiscordCall ? callTime
                : (_privacy?.MicApp.Length > 0 ? _privacy.MicApp + " · " : "On a call · ") + callTime;
        }

        if (_mode == Mode.Compact && _widgets?.Any == true && Widgets.Refresh(now) && WidgetsShown) UpdateTargets();
        if (_mode != Mode.Expanded) return;

        ClockText.Text = now.ToString(_settings.Clock24h ? "HH:mm" : "h:mm");
        var snap = _stats.Sample();
        HeaderBattery.Text = snap.Battery.HasValue && _settings.ShowBattery ? $"{snap.Battery:0}%{(snap.Charging ? " ⚡" : "")}" : "";
        if (TabHome.IsChecked == true) UpdateProgress();
        if (TabSystem.IsChecked == true) ShowStats(snap);
    }

    private void RefreshStats() => ShowStats(_stats.Sample());

    private void ShowStats(SystemSnapshot s)
    {
        CpuText.Text = $"{s.Cpu:0}%";
        SetRing(CpuRing, s.Cpu / 100);
        CpuRing.Stroke = (Brush)FindResource(s.Cpu > 85 ? "Red" : s.Cpu > 60 ? "Orange" : "Green");
        UptimeText.Text = $"Up {(int)s.Uptime.TotalDays}d {s.Uptime.Hours}h {s.Uptime.Minutes}m";

        RamText.Text = $"{s.Ram:0}%";
        SetRing(RamRing, s.Ram / 100);
        RamDetail.Text = $"{s.RamUsedGb:0.0} of {s.RamTotalGb:0} GB";

        if (s.Battery.HasValue)
        {
            BatText.Text = $"{s.Battery:0}%";
            SetRing(BatRing, s.Battery.Value / 100);
            BatRing.Stroke = (Brush)FindResource(s.Battery < 20 && !s.Charging ? "Red" : "Green");
            BatDetail.Text = s.Charging ? "Plugged in" : "On battery";
        }
        else
        {
            BatText.Text = "AC";
            SetRing(BatRing, 1);
            BatDetail.Text = "No battery";
        }
    }

    private static void SetRing(Controls.Ring ring, double v) =>
        ring.BeginAnimation(Controls.Ring.ValueProperty,
            new DoubleAnimation(Math.Clamp(v, 0, 1), TimeSpan.FromMilliseconds(600)) { EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut } });

    // ───────────────────────────── fullscreen & topmost ─────────────────────────────

    private void WatchTick()
    {
        if (_escFocus != IntPtr.Zero) { UpdateEscWatch(); return; } // Crest has the focus for now: the game is still there
        bool fullscreen = false;
        if (_settings.HideInFullscreen && Native.SHQueryUserNotificationState(out int state) == 0)
            // Windows sometimes says "busy" for the desktop or the Settings app too: only hide when the
            // window in front really covers the whole screen (presentation mode is always trusted).
            fullscreen = state == Native.QUNS_PRESENTATION_MODE
                         || (state is Native.QUNS_BUSY or Native.QUNS_RUNNING_D3D_FULL_SCREEN
                             && (Native.GetForegroundWindow() == _hwnd ? _fullscreen : Native.ForegroundFillsScreen(_hwnd)));

        _fullscreen = fullscreen;
        if (fullscreen && !_hiddenForFullscreen && _mode != Mode.Expanded)
            HideForFullscreen();
        else if (!fullscreen && _hiddenForFullscreen)
            ShowFromFullscreen();
        else if (!fullscreen && _escShown)
            _escShown = false;              // fullscreen ended while Esc held the island open: just stay
        UpdateEscWatch();

        if (!_hiddenForFullscreen)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void HideForFullscreen()
    {
        _hiddenForFullscreen = true;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
        Island.IsHitTestVisible = false;
        UpdateVisualizerState();
        UpdatePet();
    }

    // ── Hold Esc in fullscreen ──
    // Esc is only read (GetAsyncKeyState), never intercepted, and only while the island is hidden
    // for fullscreen or held open by Esc. A short tap does nothing; holding it opens the island,
    // and letting go closes and hides it again.
    private const int EscHoldMs = 350;
    private DispatcherTimer? _escTimer;
    private DateTime? _escDownSince;
    private bool _escShown, _fullscreen;
    private int _escTicks;

    private void UpdateEscWatch()
    {
        bool watch = _settings.HoldEscInFullscreen && (_hiddenForFullscreen || _escShown);
        if (!watch) { _escTimer?.Stop(); _escDownSince = null; return; }
        if (_escTimer == null)
        {
            _escTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => EscTick(), Dispatcher);
            // Esc already down when fullscreen started (or the island just hid) has to be let go first.
            _escDownSince = EscDown() ? DateTime.MaxValue : null;
        }
        if (!_escTimer.IsEnabled)
        {
            _escDownSince = EscDown() ? DateTime.MaxValue : null;
            _escTimer.Start();
        }
    }

    private static bool EscDown() => (Native.GetAsyncKeyState(Native.VK_ESCAPE) & 0x8000) != 0;

    private void EscTick()
    {
        bool down = EscDown();
        if (_escShown)
        {
            if (down)
            {
                _pinnedUntil = DateTime.Now.AddSeconds(1);
                // Games put their own window back on top now and then: keep the island above it.
                if (++_escTicks % 8 == 0)
                    Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                return;
            }
            _escShown = false;
            _escDownSince = null;
            _pinnedUntil = default;
            ReturnFocus();
            if (_mode == Mode.Expanded) SetMode(Mode.Compact);
            if (_fullscreen && _settings.HideInFullscreen) HideForFullscreen();
            UpdateEscWatch();
            return;
        }
        if (!down) { _escDownSince = null; return; }
        if (_escDownSince == null)
        {
            _escDownSince = DateTime.Now;
            WarmUp(EscHoldMs + 300, content: true); // the render loop is running by the time it opens
            PrepareGlass();
            return;
        }
        if (!_hiddenForFullscreen || DateTime.Now - _escDownSince.Value < TimeSpan.FromMilliseconds(EscHoldMs)) return;
        _escShown = true;
        ShowFromFullscreen();
        SetMode(Mode.Expanded);
        _pinnedUntil = DateTime.Now.AddSeconds(1);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        TakeFocus();
    }

    // A game holds the mouse (hidden, locked to its window) for as long as it has the focus.
    // While Esc holds the island open, Crest takes the focus, so the game lets go and the pointer
    // is free; on release the focus goes straight back to the game, which takes the mouse again.
    private IntPtr _escFocus;

    private void TakeFocus()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
        // Windows only lets the app the user is using hand over the focus: join its input queue for a moment.
        uint other = Native.GetWindowThreadProcessId(fg, IntPtr.Zero), me = Native.GetCurrentThreadId();
        bool joined = other != 0 && other != me && Native.AttachThreadInput(me, other, true);
        bool ok = Native.SetForegroundWindow(_hwnd);
        if (joined) Native.AttachThreadInput(me, other, false);
        if (ok)
        {
            _escFocus = fg;
            Native.ClipCursor(IntPtr.Zero);
        }
        else Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    private void ReturnFocus()
    {
        var game = _escFocus;
        if (game == IntPtr.Zero) return;
        _escFocus = IntPtr.Zero;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE));
        if (!Native.IsWindow(game)) return;
        if (Native.IsIconic(game)) Native.ShowWindow(game, Native.SW_RESTORE); // exclusive fullscreen minimises on focus loss
        Native.SetForegroundWindow(game);
    }

    private void ShowFromFullscreen()
    {
        _hiddenForFullscreen = false;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        Island.IsHitTestVisible = true;
        UpdateVisualizerState();
        UpdatePet();
    }

    // ───────────────────────────── settings ─────────────────────────────

    private void LoadSettingsUi()
    {
        StyleNotch.IsChecked = IsNotch;
        StyleIsland.IsChecked = !IsNotch;
        ChkHideHover.IsChecked = _settings.HideUntilHover;
        ChkFullscreen.IsChecked = _settings.HideInFullscreen;
        ChkStartup.IsChecked = _settings.StartWithWindows;
        ChkVisualizer.IsChecked = _settings.Visualizer;
        ChkTrackPeek.IsChecked = _settings.TrackPeek;
        ChkClipPeek.IsChecked = _settings.ClipboardPeek;
        SegThumbX.X = IsNotch ? 0 : 62;
    }

    private void Shape_Checked(object sender, RoutedEventArgs e)
    {
        if (SegThumbX != null)
            SegThumbX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(StyleIsland.IsChecked == true ? 62 : 0, TimeSpan.FromMilliseconds(380))
            { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
        if (_media == null || _applying) return; // still loading, or being set from code
        _settings.Style = StyleIsland.IsChecked == true ? "Island" : "Notch";
        _settings.Commit();
    }

    private void Setting_Click(object sender, RoutedEventArgs e)
    {
        _settings.HideUntilHover = ChkHideHover.IsChecked == true;
        _settings.HideInFullscreen = ChkFullscreen.IsChecked == true;
        _settings.Visualizer = ChkVisualizer.IsChecked == true;
        _settings.TrackPeek = ChkTrackPeek.IsChecked == true;
        _settings.ClipboardPeek = ChkClipPeek.IsChecked == true;
        if (_settings.StartWithWindows != (ChkStartup.IsChecked == true))
        {
            _settings.StartWithWindows = ChkStartup.IsChecked == true;
            StartupService.Apply(_settings.StartWithWindows);
        }
        _settings.Commit();
    }

    /// <summary>Apply every setting live. Runs at start-up and whenever the settings change.</summary>
    private void ApplySettings()
    {
        if (_applying) return;
        _applying = true;
        try
        {
            LoadSettingsUi();
            ClipboardService.MaxUnpinned = Math.Clamp(_settings.ClipHistory, 5, 500);

            TabClip.Visibility = _settings.ShowClipTab ? Visibility.Visible : Visibility.Collapsed;
            TabControls.Visibility = _settings.ShowControlsTab ? Visibility.Visible : Visibility.Collapsed;
            TabSystem.Visibility = _settings.ShowSystemTab ? Visibility.Visible : Visibility.Collapsed;
            ApplyTabOrder();
            var current = TabById(CurrentTab);
            if (current.Visibility != Visibility.Visible) TabHome.IsChecked = true;
            SnapTabDrop();

            Scrub.Look = _settings.ScrubStyle;
            MiniVolume.Visibility = _settings.MiniVolume ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in new[] { VolumeSlider, MiniVolume, BrightnessSlider })
            {
                p.WheelEnabled = _settings.ScrollAdjustsSliders;
                p.WheelStep = Math.Clamp(_settings.SliderScrollStep, 0.5, 40);
            }

            ClockText.Visibility = _settings.ShowClock ? Visibility.Visible : Visibility.Collapsed;
            if (_settings.DiscordControls) _discord.Start(); else _discord.Stop();
            if (_audio != null) SyncCallUi();
            else HeaderBattery.Visibility = _settings.ShowBattery ? Visibility.Visible : Visibility.Collapsed;
            HeaderMic.Visibility = _settings.ShowMicButton ? Visibility.Visible : Visibility.Collapsed;
            IslandShape.StrokeThickness = _settings.RimLight ? 1 : 0;
            if (_mode == Mode.Expanded && _settings.Shadow && !_animating) ShowShadow();
            else if (!_settings.Shadow) HideShadow();
            HeroArt.Cursor = _settings.ArtOpensSource ? Cursors.Hand : Cursors.Arrow;
            Viz.SetGlow(_settings.VizGlow);
            ApplyBackdropLook();
            UpdateDrift();
            if (!GlassOn) ReleaseGlass();
            else if (_mode == Mode.Expanded && GlassImage.Source == null) StartGlass();
            UpdateGlassLook();

            if (_hwnd != IntPtr.Zero) RegisterHotkeys();
            UpdateHotkeyHint();
            ApplyQuickButtons();
            if (_media != null)
            {
                _media.Reconsider();
                OnMediaChanged();
            }
            Widgets.Rebuild(_settings);
            Widgets.Refresh(DateTime.Now);
            UpdateTargets();
            UpdateHero();
            UpdateVisualizerState();
            Materialize();
            StartAnimating();
        }
        finally
        {
            _applying = false;
        }
    }

    private void AllSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private void OpenSettingsWindow()
    {
        if (_mode == Mode.Expanded) SetMode(Mode.Compact);
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings);
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; PauseHotkeys(false); ScheduleTrim(2); };
            _settingsWindow.RecordingKeys += PauseHotkeys;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private DispatcherTimer? _updateTimer;
    private UpdateService.Release? _pendingUpdate;

    /// <summary>
    /// A minute after start, then every six hours: ask GitHub for a newer release. An installed copy
    /// updates itself the next time the island is idle; otherwise the new version is announced once.
    /// </summary>
    private void StartUpdateChecks()
    {
        UpdateService.CleanUp();
        string now = UpdateService.Current.ToString(3);
        if (_settings.LastVersion != now)
        {
            bool updated = _settings.LastVersion.Length > 0;
            _settings.LastVersion = now;
            _settings.Commit();
            if (updated)
            {
                var hello = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                hello.Tick += (_, _) => { hello.Stop(); ShowPeek(GCheck, $"Crest updated to {now}", width: 300, ms: 3000); };
                hello.Start();
            }
        }

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            if (_pendingUpdate != null) { InstallQuietly(); return; }
            if (!_settings.AutoUpdateCheck) return;
            try
            {
                var r = await UpdateService.CheckAsync();
                if (r == null) return;
                if (_settings.AutoInstallUpdates && UpdateService.IsInstalledCopy)
                {
                    _pendingUpdate = r;
                    InstallQuietly();
                    return;
                }
                string v = r.Version.ToString(3);
                if (_settings.AnnouncedUpdate == v) return;
                _settings.AnnouncedUpdate = v;
                _settings.Commit();
                ShowPeek(GCheck, $"Crest {v} is available", "Install it in Settings › About", width: 340, ms: 4000);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException or System.Text.Json.JsonException) { }
            catch (Exception ex) { App.Log(ex); }
        };
        _updateTimer.Start();
    }

    private bool _installingUpdate;

    /// <summary>
    /// Installs the waiting update, but only while you aren't using Crest: the island closed, no
    /// Settings window, no call. Otherwise it tries again in two minutes.
    /// </summary>
    private async void InstallQuietly()
    {
        var r = _pendingUpdate;
        if (r == null || _installingUpdate || _updateTimer == null) return;
        if (!_settings.AutoInstallUpdates) { _pendingUpdate = null; return; }
        bool busy = _mode == Mode.Expanded || _moving || _settingsWindow != null || _privacy?.MicInUse == true
                    || Mouse.LeftButton == MouseButtonState.Pressed;
        if (busy)
        {
            _updateTimer.Interval = TimeSpan.FromMinutes(2);
            return;
        }
        _installingUpdate = true;
        try
        {
            await UpdateService.InstallAsync(r, silent: true);
            // Setup closes this copy and starts the new one.
        }
        catch (Exception ex)
        {
            _installingUpdate = false;
            _pendingUpdate = null;
            if (ex is not (System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException)) App.Log(ex);
            // Couldn't do it quietly: say so once, and leave the choice to you.
            string v = r.Version.ToString(3);
            if (_settings.AnnouncedUpdate == v) return;
            _settings.AnnouncedUpdate = v;
            _settings.Commit();
            ShowPeek(GCheck, $"Crest {v} is available", "Install it in Settings › About", width: 340, ms: 4000);
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Quit();

    private void Quit()
    {
        try
        {
            Native.RemoveClipboardFormatListener(_hwnd);
            for (int id = 1; id <= 4; id++) Native.UnregisterHotKey(_hwnd, id);
            _settingsWindow?.Close();
            _spectrum.Dispose();
            _tray?.Dispose();
            _audio?.Dispose();
            _discord.Dispose();
            BrightnessService.Restore();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        Application.Current.Shutdown();
    }
}
