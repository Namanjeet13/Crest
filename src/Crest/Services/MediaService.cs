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

    // ───── source filter: Spotify only by default; apps and websites can be added ─────

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

            if (newTrack || Art == null)
            {
                Art = null;
                if (props.Thumbnail != null)
                {
                    var art = await LoadArtAsync(props.Thumbnail);
                    if (token != _loadToken) return;
                    Art = art;
                    if (art != null) ArtColor = DominantColor(art);
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

    public bool StartingSpotify { get; private set; }

    /// <summary>
    /// Nothing is playing: open Spotify out of sight and resume whatever it had last.
    /// True once Spotify is playing.
    /// </summary>
    public async Task<bool> ResumeSpotifyAsync()
    {
        if (StartingSpotify || _manager == null) return false;
        StartingSpotify = true;
        IntPtr focus = SpotifyStarter.Foreground;
        try
        {
            bool launched = false;
            if (!SpotifyStarter.IsRunning)
            {
                if (!SpotifyStarter.Start()) return false;
                launched = true;
            }
            // Spotify takes a few seconds to load the last song and show up in Windows' media controls.
            var until = DateTime.Now.AddSeconds(30);
            DateTime nextPress = DateTime.MinValue;
            while (DateTime.Now < until)
            {
                if (launched) SpotifyStarter.PutAway(focus);
                var s = FindSpotify();
                if (s != null)
                {
                    var info = s.GetPlaybackInfo();
                    if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        Rebind();
                        if (launched)
                        {
                            // Its window can still pop up a moment after it starts.
                            for (int i = 0; i < 6; i++) { await Task.Delay(500); SpotifyStarter.PutAway(focus); }
                        }
                        return true;
                    }
                    // Press as soon as the session appears and keep pressing: Spotify often ignores
                    // the first few while it is still loading the last song. Play never toggles, so repeats are safe.
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
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
        finally
        {
            StartingSpotify = false;
        }
    }

    private GlobalSystemMediaTransportControlsSession? FindSpotify()
    {
        try
        {
            foreach (var s in _manager!.GetSessions())
                if ((s.SourceAppUserModelId ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase)) return s;
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
