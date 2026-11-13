using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Crest.Controls;

/// <summary>
/// A small pixel pet (cat, dog or bunny) that sits in one corner of the idle island. It blinks and
/// flicks its tail, yawns and curls up asleep when you have been away a minute, and purrs out hearts
/// when you rest the pointer on it.
///
/// Each pose is drawn as soft vector shapes on a 34×24 grid, then every cell that is covered
/// enough becomes one solid pixel (one layer per colour), so it stays crisp pixel art at any size.
/// It animates at 12 frames a second, like hand-drawn animation "on twos".
/// </summary>
public sealed class PixelPet : Image
{
    private const int GW = 34, GH = 24;              // the pet's own grid
    private const int BW = 42, BH = 26, OX = 3, OY = 2; // the picture: room around it for hearts and z's
    private const double Fps = 12;
    private const double BlinkEvery = 4, BlinkFor = 0.18, SleepAfter = 60, YawnFor = 2.2;

    private static readonly uint Dark = 0xFF141414, Pink = 0xFFED93B1, Zzz = 0xFFB4B2A9, Cotton = 0xFFFFF8F0;

    /// <summary>One look: body, shadow, outline, markings (stripes, ears or patches), and an accent.</summary>
    private sealed record Palette(uint Fur, uint Shade, uint Line, uint Mark, uint Accent);

    public static readonly string[] Kinds = { "Cat", "Dog", "Bunny" };

    private static readonly Dictionary<string, (string Name, Palette P)[]> Palettes = new()
    {
        ["Cat"] = new[]
        {
            ("Cream",  new Palette(0xFFF4EDE0, 0xFFA99F8C, 0xFF8C8270, 0xFFE0A35C, Pink)),
            ("Ginger", new Palette(0xFFF2A65A, 0xFFB86E2E, 0xFF9A5A22, 0xFFC4652A, Pink)),
            ("Silver", new Palette(0xFFD9DDE2, 0xFF8F96A0, 0xFF7A818B, 0xFF7F8794, Pink)),
            ("Snow",   new Palette(0xFFFFFFFF, 0xFFB9B6B0, 0xFFA3A09A, 0xFFE6E1D8, Pink)),
        },
        ["Dog"] = new[]   // Mark = ears, Accent = muzzle and chest
        {
            ("Golden", new Palette(0xFFE8B86A, 0xFFB0823F, 0xFF94703A, 0xFFC98F45, 0xFFF6DDB0)),
            ("Beagle", new Palette(0xFFD6A062, 0xFF9C6E3C, 0xFF7E5A32, 0xFF6E4424, 0xFFFFF6EA)),
            ("Cocoa",  new Palette(0xFF94623F, 0xFF654128, 0xFF553622, 0xFF6E4630, 0xFFC09670)),
            ("Cream",  new Palette(0xFFF3E6CF, 0xFFB8A88E, 0xFF9C8E76, 0xFFE0C9A2, 0xFFFFFAF0)),
        },
        ["Bunny"] = new[] // Mark = patches, Accent = inside of the ear
        {
            ("White", new Palette(0xFFFAFAFA, 0xFFBDBDBD, 0xFFA6A6A6, 0xFFFAFAFA, Pink)),
            ("Brown", new Palette(0xFFB8875C, 0xFF7E5A3A, 0xFF6A4A30, 0xFFB8875C, 0xFFE8A9A0)),
            ("Grey",  new Palette(0xFFAEB2B8, 0xFF767C84, 0xFF646A72, 0xFFAEB2B8, 0xFFE3A7B4)),
            ("Dutch", new Palette(0xFFFAFAFA, 0xFFBDBDBD, 0xFFA6A6A6, 0xFF6B5448, Pink)),
        },
    };

    /// <summary>The looks a kind of pet comes in; the first one is the default.</summary>
    public static string[] Looks(string kind) =>
        Array.ConvertAll(Palettes.TryGetValue(kind, out var l) ? l : Palettes["Cat"], x => x.Name);

    private enum Eyes { Open, Shut, Happy }
    private enum State { Sit, Yawn, Sleep }

    /// <summary>Where it may sit, in DIPs of the parent: left edge, right edge, and the floor.</summary>
    public Func<(double Left, double Right, double Floor)>? Area { get; set; }
    /// <summary>True while the pointer is resting on the pet.</summary>
    public Func<bool>? Petted { get; set; }

    private readonly DispatcherTimer _timer;
    private readonly TranslateTransform _shift = new();
    private WriteableBitmap? _bmp;
    private uint[] _px = Array.Empty<uint>();
    private int _cell = 2;
    private double _dpi = 1;
    private string _kind = "Cat", _look = "Cream";
    private Palette _pal = Palettes["Cat"][0].P;
    private bool _right;

    private double _clock, _yawnLeft, _pettedUntil;
    private State _state = State.Sit;

    // ── changing pets ──
    // A new look: the pet turns right round where it sits, its spine bending into the turn (head first,
    // then chest, then haunch and tail), and the new colours wash down it from the ears while its back
    // is to you; then it settles back into its sit. A new pet: a ball bounces in, the old pet chases it
    // out of the island (cats and dogs gallop, bunnies hop), and the new one runs in from the other side.
    private enum Fx { None, Spin, Swap }
    private Fx _fx;
    private int _phase, _hop;
    private double _fxT, _fxX, _fxV, _gait, _hopX;
    private string _oldKind = "Cat", _oldLook = "Cream";
    private Palette _oldPal = Palettes["Cat"][0].P;
    private const double SpinFor = 2.4;
    private static readonly TimeSpan Calm = TimeSpan.FromSeconds(1 / Fps), Lively = TimeSpan.FromSeconds(1 / 24.0);

