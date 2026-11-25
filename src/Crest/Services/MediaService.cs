using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Crest.Services;

/// <summary>
/// Now-playing from any app that talks to Windows' media controls (Spotify,
/// browsers, Apple Music, VLC...). Prefers the session that is actually playing,
/// not just whichever one Windows calls "current".
/// </summary>
public sealed class MediaService
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _loadToken;
    private string _trackKey = "";

    private TimeSpan _position;
    private DateTimeOffset _positionStamp;
    private double _rate = 1;
    private TimeSpan _startTime;
    private TimeSpan _seekTarget;
    private DateTime _seekGuardUntil;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public string Source { get; private set; } = "";
    /// <summary>The playing app's AppUserModelId, used to take the user back to it.</summary>
    public string SourceId { get; private set; } = "";
    public BitmapSource? Art { get; private set; }
    public Color ArtColor { get; private set; } = Color.FromRgb(0x0A, 0x84, 0xFF);
    public bool IsPlaying { get; private set; }
    public bool HasSession => _session != null && Title.Length > 0;
    public TimeSpan Duration { get; private set; }
    public bool CanSeek { get; private set; }

    public event Action? Changed;
    public event Action? TrackChanged;

    public MediaService(Dispatcher ui, Settings settings)
    {
        _ui = ui;
        _settings = settings;
    }

    public async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _ui.BeginInvoke(Rebind);
            _manager.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(Rebind);
            Rebind();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    // ───── source filter: Spotify and Apple Music by default; apps and websites can be added ─────

    private readonly Settings _settings;
    private readonly Dictionary<string, bool> _domainVerdicts = new();
    private readonly HashSet<string> _resolving = new();
    private readonly Dictionary<string, bool> _lastVerdictByApp = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _watched = new();

    /// <summary>Settings changed: forget site verdicts and pick again.</summary>
    public void Reconsider()
    {
        _domainVerdicts.Clear();
        _lastVerdictByApp.Clear();
        Rebind();
    }

    private static bool IsDomain(string entry) => entry.Contains('.') && !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private bool Allowed(GlobalSystemMediaTransportControlsSession s)
    {
        if (_settings.AnyMediaSource) return true;
        string aumid = s.SourceAppUserModelId ?? "";
        if (aumid.Length == 0) return false;
        string lower = aumid.ToLowerInvariant();
        string key = SourceLauncher.ProcessKey(aumid);

        var apps = _settings.MediaSources.Where(e => !string.IsNullOrWhiteSpace(e) && !IsDomain(e))
                                         .Select(e => e.Trim().ToLowerInvariant());
        foreach (var a in apps)
        {
            string app = a.EndsWith(".exe") ? a[..^4] : a;
            if (key.Contains(app) || lower.Contains(app)) return true;
        }

        if (!SourceLauncher.IsBrowser(aumid)) return false;
        var domains = _settings.MediaSources.Where(IsDomain).Select(e => e.Trim().ToLowerInvariant()).ToList();
        if (domains.Count == 0) return false;

        string title = BrowserTitle(s, aumid);
        if (title.Length == 0) return _lastVerdictByApp.TryGetValue(aumid, out var prevEmpty) && prevEmpty;

        string vkey = aumid + "" + title;
        if (_domainVerdicts.TryGetValue(vkey, out bool verdict)) return verdict;

        if (_resolving.Add(vkey))
        {
            Task.Run(() => SourceLauncher.TabMatchesDomain(aumid, title, domains)).ContinueWith(t =>
                _ui.BeginInvoke(() =>
                {
                    _resolving.Remove(vkey);
                    bool ok = t.Status == TaskStatus.RanToCompletion && t.Result;
                    _domainVerdicts[vkey] = ok;
                    _lastVerdictByApp[aumid] = ok;
                    Rebind();
                }));
        }
        // While we look, keep whatever this browser was last time so a new song does not flicker.
        return _lastVerdictByApp.TryGetValue(aumid, out var prev) && prev;
    }

    private readonly Dictionary<string, string> _browserTitles = new();
    private readonly HashSet<string> _titleFetching = new();

    /// <summary>
    /// The browser tab's title, from a cache. It is refreshed in the background (never by blocking
    /// the UI thread), and a change re-picks the session.
    /// </summary>
    private string BrowserTitle(GlobalSystemMediaTransportControlsSession s, string aumid)
    {
        _browserTitles.TryGetValue(aumid, out var cached);
        if (_titleFetching.Add(aumid))
        {
            _ = FetchTitle();
            async Task FetchTitle()
            {
                string fresh = cached ?? "";
                try { fresh = (await s.TryGetMediaPropertiesAsync())?.Title ?? ""; } catch { }
                _titleFetching.Remove(aumid);
                if (fresh == (cached ?? "")) { _browserTitles[aumid] = fresh; return; }
                _browserTitles[aumid] = fresh;
                Rebind();
            }
        }
        return cached ?? "";
    }

    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;
        try
        {
            var sessions = _manager.GetSessions();
            WatchAll(sessions);
            var allowed = sessions.Where(Allowed).ToList();
            foreach (var s in allowed)
                if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    return s;
            // Nothing allowed is playing: stay on the one we had (it may just be paused), else Windows' pick.
            if (_session != null && allowed.Any(a => a.SourceAppUserModelId == _session.SourceAppUserModelId))
                return allowed.First(a => a.SourceAppUserModelId == _session.SourceAppUserModelId);
            var current = _manager.GetCurrentSession();
            if (current != null && allowed.Any(a => a.SourceAppUserModelId == current.SourceAppUserModelId))
                return allowed.First(a => a.SourceAppUserModelId == current.SourceAppUserModelId);
            return allowed.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Listen to every session so an allowed app that starts playing is noticed at once.</summary>
    private void WatchAll(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        // Rebind runs on every song change; only re-subscribe when the set of sessions really changed.
        if (sessions.Count == _watched.Count && sessions.All(x => _watched.Contains(x))) return;
        foreach (var w in _watched) w.PlaybackInfoChanged -= OnAnyPlayback;
        _watched.Clear();
        foreach (var s in sessions)
        {
            s.PlaybackInfoChanged += OnAnyPlayback;
            _watched.Add(s);
        }
    }

    private void OnAnyPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
        => _ui.BeginInvoke(() =>
        {
            if (_session != null && s.SourceAppUserModelId == _session.SourceAppUserModelId) return;
            if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                Rebind();
        });

    private void Rebind()
    {
        var next = PickSession();
        if (ReferenceEquals(next, _session))
        {
            _ = RefreshAsync();
            return;
        }

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaProps;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }

        _session = next;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaProps;
            _session.PlaybackInfoChanged += OnPlayback;
            _session.TimelinePropertiesChanged += OnTimeline;
        }
        else
        {
            Title = Artist = Album = Source = SourceId = "";
            Art = null;
            IsPlaying = false;
            _trackKey = "";
        }
        _ = RefreshAsync();
    }

    private void OnMediaProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
        => _ui.BeginInvoke(Rebind);

    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
        => _ui.BeginInvoke(() =>
        {
            // Another app may have started playing: re-pick the most relevant session.
            var info = s.GetPlaybackInfo();
            bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing && !ReferenceEquals(s, _session)) { Rebind(); return; }
            ReadPlayback();
            ReadTimeline();
            Changed?.Invoke();
            // Still no cover: the app may add it on play or pause without saying so, so ask again.
            if (Art == null && Title.Length > 0) { _artTries = 0; _ = RefreshAsync(); }
        });

    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
        => _ui.BeginInvoke(() => { ReadTimeline(); Changed?.Invoke(); });

    private void ReadPlayback()
    {
        if (_session == null) { IsPlaying = false; return; }
        try
        {
            var info = _session.GetPlaybackInfo();
            IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _rate = info.PlaybackRate ?? 1;
            CanSeek = info.Controls.IsPlaybackPositionEnabled;
        }
        catch { IsPlaying = false; }
    }

    private void ReadTimeline()
    {
        if (_session == null) return;
        try
        {
            var t = _session.GetTimelineProperties();
            Duration = t.EndTime - t.StartTime;
            _startTime = t.StartTime;
            var pos = t.Position - t.StartTime;
            var stamp = t.LastUpdatedTime;
            if (stamp.Year < 2000) stamp = DateTimeOffset.Now;
            if (DateTime.Now < _seekGuardUntil)
            {
                // Apps often report the old position once more right after a seek; ignore it so the bar does not jump back.
                var projected = pos;
                if (IsPlaying) projected += DateTimeOffset.Now - stamp;
                if ((projected - _seekTarget).Duration() > TimeSpan.FromSeconds(3)) return;
                _seekGuardUntil = DateTime.MinValue;
            }
            _position = pos;
            _positionStamp = stamp;
        }
        catch { /* ignore */ }
    }

    /// <summary>Position extrapolated from the last update while playing.</summary>
    public TimeSpan Position
    {
        get
        {
            var p = _position;
            if (IsPlaying)
            {
                var elapsed = DateTimeOffset.Now - _positionStamp;
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(6))
                    p += TimeSpan.FromTicks((long)(elapsed.Ticks * _rate));
            }
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session == null) { Changed?.Invoke(); return; }

        ReadPlayback();
        ReadTimeline();
        SourceId = session.SourceAppUserModelId ?? "";
        Source = PrettySource(SourceId);

        int token = ++_loadToken;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (token != _loadToken || props == null) return;

            Title = props.Title ?? "";
            Artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            Album = props.AlbumTitle ?? "";

            string key = Title + "\u0001" + Artist;
            bool newTrack = key != _trackKey && Title.Length > 0;
            _trackKey = key;

            if (newTrack) { _artTries = 0; _artFound = false; }
            if (newTrack || Art == null || _artFound)
            {
                if (!_artFound) Art = null;
                if (props.Thumbnail != null)
                {
                    var art = await LoadArtAsync(props.Thumbnail);
                    if (token != _loadToken) return;
                    // The app's own cover always wins over one found online.
                    if (art != null) { Art = art; _artFound = false; ArtColor = DominantColor(art); }
                }
                // Apps often name the song before its cover is ready, and then never say when it is:
                // keep asking for a while instead of playing the whole song without art.
                if (Art == null && Title.Length > 0) RetryArt();
                // Still nothing after a couple of seconds: look the song up online.
                if (Art == null && Title.Length > 0 && _artTries >= 3 && _settings.FindMissingArt)
                {
                    var found = await ArtLookup.FindAsync(Title, Artist);
                    if (token != _loadToken) return;
                    if (found != null && Art == null) { Art = found; _artFound = true; ArtColor = DominantColor(found); }
                }
            }

            Changed?.Invoke();
            if (newTrack) TrackChanged?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static readonly double[] ArtRetrySeconds = { 0.5, 1, 2, 3, 5, 8, 12, 20, 30, 45 };
    private int _artTries;
    /// <summary>The cover on show was found online, so the app's own is still welcome if it turns up.</summary>
    private bool _artFound;
    private DispatcherTimer? _artRetry;

    /// <summary>Asks again for the cover of the current song, sooner at first and then less often (about 2 minutes in all).</summary>
    private void RetryArt()
    {
        if (_artTries >= ArtRetrySeconds.Length) return;
        if (_artRetry == null)
        {
            _artRetry = new DispatcherTimer(DispatcherPriority.Background, _ui);
            _artRetry.Tick += (_, _) =>
            {
                _artRetry.Stop();
                if (Art == null && _session != null) _ = RefreshAsync();
            };
        }
        _artRetry.Stop();
        _artRetry.Interval = TimeSpan.FromSeconds(ArtRetrySeconds[_artTries++]);
        _artRetry.Start();
    }

    private static async Task<BitmapSource?> LoadArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            var ms = new MemoryStream();
            await input.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 256;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A vivid, readable accent taken from the artwork.</summary>
    public static Color DominantColor(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, sum = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double B = px[i], G = px[i + 1], R = px[i + 2];
                double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double weight = sat * sat * (max / 255.0) + 0.01;
                r += R * weight; g += G * weight; b += B * weight; sum += weight;
            }
            r /= sum; g /= sum; b /= sum;

            // Lift dark colours so they read on black.
            double m = Math.Max(r, Math.Max(g, b));
            if (m < 170 && m > 0)
            {
                double k = 170 / m;
                r *= k; g *= k; b *= k;
            }
            return Color.FromRgb((byte)Math.Min(255, r), (byte)Math.Min(255, g), (byte)Math.Min(255, b));
        }
        catch
        {
            return Color.FromRgb(0x0A, 0x84, 0xFF);
        }
    }

    private static string PrettySource(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        // Apple's app reports itself as "AppleInc.AppleMusicWin_…!App".
        if (id.Contains("applemusic", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
        if (id.Contains("itunes", StringComparison.OrdinalIgnoreCase)) return "iTunes";
        string s = id;
        int bang = s.LastIndexOf('!');
        if (bang >= 0) s = s[(bang + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return s.ToLowerInvariant() switch
        {
            "msedge" => "Edge",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            "spotify" => "Spotify",
            "zunemusic" or "music" => "Media Player",
            "app" => "Music",
            _ => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s,
        };
    }

    /// <summary>True while Play is opening a music app; <see cref="StartingName"/> says which.</summary>
    public bool Starting { get; private set; }
    public string StartingName { get; private set; } = "";

    /// <summary>The music services Play can open when nothing is playing.</summary>
    public static readonly string[] PlayServices = { "Spotify", "Apple Music", "YouTube Music", "Your own" };

    private const string AppleMusicFamily = "AppleInc.AppleMusicWin_nzyj5cx40ttqa";
    private static bool? _appleMusic;

    /// <summary>Apple's Microsoft Store app.</summary>
    public static bool AppleMusicInstalled => _appleMusic ??= FindAppleMusic();

    private static bool FindAppleMusic()
    {
        try
        {
            foreach (var _ in new Windows.Management.Deployment.PackageManager().FindPackagesForUser("", AppleMusicFamily)) return true;
        }
        catch { }
        return false;
    }

    private static string? ITunesPath()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string p = Path.Combine(Environment.GetFolderPath(root), "iTunes", "iTunes.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Whether Play can open <paramref name="service"/> at all.</summary>
    public static bool CanStart(string service, string custom) => service switch
    {
        "Spotify" => SpotifyStarter.IsInstalled,
        "Your own" => custom.Trim().Length > 0,
        _ => true, // Apple Music and YouTube Music fall back to their websites
    };

    /// <summary>The name Play shows while it opens <paramref name="service"/>.</summary>
    public static string ServiceName(string service, string custom) =>
        service == "Your own" ? QuickAction.ForTarget(custom).Label : service;

    /// <summary>
    /// Nothing is playing: open the chosen music app and resume whatever it had last.
    /// Spotify opens out of sight; Apple Music opens its app (or iTunes, or its website);
    /// YouTube Music and your own choice simply open. False if it couldn't be opened.
    /// </summary>
    public async Task<bool> ResumeAsync(string service, string custom)
    {
        if (Starting || _manager == null) return false;
        Starting = true;
        StartingName = ServiceName(service, custom);
        Changed?.Invoke();
        try
        {
            switch (service)
            {
                case "Apple Music":
                    if (AppleMusicInstalled)
                    {
                        if (!Launcher.Open(@"shell:AppsFolder\" + AppleMusicFamily + "!App")) return false;
                        await PlayWhenReady("applemusic", false, IntPtr.Zero);
                        return true; // it is open, even if it had nothing to resume
                    }
                    if (ITunesPath() is string itunes)
                    {
                        if (!Launcher.Open(itunes)) return false;
                        await PlayWhenReady("itunes", false, IntPtr.Zero);
                        return true;
                    }
                    return Launcher.Open("https://music.apple.com");
                case "YouTube Music":
                    return Launcher.Open("https://music.youtube.com");
                case "Your own":
                    return Launcher.Open(custom);
                default:
                    IntPtr focus = SpotifyStarter.Foreground;
                    bool launched = false;
                    if (!SpotifyStarter.IsRunning)
                    {
                        if (!SpotifyStarter.Start()) return false;
                        launched = true;
                    }
                    return await PlayWhenReady("spotify", launched, focus);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
        finally
        {
            Starting = false;
        }
    }

    /// <summary>
    /// Waits up to 30 s for the app's media session and presses Play until it plays.
    /// <paramref name="putAway"/>: a Spotify that was just started is kept minimized meanwhile.
    /// </summary>
    private async Task<bool> PlayWhenReady(string key, bool putAway, IntPtr focus)
    {
        // Apps take a few seconds to load the last song and show up in Windows' media controls.
        var until = DateTime.Now.AddSeconds(30);
        DateTime nextPress = DateTime.MinValue;
        while (DateTime.Now < until)
        {
            if (putAway) SpotifyStarter.PutAway(focus);
            var s = FindSession(key);
            if (s != null)
            {
                var info = s.GetPlaybackInfo();
                if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    Rebind();
                    if (putAway)
                    {
                        // Its window can still pop up a moment after it starts.
                        for (int i = 0; i < 6; i++) { await Task.Delay(500); SpotifyStarter.PutAway(focus); }
                    }
                    return true;
                }
                // Press as soon as the session appears and keep pressing: apps often ignore
                // the first few while they are still loading the last song. Play never toggles, so repeats are safe.
                if (DateTime.Now >= nextPress)
                {
                    try { await s.TryPlayAsync(); } catch { }
                    nextPress = DateTime.Now.AddMilliseconds(150);
                }
            }
            await Task.Delay(100);
        }
        return false;
    }

    private GlobalSystemMediaTransportControlsSession? FindSession(string key)
    {
        try
        {
            foreach (var s in _manager!.GetSessions())
                if ((s.SourceAppUserModelId ?? "").Contains(key, StringComparison.OrdinalIgnoreCase)) return s;
        }
        catch { }
        return null;
    }

    public async void TogglePlay()
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    public async void Previous()
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }

    public async void Seek(TimeSpan to)
    {
        try
        {
            if (_session == null) return;
            if (Duration > TimeSpan.Zero && to > Duration) to = Duration - TimeSpan.FromMilliseconds(500);
            if (to < TimeSpan.Zero) to = TimeSpan.Zero;
            _position = to;
            _positionStamp = DateTimeOffset.Now;
            _seekTarget = to;
            _seekGuardUntil = DateTime.Now.AddSeconds(2.5);
            Changed?.Invoke();
            bool ok = await _session.TryChangePlaybackPositionAsync((_startTime + to).Ticks);
            if (!ok)
            {
                App.Log(new Exception("Seek refused by " + SourceId));
                _seekGuardUntil = DateTime.MinValue;
                ReadTimeline();
                Changed?.Invoke();
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }
}

/// <summary>The music services Settings offers as switches. Each one is a set of entries in <see cref="Settings.MediaSources"/>.</summary>
public sealed record MusicPreset(string Name, string Sub, string Color, string[] Entries)
{
    public static readonly MusicPreset[] All =
    {
        new("Spotify", "The app and open.spotify.com.", "#1DB954", new[] { "spotify", "open.spotify.com" }),
        new("Apple Music", "The app, iTunes and music.apple.com.", "#FA2D48", new[] { "applemusic", "itunes", "music.apple.com" }),
        new("YouTube", "YouTube and YouTube Music in any browser, videos too.", "#FF0033", new[] { "youtube.com" }),
    };

    public static bool IsPreset(string entry) => Array.Exists(All, p => Array.IndexOf(p.Entries, entry) >= 0);
}