    public PixelPet()
    {
        Stretch = Stretch.None;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        RenderTransform = _shift;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1 / Fps) };
        _timer.Tick += (_, _) => Step();
    }

    /// <summary>Size of one pet pixel, in screen pixels.</summary>
    public int Cell
    {
        get => _cell;
        set { value = Math.Clamp(value, 1, 4); if (value == _cell) return; _cell = value; _bmp = null; }
    }

    /// <summary>Which pet and which look of it; unknown names fall back to the first.</summary>
    public void SetPet(string kind, string look)
    {
        if (!Palettes.TryGetValue(kind, out var looks)) { kind = "Cat"; looks = Palettes["Cat"]; }
        var pick = Array.Find(looks, x => x.Name == look);
        if (pick.Name == null) pick = looks[0];
        if (kind == _kind && pick.Name == _look) return;
        // Only play it out when you can see it; otherwise just switch.
        bool show = Running && Opacity > 0.5;
        if (_fx != Fx.None) EndFx();
        _oldKind = _kind; _oldLook = _look; _oldPal = _pal;
        _kind = kind; _look = pick.Name; _pal = pick.P;
        if (show)
        {
            _fx = kind != _oldKind ? Fx.Swap : Fx.Spin;
            _fxT = 0; _phase = 0; _gait = 0;
            _timer.Interval = Lively;
        }
        if (Running) Step();
    }

    private void EndFx()
    {
        _fx = Fx.None;
        _state = State.Sit;
        _timer.Interval = Calm;
    }

    /// <summary>Sit in the right corner (facing left) instead of the left one (facing right).</summary>
    public bool RightCorner
    {
        get => _right;
        set { if (value == _right) return; _right = value; if (Running) Step(); }
    }

    /// <summary>The pet's height in DIPs at the current size (the island grows to fit it).</summary>
    public double CatHeight(double dpi) => GH * _cell / dpi;
    public double CatWidth(double dpi) => GW * _cell / dpi;

    /// <summary>Is this screen point (physical pixels) on the pet itself, with a little slack?</summary>
    public bool IsOnCat(Point screen)
    {
        if (Opacity < 0.5 || PresentationSource.FromVisual(this) == null) return false;
        var p = PointFromScreen(screen);
        double c = _cell / _dpi, slack = 3;
        return p.X >= OX * c - slack && p.X <= (OX + GW) * c + slack &&
               p.Y >= OY * c - slack && p.Y <= (OY + GH) * c + slack;
    }

    public bool Running
    {
        get => _timer.IsEnabled;
        set
        {
            if (value == _timer.IsEnabled) return;
            if (value) { _timer.Start(); Step(); }
            else { _timer.Stop(); if (_fx != Fx.None) EndFx(); }
        }
    }

    // ───────────────────────────── behaviour ─────────────────────────────

    private void Step()
    {
        double dt = _timer.Interval.TotalSeconds;
        _clock += dt;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi != _dpi) { _dpi = dpi; _bmp = null; }
        EnsureBitmap();

        // It stays put in its corner, facing the middle of the island.
        var area = Area?.Invoke() ?? (0, 100, 30);
        double minX = Math.Ceiling(area.Left * dpi), maxX = Math.Floor(area.Right * dpi) - GW * _cell;
        if (maxX < minX) minX = maxX = Math.Round((area.Left + area.Right) / 2 * dpi - GW * _cell / 2.0);
        double x = _right ? maxX : minX;

        if (_fx != Fx.None)
        {
            _fxT += dt;
            // Fully past either end of the island (its outline clips the pet there).
            double outL = (area.Left - 18) * dpi - GW * _cell, outR = (area.Right + 18) * dpi;
            Present(StepFx(dt, x, outL, outR), area.Floor, dpi);
            return;
        }

        // You have been away a minute: it yawns, then curls up asleep. Any mouse or key wakes it.
        double away = IdleSeconds();
        if (away >= SleepAfter) { if (_state == State.Sit) { _state = State.Yawn; _yawnLeft = YawnFor; } }
        else _state = State.Sit;

        bool petted = Petted?.Invoke() == true;
        if (petted) _pettedUntil = _clock + 1.2;
        bool happy = _clock < _pettedUntil && _state == State.Sit;

        string pose = "sit"; double s = _clock; var eyes = (_clock % BlinkEvery) < BlinkFor ? Eyes.Shut : Eyes.Open;
        if (_state == State.Sleep) { pose = "sleep"; eyes = Eyes.Shut; }
        else if (_state == State.Yawn)
        {
            // Mouth opens wide and closes again, eyes squeezed shut; then it lies down.
            double t = YawnFor - _yawnLeft;
            pose = "yawn";
            s = t < 1.6 ? Math.Sin(Math.PI * t / 1.6) : 0;
            _yawnLeft -= dt;
            if (_yawnLeft <= 0) _state = State.Sleep;
        }
        else if (happy) { s = _clock * 2; eyes = Eyes.Happy; }

        bool flip = _right;
        Array.Clear(_px);
        Compose(Layers(pose, s, eyes), flip);
        if (pose == "sleep") DrawZs(flip);
        if (happy) DrawHearts(flip);
        Present(x, area.Floor, dpi);
    }

    private void Present(double x, double floor, double dpi)
    {
        _bmp!.WritePixels(new Int32Rect(0, 0, BW * _cell, BH * _cell), _px, BW * _cell * 4, 0);
        // Land on whole screen pixels so the art never blurs.
        double left = Math.Round(x) - OX * _cell;
        double top = Math.Round(floor * dpi) - BH * _cell;
        _shift.X = left / dpi;
        _shift.Y = top / dpi;
    }

    /// <summary>Draws one frame of a pet change and returns where the pet is (screen pixels).</summary>
    private double StepFx(double dt, double home, double outL, double outR)
    {
        int dir = _right ? -1 : 1;     // toward the middle of the island
        bool flip = _right;
        double x = home, t = _fxT;
        Array.Clear(_px);

        if (_fx == Fx.Spin)
        {
            if (t < SpinFor)
            {
                // It turns right round where it sits: the head swings round first, the spine bends
                // after it and the haunch follows, until it faces the same way again. As its back comes
                // round to you, the new look washes down it from the ears.
                double u = t / SpinFor;
                var (A, _, _) = TurnAt(_kind, u);
                int lift = _kind == "Bunny" ? (int)Math.Round(1.4 * Math.Abs(Math.Sin(3 * Math.PI * u))) : 0;
                FloorShadow(-A, flip, 0);
                double p = Math.Clamp((u - 0.3) / 0.4, 0, 1);
                double line = p * 27 - 1;
                var was = Layers(_kind, _oldLook, _oldPal, "bend", u, Eyes.Happy);
                var now = Layers(_kind, _look, _pal, "bend", u, Eyes.Happy);
                Compose(was, flip, 1, lift, (gx, gy) => gy >= line);
                Compose(now, flip, 1, lift, (gx, gy) => gy < line);
            }
            else
            {
                // Dizzy and pleased: a little hop down into a sit.
                const double D = 0.25;
                double v = Math.Min(1, (t - SpinFor) / D);
                Compose(Layers(_kind, _look, _pal, "sit", 0, Eyes.Happy), flip, lift: (int)Math.Round(2 * Math.Sin(Math.PI * v)));
                if (v >= 1) { EndFx(); _pettedUntil = _clock + 1.3; }
            }
            return x;
        }

        switch (_phase)
        {
            case 0:
            {
                // A ball bounces down in front of it: ears up... then two excited little hops.
                int lift = 0; var eyes = Eyes.Open; double s = 0;
                if (t > 0.5) { lift = (int)Math.Round(2 * Math.Abs(Math.Sin(Math.PI * (t - 0.5) / 0.25))); eyes = Eyes.Happy; s = _clock * 3; }
                Compose(Layers(_oldKind, _oldLook, _oldPal, "sit", s, eyes), flip, lift: lift);
                double tb = t - 0.1;
                if (tb > 0) Ball(19 - 20 * Math.Exp(-3.2 * tb) * Math.Abs(Math.Cos(Math.PI * tb / 0.38)), flip);
                if (t >= 1.0) { _phase = 1; _fxT = 0; _fxX = home; _fxV = 0; _gait = 0; _hop = 0; _hopX = home; }
                break;
            }
            case 1:
            {
                // Off it goes after the ball, all the way out of the island.
                if (_oldKind == "Bunny")
                {
                    _gait += dt / 0.36;
                    if (_gait >= 1) { _hopX += dir * (9 + 4 * _hop) * _cell; _hop++; _gait -= 1; }
                    double h = _gait;
                    _fxX = _hopX + dir * (9 + 4 * _hop) * _cell * h;
                    Compose(Layers(_oldKind, _oldLook, _oldPal, "hop", Math.Sin(Math.PI * h), Eyes.Happy), flip,
                        lift: (int)Math.Round(5 * Math.Sin(Math.PI * h)));
                }
                else
                {
                    _fxV = Math.Min(70, _fxV + 150 * dt);          // grid cells a second
                    _fxX += dir * _fxV * _cell * dt;
                    _gait += dt / 0.32;
                    double c = Math.Cos(2 * Math.PI * _gait);
                    Compose(Layers(_oldKind, _oldLook, _oldPal, "run", _gait % 1, Eyes.Happy), flip, lift: c > 0.4 ? 1 : 0);
                }
                Ball(19 - Math.Round(2 * Math.Abs(Math.Sin(2 * Math.PI * t * 1.6))), flip);
                x = _fxX;
                if (dir > 0 ? x > outR : x < outL) { _phase = 2; _fxT = 0; }
                break;
            }
            case 2:
                // An empty corner for a beat.
                if (t >= 0.15) { _phase = 3; _fxT = 0; _gait = 0; }
                break;
            case 3:
            {
                // The new pet comes in from the other side and slows into its corner.
                double start = dir > 0 ? outL : outR, span = home - start;
                if (_kind == "Bunny")
                {
                    const double H = 0.38;
                    int n = t < H ? 0 : 1;
                    double h = Math.Min(1, (t - n * H) / H);
                    double from = n == 0 ? 0 : 0.62, to = n == 0 ? 0.62 : 1;
                    x = start + span * (from + (to - from) * h);
                    Compose(Layers(_kind, _look, _pal, "hop", Math.Sin(Math.PI * h), Eyes.Happy), flip,
                        lift: (int)Math.Round((n == 0 ? 5 : 3) * Math.Sin(Math.PI * h)));
                    if (t >= 2 * H) { _phase = 4; _fxT = 0; }
                }
                else
                {
                    const double D = 0.8;
                    double u = Math.Min(1, t / D), e = 1 - (1 - u) * (1 - u);
                    x = start + span * e;
                    _gait += dt / 0.32 * (1.1 - 0.6 * u);
                    double c = Math.Cos(2 * Math.PI * _gait);
                    Compose(Layers(_kind, _look, _pal, "run", _gait % 1, Eyes.Happy), flip, lift: c > 0.4 ? 1 : 0);
                    if (u >= 1) { _phase = 4; _fxT = 0; }
                }
                break;
            }
            default:
            {
                // A little hop down into a sit, pleased with itself.
                const double D = 0.25;
                int lift = (int)Math.Round(2 * Math.Sin(Math.PI * Math.Min(1, t / D)));
                Compose(Layers(_kind, _look, _pal, "sit", 0, Eyes.Happy), flip, lift: lift);
                if (t >= D) { EndFx(); _pettedUntil = _clock + 1.3; }
                break;
            }
        }
        return x;
    }

    private void EnsureBitmap()
    {
        if (_bmp != null) return;
        _bmp = new WriteableBitmap(BW * _cell, BH * _cell, 96 * _dpi, 96 * _dpi, PixelFormats.Bgra32, null);
        _px = new uint[BW * _cell * BH * _cell];
        Source = _bmp;
    }

    // ───────────────────────────── drawing the frame ─────────────────────────────

    /// <summary>
    /// Paints a pose. k squeezes it sideways around its middle (negative mirrors it), for the twirl;
    /// lift raises it off the floor; take picks which columns to paint; faceless leaves out the
    /// eyes, nose and mouth (its back is turned); tint paints every cell one colour.
    /// </summary>
    private void Compose(List<Layer> layers, bool flip, double k = 1, int lift = 0,
                         Func<int, int, bool>? take = null, bool faceless = false, uint? tint = null)
    {
        int stride = BW * _cell, h = BH * _cell;
        foreach (var l in layers)
        {
            if (faceless && (l.Color == Dark || l.Color == Pink)) continue;
            for (int gx = 0; gx < GW; gx++)
            {
                double col = flip ? GW - 1 - gx : gx;
                if (k != 1) col = Math.Floor(GW / 2.0 + (col + 0.5 - GW / 2.0) * k);
                int cx = OX + (int)col;
                for (int gy = 0; gy < GH; gy++)
                    if (l.Mask[gy * GW + gx] && (take == null || take(gx, gy)))
                        FillCell(cx * _cell, (OY + gy - lift) * _cell, tint ?? l.Color, stride, h);
            }
        }
    }

    private void FillCell(int x, int y, uint c, int stride, int h)
    {
        for (int j = 0; j < _cell; j++)
        {
            int yy = y + j;
            if (yy < 0 || yy >= h) continue;
            int row = yy * stride;
            for (int i = 0; i < _cell; i++)
            {
                int xx = x + i;
                if (xx >= 0 && xx < stride) _px[row + xx] = c;
            }
        }
    }

    /// <summary>Pixel sprite in pet-grid coordinates (mirrored with the pet).</summary>
    private void Sprite(string[] rows, double gx, double gy, uint c, bool flip)
    {
        int stride = BW * _cell, h = BH * _cell;
        int y0 = (int)Math.Round(gy);
        for (int j = 0; j < rows.Length; j++)
            for (int i = 0; i < rows[j].Length; i++)
            {
                if (rows[j][i] == '.') continue;
                // The sprite moves to the mirrored spot but keeps reading the right way round.
                int cx = OX + (int)Math.Round(flip ? GW - gx - rows[j].Length + i : gx + i);
                FillCell(cx * _cell, (OY + y0 + j) * _cell, c, stride, h);
            }
    }

    private static readonly string[] Heart = { "H.H", "HHH", ".H." };
    private static readonly string[] ZSmall = { "XXX", "..X", ".X.", "XXX" };
    private static readonly string[] ZBig = { "XXXX", "...X", "..X.", ".X..", "XXXX" };

    private void DrawHearts(bool flip)
    {
        double s = _clock * 2;
        double a = (s * 6) % 8.7, b = (s * 6 + 4.3) % 8.7;
        Sprite(Heart, 30, 8.7 - a - 2, Pink, flip);
        Sprite(Heart, -2, 11 - b - 2, Pink, flip);
    }

    private void DrawZs(bool flip)
    {
        double z = (_clock % 3) / 3;
        if (z > 0.2) Sprite(ZSmall, 28, 4, Zzz, flip);
        if (z > 0.45) Sprite(ZSmall, 31, 1, Zzz, flip);
        if (z > 0.7) Sprite(ZBig, 34, -2, Zzz, flip);
    }

    private static readonly string[] BallRows = { ".XX.", "XXXX", "XXXX", ".XX." };
    private static readonly string[] Twinkle = { ".X.", "XXX", ".X." };
    private const uint BallRed = 0xFFE5604F, BallShine = 0xFFFFD6CC, Spark = 0xFFFFE9A8;

    /// <summary>The ball, just past the pet's nose; gy is its top row.</summary>
    private void Ball(double gy, bool flip)
    {
        Sprite(BallRows, 33, gy, BallRed, flip);
        Sprite(new[] { "X" }, flip ? 35 : 34, gy + 1, BallShine, flip);
    }

    private const uint Ground = 0xFF2A2A2A;

    /// <summary>A soft shadow on the floor under the walking pet, long when side-on, short end-on; up raises it with the pet when it is further off.</summary>
    private void FloorShadow(double a, bool flip, int up)
    {
        int stride = BW * _cell, h = BH * _cell;
        double rx = Math.Sqrt(Math.Pow(12.5 * Math.Cos(a), 2) + Math.Pow(5.5 * Math.Sin(a), 2)), cxg = 16;
        for (int gx = 0; gx < GW; gx++)
            for (int gy = 19; gy < GH; gy++)
            {
                double dx = (gx + 0.5 - cxg) / rx, dy = (gy + 0.5 - (23 - up)) / 1.3;
                if (dx * dx + dy * dy > 1) continue;
                int col = flip ? GW - 1 - gx : gx;
                FillCell((OX + col) * _cell, (OY + gy) * _cell, Ground, stride, h);
            }
    }

    // ───────────────────────────── the poses ─────────────────────────────

    private sealed record Layer(uint Color, bool[] Mask);
    private readonly Dictionary<string, List<Layer>> _cache = new();
    private delegate void AddLayer(uint c, int th, Action<DrawingContext> f);

    private List<Layer> Layers(string pose, double s, Eyes eyes) => Layers(_kind, _look, _pal, pose, s, eyes);

    private List<Layer> Layers(string kind, string look, Palette pal, string pose, double s, Eyes eyes, double g = 0)
    {
        // Quantize to the few distinct frames each pose really has, so they are drawn once and kept.
        string key;
        if (pose == "sit") { double fl = Math.Round(Math.Max(0, Math.Sin(s * 2.6)) * 6 * 4) / 4; s = fl; key = $"s{fl:F2}{eyes}"; }
        else if (pose == "yawn") { s = Math.Round(Math.Clamp(s, 0, 1) * 4) / 4; key = $"y{s:F2}"; }
        else if (pose == "run") { s = Math.Round((s % 1 + 1) % 1 * 8) % 8 / 8; key = $"r{s:F3}{eyes}"; }
        else if (pose == "hop") { s = Math.Round(Math.Clamp(s, 0, 1) * 6) / 6; key = $"h{s:F3}{eyes}"; }
        else if (pose == "bend") { s = Math.Round(Math.Clamp(s, 0, 1) * 48) / 48; key = $"b{s:F3}{eyes}"; }
        else if (pose == "walk")
        {
            s = Math.Round((s % 1 + 1) % 1 * 24) % 24 / 24;
            g = kind == "Bunny" ? 0 : Math.Round((g % 1 + 1) % 1 * 6) % 6 / 6;
            key = $"w{s:F3}.{g:F3}{eyes}";
        }
        else { double br = Math.Round((Math.Sin(s * 1.7) * 0.5 + 0.5) * 20) / 20; s = br; key = $"z{br:F2}"; }
        key = $"{kind}.{look}.{key}";
        if (_cache.TryGetValue(key, out var hit)) return hit;

        var list = new List<Layer>();
        void L(uint c, int th, Action<DrawingContext> f) => list.Add(new Layer(c, Raster(f, th)));
        if (pose == "bend") BendLayers(kind, L, pal, s, eyes);
        else if (pose == "walk") WalkLayers(kind, L, pal, s * 2 * Math.PI, g, eyes);
        else if (kind == "Dog") DogLayers(L, pal, pose, s, eyes);
        else if (kind == "Bunny") BunnyLayers(L, pal, pose, s, eyes);
        else CatLayers(L, pal, pose, s, eyes);
        _cache[key] = list;
        return list;
    }

    // s means: sit = tail flick 0..6, yawn = mouth 0..1, sleep = breath 0..1.
    private static void CatLayers(AddLayer L, Palette p, string pose, double s, Eyes eyes)
    {
        if (pose is "sit" or "yawn")
        {
            double fl = pose == "sit" ? s : 0;
            int mouth = pose == "yawn" ? (int)Math.Round(s * 2) : 0;
            var e = pose == "yawn" ? Eyes.Shut : eyes;
            L(p.Shade, 105, d => Ln(d, 2.6, 22.6, 15, 22.6, 22.4));
            L(p.Fur, 105, d => { Ell(d, 13, 18, 7, 5.5); Ell(d, 17, 14, 6, 7.5); Ln(d, 3, 20.4, 15, 20.4, 22.4); HeadFur(d, 19, 7); Quad(d, 7, 21.6, 3, 23.5, 1.8, 21.5 - fl, 2.8); });
            L(p.Mark, 95, d => { Ln(d, 1.4, 12, 11.6, 14, 12.4); Ln(d, 1.4, 11.4, 14, 13.4, 14.8); Ln(d, 1.4, 11.6, 16.6, 13.4, 17.2); });
            L(p.Line, 80, d => { Arc(d, 19, 7, 5.7, 0.62, 0.92); Arc(d, 13, 18, 6, 1.2, 1.65); });
            L(Dark, 105, d => { Face(d, 19, 7, e); if (mouth > 0) Rect(d, 19, 9, 3, mouth); });
            L(p.Accent, 105, d => { HeadPink(d, 19, 7); if (mouth > 1) Rect(d, 20, 10, 1, 1); });
        }
        else if (pose == "run")
        {
            // A happy gallop, tail held high: legs stretch out, then gather under it.
            double c = Math.Cos(2 * Math.PI * s), cf = Math.Cos(2 * Math.PI * (s - 0.12));
            double sw = Math.Sin(2 * Math.PI * s) * 1.2;
            L(p.Shade, 105, d => { Gallop(d, 22.5, 16, 3.5 * cf); Gallop(d, 10.5, 16, -3.5 * cf); });
            L(p.Fur, 105, d =>
            {
                Ell(d, 16, 14.5, 9, 4.6); HeadFur(d, 26, 9.5);
                Quad(d, 7.5, 12.5, 2.5, 11, 3.8 + sw, 4, 2.6);
                Gallop(d, 24, 16, 3.5 * c); Gallop(d, 9, 16, -3.5 * c);
            });
            L(p.Mark, 95, d => { Ln(d, 1.4, 12, 10.4, 12.4, 12.6); Ln(d, 1.4, 15, 10.1, 15.4, 12.3); Ln(d, 1.4, 18, 10.3, 18.4, 12.5); });
            L(p.Line, 80, d => Arc(d, 26, 9.5, 5.7, 0.62, 0.92));
            L(Dark, 105, d => Face(d, 26, 9.5, eyes));
            L(p.Accent, 105, d => HeadPink(d, 26, 9.5));
        }
        else
        {
            double br = s;
            L(p.Fur, 105, d => { Ell(d, 15, 17.5 - br * 0.4, 11, 5.4 + br * 0.8); HeadFur(d, 25, 18); Quad(d, 4.5, 20.5, 12, 24.6, 21, 22.3, 2.6); });
            L(p.Mark, 95, d => { Ln(d, 1.4, 8.6, 14.4, 9.2, 16.8); Ln(d, 1.4, 12.2, 14, 12.8, 16.6); Ln(d, 1.4, 15.8, 14, 16.4, 16.6); });
            L(p.Line, 80, d => { Arc(d, 25, 18, 5.8, 0.55, 1.35); Quad(d, 5, 18.5, 12, 22.7, 20.4, 20.3, 1.1); });
            L(Dark, 105, d => Face(d, 25, 18, Eyes.Shut));
            L(p.Accent, 105, d => HeadPink(d, 25, 18));
        }
    }

    private static void DogLayers(AddLayer L, Palette p, string pose, double s, Eyes eyes)
    {
        if (pose is "sit" or "yawn")
        {
            // A sitting pup: round face to the front, floppy ears, tail wagging side to side.
            double wag = pose == "sit" ? s / 6 : 0.3;
            int mouth = pose == "yawn" ? (int)Math.Round(s * 2) : 0;
            var e = pose == "yawn" ? Eyes.Shut : eyes;
            L(p.Shade, 105, d => Ln(d, 2.6, 22.6, 15, 22.6, 22.4));
            L(p.Fur, 105, d => { Quad(d, 7.5, 18.5, 3, 17.5, 1.2 + wag * 4.5, 12.6, 2.6); Ell(d, 13, 18, 7, 5.5); Ell(d, 17, 14, 6, 7.5); Ln(d, 3, 20.4, 15, 20.4, 22.4); Ell(d, 19, 7.5, 5.3, 4.6); });
            L(p.Accent, 95, d => { Ell(d, 19.5, 10.2, 2.7, 1.9); Ell(d, 19.6, 15.5, 2, 3); });
            L(p.Mark, 105, d => { Ell(d, 13.9, 8.4, 1.7, 3.4); Ell(d, 24.1, 8.4, 1.7, 3.4); });
            L(p.Line, 80, d => Arc(d, 13, 18, 6, 1.2, 1.65));
            L(Dark, 105, d => { Face(d, 18, 7, e); Rect(d, 19, 9, 2, 1); if (mouth > 0) Rect(d, 19, 10, 2, mouth); });
            if (e == Eyes.Happy) L(Pink, 105, d => Rect(d, 20, 11, 1, 2));   // tongue out while petted
            else if (mouth > 1) L(Pink, 105, d => Rect(d, 19, 11, 2, 1));
        }
        else if (pose == "run")
        {
            // Bounding along with its ears flapping, tongue out, tail up and wagging.
            double c = Math.Cos(2 * Math.PI * s), cf = Math.Cos(2 * Math.PI * (s - 0.12));
            double wag = Math.Sin(2 * Math.PI * s * 2) * 1.5;
            L(p.Shade, 105, d => { Gallop(d, 22, 16, 3.5 * cf); Gallop(d, 10.5, 16, -3.5 * cf); });
            L(p.Fur, 105, d =>
            {
                Ell(d, 15.5, 14.5, 9, 4.6); Ell(d, 25.5, 10, 5, 4.4);
                Quad(d, 7.5, 12.5, 3, 10, 3 + wag, 4.5, 2.4);
                Gallop(d, 23.5, 16, 3.5 * c); Gallop(d, 9, 16, -3.5 * c);
            });
            L(p.Accent, 95, d => { Ell(d, 29.6, 11.8, 2.4, 1.7); Ell(d, 21.5, 16, 2, 2.2); });
            L(p.Mark, 105, d => Ln(d, 3, 23.4, 7.4, 20.6, 10.4 - 2.2 * c));
            L(Dark, 105, d =>
            {
                if (eyes == Eyes.Happy) { Rect(d, 25, 9, 1, 1); Rect(d, 26, 8, 1, 1); Rect(d, 27, 9, 1, 1); }
                else Rect(d, 26, 8, 1, 2);
                Rect(d, 31, 10, 1, 1);
            });
            L(Pink, 105, d => Rect(d, 29, 13, 1, 2));
        }
        else
        {
            // Lying flat with its chin on its paws.
            double br = s;
            L(p.Fur, 105, d => { Ell(d, 14, 18.6 - br * 0.4, 10.5, 4.6 + br * 0.8); Ell(d, 25, 18.6, 5, 4.2); Quad(d, 4.5, 20.5, 1.5, 22.8, 5.5, 23.4, 2.6); Ell(d, 26, 22.6, 3.6, 1.2); });
            L(p.Accent, 95, d => Ell(d, 28.3, 20, 2.4, 1.7));
            L(p.Mark, 105, d => Ell(d, 21.4, 18.8, 1.8, 3.2));
            L(p.Line, 80, d => Quad(d, 5, 18.5, 12, 22.7, 20.4, 20.3, 1.1));
            L(Dark, 105, d => { Face(d, 25, 17, Eyes.Shut); Rect(d, 29, 19, 2, 1); });
        }
    }

    private static void BunnyLayers(AddLayer L, Palette p, string pose, double s, Eyes eyes)
    {
        if (pose is "sit" or "yawn")
        {
            // A side-on bunny with tall ears; the far ear twitches back now and then.
            double tw = pose == "sit" ? s / 6 : 0;
            int mouth = pose == "yawn" ? (int)Math.Round(s * 2) : 0;
            var e = pose == "yawn" ? Eyes.Shut : eyes;
            double ex = 18.4 - 2.2 * tw, ey = 1.3 + 0.8 * tw;
            void Ears(DrawingContext d) { Ln(d, 2.8, 19.6, 8, ex, ey); Ln(d, 2.8, 22.2, 8, 22.8, 1.3); }
            L(p.Shade, 105, d => Ell(d, 23.4, 22.6, 1.6, 1.1));
            L(p.Fur, 105, d => { Ell(d, 14, 17.5, 8, 6); Ell(d, 21, 11.4, 4.6, 4.2); Ears(d); Ell(d, 21, 22.6, 1.8, 1.1); Ell(d, 12.5, 22.8, 4, 1.2); });
            L(p.Mark, 105, d => { Ears(d); Ell(d, 10.5, 17.5, 4.6, 5.6); Ell(d, 21.4, 10.2, 2.6, 2.4); });
            L(p.Accent, 95, d => Ln(d, 1.3, 22.2, 6.8, 22.6, 2.6));
            L(Cotton, 105, d => Ell(d, 6.2, 17.8, 2.4, 2.2));
            L(p.Line, 80, d => Arc(d, 14, 17.5, 6, 1.15, 1.6));
            L(Dark, 105, d =>
            {
                if (e == Eyes.Open) Rect(d, 22, 10, 1, 2);
                else if (e == Eyes.Happy) { Rect(d, 21, 11, 1, 1); Rect(d, 22, 10, 1, 1); Rect(d, 23, 11, 1, 1); }
                else Rect(d, 21, 11, 2, 1);
                if (mouth > 0) Rect(d, 24, 13, 1, mouth);
            });
            L(Pink, 105, d => Rect(d, 25, 12, 1, 1));
        }
        else if (pose == "hop")
        {
            // s: 0 crouched on the ground, 1 stretched out mid-air, ears streaming back.
            double b = s;
            double bx = Lerp(14, 15, b), by = Lerp(18.5, 15, b), brx = Lerp(7.5, 10, b), bry = Lerp(5, 3.8, b);
            double hx = Lerp(21.5, 25.5, b), hy = Lerp(13, 11.5, b);
            void Ears(DrawingContext d)
            {
                Ln(d, 2.6, hx - 1, hy - 3, hx - 1 - Lerp(4, 6, b), hy - 3 - Lerp(5, 6.5, b));
                Ln(d, 2.6, hx + 0.6, hy - 3.2, hx + 0.6 - Lerp(3, 4.5, b), hy - 3.2 - Lerp(6, 7, b));
            }
            L(p.Fur, 105, d =>
            {
                Ell(d, bx, by, brx, bry); Ell(d, hx, hy, 4.3, 4); Ears(d);
                Ln(d, 2.8, Lerp(11, 9, b), Lerp(21, 17.5, b), Lerp(13, 2.5, b), Lerp(22.6, 20.5, b));
                Ln(d, 2, Lerp(19, 22, b), Lerp(21, 16.5, b), Lerp(21, 28, b), Lerp(22.6, 19, b));
            });
            L(p.Mark, 105, d => { Ears(d); Ell(d, bx - 3.5, by, brx * 0.55, bry * 0.95); Ell(d, hx + 0.4, hy - 1.2, 2.6, 2.4); });
            L(p.Accent, 95, d => Ln(d, 1.3, hx + 0.4, hy - 4.4, hx + 0.6 - Lerp(3, 4.5, b) + 0.6, hy - 3.2 - Lerp(6, 7, b) + 1.6));
            L(Cotton, 105, d => Ell(d, Lerp(6.6, 5.2, b), Lerp(16, 13.5, b), 2.3, 2.1));
            L(Dark, 105, d =>
            {
                int ex = (int)Math.Round(hx + 1), ey = (int)Math.Round(hy - 1);
                if (eyes == Eyes.Happy) { Rect(d, ex - 1, ey + 1, 1, 1); Rect(d, ex, ey, 1, 1); Rect(d, ex + 1, ey + 1, 1, 1); }
                else Rect(d, ex, ey, 1, 2);
            });
            L(Pink, 105, d => Rect(d, Math.Round(hx + 4), Math.Round(hy), 1, 1));
        }
        else
        {
            // A loaf with its ears laid flat along its back.
            double br = s;
            void Ears(DrawingContext d) { Ln(d, 2.6, 22, 14.4, 14.5, 13.6 - br * 0.3); Ln(d, 2.6, 22, 15.8, 15, 16 - br * 0.3); }
            L(p.Fur, 105, d => { Ell(d, 15.5, 18.6 - br * 0.4, 9.5, 4.8 + br * 0.7); Ell(d, 24, 18, 4.4, 4); });
            L(p.Mark, 105, d => { Ell(d, 11.5, 18.6 - br * 0.4, 5.2, 4.6 + br * 0.7); Ell(d, 24.5, 17, 2.4, 2.2); });
            L(p.Shade, 105, d => Ears(d));
            L(p.Fur, 105, d => Ln(d, 1.4, 21.6, 14.4, 16, 13.8 - br * 0.3));
            L(Cotton, 105, d => Ell(d, 6.3, 19, 2.2, 2));
            L(p.Line, 80, d => Quad(d, 8, 22, 15, 23.6, 22, 21.5, 1.1));
            L(Dark, 105, d => Rect(d, 24, 17, 2, 1));
            L(Pink, 105, d => Rect(d, 28, 18, 1, 1));
        }
    }

    /// <summary>
    /// Where the look-change turn is at u (0..1): the body's heading A (0 facing right, π/2 facing you,
    /// 3π/2 its back to you), how far its spine bends into the turn, and where its head is looking.
    /// The faster it turns the more it bends, so it is straight again at both ends.
    /// </summary>
    private static (double A, double Bend, double Yaw) TurnAt(string kind, double u)
    {
        double A = 2 * Math.PI * (u - Math.Sin(2 * Math.PI * u) / (2 * Math.PI));
        double bend = 0.085 * 2 * Math.PI * (1 - Math.Cos(2 * Math.PI * u));
        double rest = kind == "Bunny" ? 0 : kind == "Dog" ? Math.PI / 2 : 1.25;   // how the sitting art holds its head
        return (A, bend, A + rest + bend);
    }

    /// <summary>
    /// The sitting pet turning round where it sits, built from the same shapes and sizes as the sitting
    /// art. Its spine bends: the head leads, the chest follows, the haunch comes round last and the tail
    /// swings out behind. Parts are placed in 3D, drawn far to near, and shaded from the upper left while
    /// it moves; at u = 0 and 1 it is the sitting picture again.
    /// </summary>
    private static void BendLayers(string kind, AddLayer L, Palette p, double u, Eyes eyes)
    {
        var (A, bend, yaw) = TurnAt(kind, u);
        double w = Math.Sin(Math.PI * u);              // how much it is on the move
        bool bunny = kind == "Bunny", dog = kind == "Dog", cat = !bunny && !dog;
        double aBack = A - 0.8 * bend, aFront = A + 0.2 * bend;
        double cy = Math.Cos(yaw), sy = Math.Sin(yaw);
        const double K = 0.2;                          // nearer things sit a touch lower on the floor
        var parts = new List<(double Z, Action Draw)>();
        var O = (X: 15.5, Z: 0.0);

        (double X, double Z) At((double X, double Z) o, double a, double fwd, double side) =>
            (o.X + fwd * Math.Cos(a) - side * Math.Sin(a), o.Z + fwd * Math.Sin(a) + side * Math.Cos(a));
        double Wd(double rl, double rs, double a) => Math.Sqrt(Math.Pow(rl * Math.Cos(a), 2) + Math.Pow(rs * Math.Sin(a), 2));
        uint Side(double side, double a) => side * Math.Cos(a) < -0.1 ? p.Shade : p.Fur;
        void Ball(uint c, (double X, double Z) q, double y, double rl, double rs, double ry, double a,
                  double bias = 0, bool round = true, Action<double, double>? more = null)
        {
            double x = q.X, yy = y + q.Z * K, rx = Wd(rl, rs, a);
            parts.Add((q.Z + bias, () =>
            {
                double s = round ? w : 0;
                if (s > 0.1)
                {
                    L(p.Shade, 105, d => Ell(d, x, yy, rx, ry));
                    L(c, 105, d => Ell(d, x - 0.9 * s, yy - 0.6 * s, Math.Max(0.8, rx - 0.6 * s), Math.Max(0.8, ry - 0.45 * s)));
                }
                else L(c, 105, d => Ell(d, x, yy, rx, ry));
                more?.Invoke(x, yy);
            }));
        }
        void Stroke(uint c, double z, double width, params (double X, double Z, double Y)[] pts)
        {
            var flat = new double[pts.Length * 2];
            for (int i = 0; i < pts.Length; i++) { flat[i * 2] = pts[i].X; flat[i * 2 + 1] = pts[i].Y + pts[i].Z * K; }
            parts.Add((z, () => L(c, 105, d => { if (flat.Length == 6) Quad(d, flat[0], flat[1], flat[2], flat[3], flat[4], flat[5], width); else Ln(d, width, flat); })));
        }
        // A point on the head, fwd ahead of its middle and side to its side, and how much it faces you.
        (double X, double Z, double Vis) OnHead((double X, double Z) h, double fwd, double side)
        {
            var q = At(h, yaw, fwd, side);
            return (q.X, q.Z, (q.Z - h.Z) / Math.Max(0.01, Math.Sqrt(fwd * fwd + side * side)));
        }

        (double X, double Z) head; double hy;
        var overlay = new List<Action>();
        if (bunny)
        {
            var body = At(O, aFront, -1.5, 0);
            head = At(body, aFront, 7, 0); hy = 11.4;
            Ball(p.Shade, At(body, aFront, 9.4, -1.2), 22.6, 1.6, 1.2, 1.1, aFront, round: false);
            if (w > 0.15) Ball(p.Shade, At(O, aBack, -3, -2), 22.8, 4, 1.4, 1.2, aBack, round: false);
            Ball(p.Fur, body, 17.5, 8, 5.6, 6, aFront);
            Ball(p.Mark, At(O, aBack, -5, 0), 17.5, 4.6, 4.6, 5.6, aBack, bias: 0.02);
            Ball(Cotton, At(O, aBack, -9.3, 0), 17.8, 2.4, 2.4, 2.2, aBack, bias: 0.03, round: false);
            Ball(Side(1, aFront), At(body, aFront, 7, 1.2), 22.6, 1.8, 1.2, 1.1, aFront, bias: 0.04, round: false);
            Ball(Side(2, aBack), At(O, aBack, -3, 2), 22.8, 4, 1.4, 1.2, aBack, bias: 0.04, round: false);
            Ball(p.Fur, head, hy, 4.6, 4.4, 4.2, yaw, bias: 2, more: (x, y) =>
                L(p.Mark, 105, d => Ell(d, OnHead(head, 0.4, 0).X, y - 1.2, 2.6, 2.4)));
            // Tall ears: the near one shows its pink inside while it faces you.
            var ears = new[] { (bf: -1.4, bs: -1.0, tf: -2.6, ts: -1.3), (bf: 1.2, bs: 1.0, tf: 1.8, ts: 1.3) };
            double nearZ = Math.Max(At(head, yaw, ears[0].bf, ears[0].bs).Z, At(head, yaw, ears[1].bf, ears[1].bs).Z);
            foreach (var e in ears)
            {
                var b0 = At(head, yaw, e.bf, e.bs); var t0 = At(head, yaw, e.tf, e.ts);
                bool pink = b0.Z >= nearZ - 0.01 && sy > -0.3;
                parts.Add((b0.Z + 2.5, () =>
                {
                    L(p.Mark, 105, d => Ln(d, 2.8, b0.X, 8 + head.Z * K, t0.X, 1.3 + head.Z * K));
                    if (pink) L(p.Accent, 95, d => Ln(d, 1.3, b0.X, 6.8 + head.Z * K, b0.X + (t0.X - b0.X) * 0.75, 2.6 + head.Z * K));
                }));
            }
            if (Math.Cos(aFront) > 0.85) overlay.Add(() => L(p.Line, 80, d => Arc(d, body.X, 17.5 + body.Z * K, 6, 1.15, 1.6)));
            overlay.Add(() =>
            {
                L(Dark, 105, d =>
                {
                    foreach (var s in new[] { -1.8, 1.8 })
                    {
                        var e = OnHead(head, 1.2, s);
                        if (e.Vis < 0.25) continue;
                        double ex = Math.Floor(e.X), ey = 10 + head.Z * K;
                        if (eyes == Eyes.Happy) { Rect(d, ex - 1, ey + 1, 1, 1); Rect(d, ex, ey, 1, 1); Rect(d, ex + 1, ey + 1, 1, 1); }
                        else if (eyes == Eyes.Open) Rect(d, ex, ey, 1, 2);
                        else Rect(d, ex - 1, ey + 1, 2, 1);
                    }
                });
                var n = OnHead(head, 4, 0);
                if (n.Vis > -0.3) L(Pink, 105, d => Rect(d, Math.Floor(n.X), 12 + head.Z * K, 1, 1));
            });
        }
        else
        {
            var Ph = At(O, aBack, -2.5, 0); var Pc = At(O, aFront, 1.5, 0);
            head = At(Pc, aFront, 2, 0); hy = dog ? 7.5 : 7;

            // Tail, swinging out behind as the hips come round.
            if (cat)
                Stroke(p.Fur, At(Ph, aBack, -10, -1.5 * bend).Z, 2.8,
                    Pt(At(Ph, aBack, -6, 0), 21.6), Pt(At(Ph, aBack, -10, -1.5 * bend), 23.5), Pt(At(Ph, aBack, -11.2, -3 * bend), 21.5));
            else
            {
                double wag = 0.3 + 0.3 * Math.Sin(2 * Math.PI * u * 5) * w;
                Stroke(p.Fur, At(Ph, aBack, -10, -1.5 * bend).Z, 2.6,
                    Pt(At(Ph, aBack, -5.5, 0), 18.5), Pt(At(Ph, aBack, -10, -1.5 * bend), 17.5), Pt(At(Ph, aBack, -11.8 + 4.5 * wag, -3 * bend), 12.6));
            }
            // Front legs, the far one in shadow; they shuffle round as it turns.
            foreach (var (fwd, side, ph) in new[] { (5.6, -1.2, 0.5), (3.4, 1.2, 0.0) })
            {
                var q = At(Pc, aFront, fwd, side);
                double lift = 1.4 * w * Math.Max(0, Math.Sin(2 * Math.PI * (u * 4 + ph)));
                double width = side < 0 && Math.Cos(aFront) > 0.5 ? 2.6 : 3;
                Stroke(Side(side, aFront), q.Z, width, (q.X, q.Z, 15), (q.X, q.Z, 22.4 - lift));
            }
            if (w > 0.15)
                foreach (var side in new[] { -3.8, 3.8 })
                    Ball(Side(side, aBack), At(Ph, aBack, 2, side), 22.3, 2.6, 1.3, 1.1, aBack, round: false);
            Ball(p.Fur, Ph, 18, 7, 5.6, 5.5, aBack);
            Ball(p.Fur, Pc, 14, 6, 4.6, 7.5, aFront, bias: 0.01);
            if (dog) Ball(p.Accent, At(Pc, aFront, 2.6, 0), 15.5, 2, 2, 3, aFront, bias: 0.05, round: false);

            Ball(p.Fur, head, hy, 5.3, 5.3, dog ? 4.6 : 4.7, yaw, bias: 2, more: (x, y) =>
            {
                if (!cat) return;
                // Pointed ears; their pink insides show while they face you.
                foreach (var s in new[] { -1, 1 })
                {
                    var e = OnHead(head, 0.2, s * 2.6);
                    double hw = 0.8 + 1.3 * Math.Abs(sy), lean = (e.X - head.X) * 0.35;
                    L(p.Fur, 105, d => Tri(d, e.X - hw, y - 1.8, e.X + lean, y - 7.6, e.X + hw, y - 1.8));
                    if (sy > 0.3) L(p.Accent, 105, d => Rect(d, Math.Round(e.X - 0.5), y - 6, 1, 2));
                }
                L(p.Line, 80, d => Arc(d, x, y, 5.7, 0.62, 0.92));
            });
            if (dog)
            {
                foreach (var s in new[] { -5.1, 5.1 }) Ball(p.Mark, At(head, yaw, 0, s), 8.4, 2.5, 1.7, 3.4, yaw, bias: 2.1, round: false);
                Ball(p.Accent, At(head, yaw, 3.5, -0.5), 10.2, 1.6, 2.7, 1.9, yaw, bias: 2.2, round: false);
            }

            if (cat && Math.Abs(Math.Cos(aBack)) > 0.35)
            {
                double c = Math.Cos(aBack), yz = Ph.Z * K;
                overlay.Add(() => L(p.Mark, 95, d =>
                {
                    Ln(d, 1.4, Ph.X - 1 * c, 11.6 + yz, Ph.X + 1 * c, 12.4 + yz);
                    Ln(d, 1.4, Ph.X - 1.6 * c, 14 + yz, Ph.X + 0.4 * c, 14.8 + yz);
                    Ln(d, 1.4, Ph.X - 1.4 * c, 16.6 + yz, Ph.X + 0.4 * c, 17.2 + yz);
                }));
            }
            if (Math.Cos(aBack) > 0.85) overlay.Add(() => L(p.Line, 80, d => Arc(d, Ph.X, 18 + Ph.Z * K, 6, 1.2, 1.65)));
            overlay.Add(() =>
            {
                double y0 = hy + head.Z * K;
                L(Dark, 105, d =>
                {
                    foreach (var s in new[] { -1, 1 })
                    {
                        var e = cat ? OnHead(head, 4.6, s * 1.9) : OnHead(head, 4, s * 2);
                        if (e.Vis < 0.2) continue;
                        double ex = Math.Floor(e.X), ey = y0 - 1;
                        if (eyes == Eyes.Happy) { Rect(d, ex - 1, ey, 1, 1); Rect(d, ex, ey - 1, 1, 1); Rect(d, ex + 1, ey, 1, 1); }
                        else if (eyes == Eyes.Open) Rect(d, ex, ey, 1, 2);
                        else Rect(d, ex - 1, y0, 2, 1);
                    }
                    if (dog) { var n = OnHead(head, 4.8, -0.5); if (n.Vis > 0.1) Rect(d, Math.Floor(n.X - 0.5), y0 + 1.5, 2, 1); }
                });
                if (dog)
                {
                    var n = OnHead(head, 4.8, -0.5);
                    if (eyes == Eyes.Happy && n.Vis > 0.1) L(Pink, 105, d => Rect(d, Math.Floor(n.X - 0.5) + 1, y0 + 3.5, 1, 2));
                }
                else
                {
                    var n = OnHead(head, 5.2, 0);
                    if (n.Vis > 0.1) L(p.Accent, 105, d => Rect(d, Math.Floor(n.X), y0 + 1, 1, 1));
                }
            });
        }

        parts.Sort((a, b) => a.Z.CompareTo(b.Z));
        foreach (var part in parts) part.Draw();
        foreach (var o in overlay) o();

        static (double X, double Z, double Y) Pt((double X, double Z) q, double y) => (q.X, q.Z, y);
    }

    /// <summary>
    /// The pet up on its feet and walking, seen from any side: a = 0 side-on facing right, π/2 facing
    /// you, π facing left, 3π/2 its back to you; g is where it is in its stride. Each body part is a
    /// little ball placed in 3D and drawn far to near; the light stays up and to the left, so the
    /// shading slides across it as it turns, and the legs on its far side are in shadow.
    /// </summary>
    private static void WalkLayers(string kind, AddLayer L, Palette p, double a, double g, Eyes eyes)
    {
        double ca = Math.Cos(a), sa = Math.Sin(a);
        const double cx = 16;
        double Sx(double X, double Y) => cx + X * ca - Y * sa;     // across the screen
        double Dz(double X, double Y) => X * sa + Y * ca;          // toward you
        double Rw(double rl, double rs) => Math.Sqrt(rl * rl * ca * ca + rs * rs * sa * sa);
        var parts = new List<(double Z, Action Draw)>();

        void Part(uint c, double X, double Y, double y, double rl, double rs, double ry, bool shade = true, Action? more = null)
        {
            double x = Sx(X, Y), rx = Rw(rl, rs);
            parts.Add((Dz(X, Y), () =>
            {
                if (shade)
                {
                    L(p.Shade, 105, d => Ell(d, x, y, rx, ry));
                    L(c, 105, d => Ell(d, x - 1.2, y - 0.7, Math.Max(0.8, rx - 0.7), Math.Max(0.8, ry - 0.5)));
                }
                else L(c, 105, d => Ell(d, x, y, rx, ry));
                more?.Invoke();
            }));
        }
        // A spot on a part's surface, th radians round from its front; vis > 0 when it faces you.
        (double X, double Y, double Vis) On(double X0, double r, double th) =>
            (X0 + r * Math.Cos(th), r * Math.Sin(th), Math.Cos(th) * sa + Math.Sin(th) * ca);

        // Legs on the side away from you are in shadow (both lit when it faces you or walks away).
        uint Side(double Y) => Y * ca < -0.01 ? p.Shade : p.Fur;
        // A leg swinging through its stride: forward and back, lifting as it comes through.
        void Leg(double X, double Y, double ph, double y, double ry)
        {
            double w = 2 * Math.PI * (g + ph);
            Part(Side(Y), X + 1.7 * Math.Sin(w), Y, y - 1.1 * Math.Max(0, Math.Cos(w)), 1.3, 1.3, ry, false);
        }

        bool bunny = kind == "Bunny";
        double hX = bunny ? 6.5 : 9.5, hy = bunny ? 11.6 : kind == "Dog" ? 10.5 : 10, hr = bunny ? 4.4 : 5, hry = bunny ? 4.2 : 4.5;
        if (bunny)
        {
            // Low and round, hopping on its big back feet.
            Part(p.Mark, -2.5, 0, 17.2, 5.6, 5, 5.2);
            Part(p.Fur, 1.5, 0, 17.4, 6, 5.2, 5.4);
            foreach (var Y in new[] { -3.0, 3.0 }) Part(Side(Y), -3, Y, 22.8, 3.4, 1.4, 1.1, false);
            foreach (var Y in new[] { -1.6, 1.6 }) Part(Side(Y), 5, Y, 22.5, 1.5, 1.2, 1.1, false);
            Part(Cotton, -8, 0, 16.6, 2.2, 2.2, 2.1, false);
        }
        else
        {
            // Four legs under a long body, walking in diagonal pairs.
            Leg(6, 2.4, 0, 20.4, 2.6); Leg(-6, -2.4, 0, 20.4, 2.6);
            Leg(6, -2.4, 0.5, 20.4, 2.6); Leg(-6, 2.4, 0.5, 20.4, 2.6);
            Part(p.Fur, 0, 0, 15.4, 9, 4.8, 4.6, more: kind == "Cat" && Math.Abs(ca) > 0.35 ? () =>
                L(p.Mark, 95, d => { foreach (var dx in new[] { -4.0, -1.0, 2.0 }) Ln(d, 1.4, Sx(dx, 0), 11.2, Sx(dx + 0.4, 0), 13.2); }) : null);
            if (kind == "Dog") { var c = On(6.5, 3.6, 0); Part(p.Accent, c.X, c.Y, 16.2, 1.8, 2, 2.4, false); }
            if (kind == "Cat")   // tail held up in a question mark
                foreach (var (X, y) in new[] { (-9.5, 13.6), (-11.2, 11.4), (-11.8, 9), (-11, 6.8) })
                    Part(p.Fur, X, 0, y, 1.3, 1.3, 1.3, false);
            else                 // tail up and wagging
                foreach (var (X, y) in new[] { (-9.4, 13.4), (-10.6, 11.4), (-11.2, 9.6) })
                    Part(p.Fur, X, 0.8 * Math.Sin(2 * Math.PI * g * 2) * (X + 9) / -2, y, 1.4, 1.4, 1.4, false);
        }

        // The head, then everything on it.
        Part(p.Fur, hX, 0, hy, hr, hr, hry, more: () =>
        {
            if (kind == "Cat")
                foreach (var side in new[] { -1, 1 })
                {
                    var e = On(hX, hr * 0.6, side * 1.1);
                    double x = Sx(e.X, e.Y);
                    L(p.Fur, 105, d => Tri(d, x - 1.8, hy - 2.4, x, hy - 7.6, x + 1.8, hy - 2.4));
                    if (e.Vis > 0.2) L(p.Accent, 105, d => Rect(d, Math.Round(x - 0.5), hy - 6, 1, 2));
                }
            if (bunny)
                foreach (var side in new[] { -1, 1 })
                {
                    var e = On(hX, hr * 0.5, side * 0.6);
                    double x = Sx(e.X, e.Y), lean = -1.8 * ca;
                    L(p.Mark, 105, d => Ln(d, 2.8, x, hy - 3, x + lean, 1.3));
                    if (e.Vis > 0.2) L(p.Accent, 95, d => Ln(d, 1.3, x, hy - 4.2, x + lean, 2.6));
                }
            if (bunny) L(p.Mark, 105, d => Ell(d, Sx(hX, 0) - 0.4, hy - 1.4, Rw(2.6, 2.6), 2.2));
        });
        if (kind == "Dog")
        {
            foreach (var side in new[] { -1, 1 })
            {
                var e = On(hX, hr, side * 1.5);
                Part(p.Mark, e.X, e.Y, hy + 1, 1.2, 1.7, 3.4, false);
            }
            var m = On(hX, hr * 0.75, 0);
            Part(p.Accent, m.X, m.Y, hy + 2.6, 2.4, 2.6, 1.9, false);
        }

        parts.Sort((u, v) => u.Z.CompareTo(v.Z));
        foreach (var part in parts) part.Draw();

        // The face, only where it is turned toward you.
        L(Dark, 105, d =>
        {
            foreach (var side in new[] { -1, 1 })
            {
                var e = On(hX, hr, side * 0.7);
                if (e.Vis < 0.15) continue;
                double x = Math.Round(Sx(e.X, e.Y) - 0.5), y = hy - 1;
                if (eyes == Eyes.Happy) { Rect(d, x - 1, y + 1, 1, 1); Rect(d, x, y, 1, 1); Rect(d, x + 1, y + 1, 1, 1); }
                else Rect(d, x, y, 1, 2);
            }
            if (kind == "Dog" && sa > -0.2) { var n = On(hX, hr * 0.75 + 2.2, 0); Rect(d, Math.Round(Sx(n.X, n.Y) - 1), hy + 1.6, 2, 1); }
        });
        if (sa > -0.2)
        {
            var n = On(hX, hr * 0.95, 0);
            double nx = Math.Round(Sx(n.X, n.Y) - 0.5);
            if (kind == "Dog") { if (eyes == Eyes.Happy) L(Pink, 105, d => Rect(d, nx + 0.5, hy + 3.6, 1, 2)); }
            else L(Pink, 105, d => Rect(d, nx, bunny ? hy + 0.6 : hy + 1, 1, 1));
        }
    }

    /// <summary>Draws white shapes on a tiny 34×24 canvas and keeps the cells covered past the threshold.</summary>
    private static bool[] Raster(Action<DrawingContext> draw, int threshold)
    {
        var v = new DrawingVisual();
        using (var d = v.RenderOpen()) draw(d);
        var rtb = new RenderTargetBitmap(GW, GH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        var buf = new byte[GW * GH * 4];
        rtb.CopyPixels(buf, GW * 4, 0);
        var mask = new bool[GW * GH];
        for (int i = 0; i < mask.Length; i++) mask[i] = buf[i * 4 + 3] > threshold;
        return mask;
    }

    private static readonly Brush White = Freeze(Brushes.White);
    private static Brush Freeze(Brush b) { if (b.CanFreeze && !b.IsFrozen) b.Freeze(); return b; }

    private static Pen P(double w)
    {
        var p = new Pen(White, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static void Ell(DrawingContext d, double x, double y, double rx, double ry) =>
        d.DrawEllipse(White, null, new Point(x, y), rx, ry);

    private static void Ln(DrawingContext d, double w, params double[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(pts[0], pts[1]), false, false);
            for (int i = 2; i < pts.Length; i += 2) c.LineTo(new Point(pts[i], pts[i + 1]), true, true);
        }
        d.DrawGeometry(null, P(w), g);
    }

    private static void Quad(DrawingContext d, double a, double b, double cx, double cy, double e, double f, double w)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(a, b), false, false);
            c.QuadraticBezierTo(new Point(cx, cy), new Point(e, f), true, true);
        }
        d.DrawGeometry(null, P(w), g);
    }

    /// <summary>Clockwise arc from a·π to b·π (screen angles, y down), like a canvas arc.</summary>
    private static void Arc(DrawingContext d, double x, double y, double r, double a, double b)
    {
        const int n = 16;
        var pts = new double[(n + 1) * 2];
        for (int i = 0; i <= n; i++)
        {
            double ang = Math.PI * (a + (b - a) * i / n);
            pts[i * 2] = x + r * Math.Cos(ang);
            pts[i * 2 + 1] = y + r * Math.Sin(ang);
        }
        Ln(d, 1.2, pts);
    }

    private static void Tri(DrawingContext d, double a, double b, double c, double e, double f, double g)
    {
        var geo = new StreamGeometry();
        using (var k = geo.Open())
        {
            k.BeginFigure(new Point(a, b), true, true);
            k.LineTo(new Point(c, e), true, true);
            k.LineTo(new Point(f, g), true, true);
        }
        d.DrawGeometry(White, null, geo);
    }

    private static void Rect(DrawingContext d, double x, double y, double w, double h) =>
        d.DrawRectangle(White, null, new System.Windows.Rect(x, y, w, h));

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>A galloping leg from the hip or shoulder; reach is how far forward (+) or back (−) the paw is.</summary>
    private static void Gallop(DrawingContext d, double hx, double hy, double reach)
    {
        double fx = hx + reach, fy = 22.3 - Math.Max(0, Math.Abs(reach) - 1.5) * 0.5;
        Ln(d, 3, hx, hy, fx, fy);
        Ell(d, fx + 0.4, fy + 0.2, 1.7, 1.1);
    }

    private static void HeadFur(DrawingContext d, double x, double y)
    {
        Ell(d, x, y, 5.3, 4.7);
        Tri(d, x - 4.6, y - 1.8, x - 3.4, y - 7.6, x - 0.4, y - 3.8);
        Tri(d, x + 0.4, y - 3.8, x + 3.4, y - 7.6, x + 4.6, y - 1.8);
    }

    private static void HeadPink(DrawingContext d, double x, double y)
    {
        Rect(d, x - 3, y - 6, 1, 2);
        Rect(d, x + 3, y - 6, 1, 2);
        Rect(d, x + 1, y + 1, 1, 1);
    }

    private static void Face(DrawingContext d, double x, double y, Eyes m)
    {
        if (m == Eyes.Open) { Rect(d, x - 1, y - 1, 1, 2); Rect(d, x + 3, y - 1, 1, 2); }
        else if (m == Eyes.Happy)
        {
            Rect(d, x - 2, y - 1, 1, 1); Rect(d, x - 1, y - 2, 1, 1); Rect(d, x, y - 1, 1, 1);
            Rect(d, x + 2, y - 1, 1, 1); Rect(d, x + 3, y - 2, 1, 1); Rect(d, x + 4, y - 1, 1, 1);
        }
        else { Rect(d, x - 2, y, 2, 1); Rect(d, x + 3, y, 2, 1); }
    }

    // ───────────────────────────── are you there? ─────────────────────────────

    private static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
