using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Crest.Services;

/// <summary>Every knob in Crest. Saved as JSON; <see cref="Changed"/> lets the island apply edits live.</summary>
public sealed class Settings
{
    // ── General ──
    /// <summary>"Notch" hangs from the top edge; "Island" floats just below it.</summary>
    public string Style { get; set; } = "Notch";
    public bool HideUntilHover { get; set; }
    /// <summary>A little pixel cat that wanders the island when nothing is happening.</summary>
    public bool PixelPet { get; set; } = true;
    /// <summary>"Large" (the island grows a little to fit it) or "Small" (fits the usual island).</summary>
    public string PetSize { get; set; } = "Large";
    /// <summary>"Cat", "Dog" or "Bunny", and which look of it (see PixelPet.Looks).</summary>
    public string PetKind { get; set; } = "Cat";
    public string PetLook { get; set; } = "Cream";
    /// <summary>"Left" or "Right": the corner of the island it sits in.</summary>
    public string PetCorner { get; set; } = "Left";
    public bool StartWithWindows { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public bool OpenOnClickOnly { get; set; }
    public int HoverDelayMs { get; set; } = 120;
    public int CloseDelayMs { get; set; } = 300;
    public int PinSeconds { get; set; } = 6;

    // ── Island ──
    public int IslandOffset { get; set; } = 8;
    /// <summary>How far the island was dragged from the middle of the screen, in DIPs. 0 = centred.</summary>
    public double IslandX { get; set; }
    public int IdleWidth { get; set; } = 190;
    public int IdleHeight { get; set; } = 32;
    public int MusicWidth { get; set; } = 320;
    public int ExpandedCorner { get; set; } = 30;
    public int ShoulderSize { get; set; } = 8;
    public bool Shadow { get; set; } = true;
    public bool RimLight { get; set; } = true;
    /// <summary>The open island is see-through glass that bends the screen at its edge, like iOS.</summary>
    public bool LiquidGlass { get; set; } = true;
    public int GlassDim { get; set; } = 45;
    public int GlassFrost { get; set; } = 100;

    // ── Music ──
    /// <summary>App names (spotify, vlc...) or website domains (open.spotify.com, youtube.com).</summary>
    public List<string> MediaSources { get; set; } = new() { "spotify", "open.spotify.com" };
    public bool AnyMediaSource { get; set; }
    /// <summary>How long a paused song stays on the closed island. 0 hides it at once.</summary>
    public double PausedLingerMinutes { get; set; } = 10;
    public bool ShowArtInCompact { get; set; } = true;
    public bool ShowArtistInCompact { get; set; } = true;
    public bool ArtOpensSource { get; set; } = true;
    public bool TrackPeek { get; set; } = true;
    public double TrackPeekSeconds { get; set; } = 2.6;
    public bool ShowTimes { get; set; } = true;
    /// <summary>Look of the playback bar: "Apple", "Knob", "Glow", "Wave" or "Line".</summary>
    public string ScrubStyle { get; set; } = "Apple";
    /// <summary>Small volume bar next to the play buttons.</summary>
    public bool MiniVolume { get; set; } = true;

    // ── Artwork backdrop ──
    public bool ArtBackdrop { get; set; } = true;
    public int BackdropBlur { get; set; } = 46;
    public int BackdropDim { get; set; } = 52;
    public bool BackdropMotion { get; set; } = true;
    /// <summary>The closed island shows the blurred cover instead of plain black while a song is on.</summary>
    public bool ArtInClosedIsland { get; set; } = false;
    public bool ArtAccent { get; set; } = true;
    public string AccentHex { get; set; } = "#0A84FF";

    // ── Visualizer ──
    public bool Visualizer { get; set; } = true;
    /// <summary>"Art", "White" or "Accent".</summary>
    public string VizColor { get; set; } = "Art";
    public double VizSensitivity { get; set; } = 1.0;
    public bool VizGlow { get; set; } = true;

    // ── Tabs ──
    public bool ShowClipTab { get; set; } = true;
    public bool ShowControlsTab { get; set; } = true;
    public bool ShowSystemTab { get; set; } = true;
    /// <summary>The tabs left to right, by name: Home, Clipboard, Controls, System, Settings.</summary>
    public string TabOrder { get; set; } = "Home,Clipboard,Controls,System,Settings";

    public static readonly string[] TabNames = { "Home", "Clipboard", "Controls", "System", "Settings" };

    /// <summary>The saved order, cleaned up: unknown or repeated names dropped, missing tabs added at the end.</summary>
    public string[] TabOrderList()
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var raw in (TabOrder ?? "").Split(','))
        {
            var name = Array.Find(TabNames, n => string.Equals(n, raw.Trim(), StringComparison.OrdinalIgnoreCase));
            if (name != null && !list.Contains(name)) list.Add(name);
        }
        foreach (var n in TabNames) if (!list.Contains(n)) list.Add(n);
        return list.ToArray();
    }
    /// <summary>"Home", "Clipboard", "Controls", "System" or "Last".</summary>
    public string DefaultTab { get; set; } = "Last";
    /// <summary>When a song is on the island, opening it always lands on the music.</summary>
    public bool OpenOnActivity { get; set; } = true;
    public bool LiquidLens { get; set; } = true;
    public bool ScrollSwitchesTabs { get; set; } = true;
    /// <summary>1 (two notches per tab, slow) to 10 (every notch moves a tab, instantly).</summary>
    public double TabScrollSpeed { get; set; } = 7;

    // ── Sliders ──
    public bool ScrollAdjustsSliders { get; set; } = true;
    /// <summary>Percent per wheel notch on volume and brightness.</summary>
    public double SliderScrollStep { get; set; } = 2;

    // ── Notifications (peeks) ──
    public bool ClipboardPeek { get; set; } = true;
    public double ClipPeekSeconds { get; set; } = 1.3;
    public bool VolumePeek { get; set; } = true;
    public bool MicPeek { get; set; } = true;
    public int ClipHistory { get; set; } = 60;

    // ── Calls ──
    /// <summary>Mute, deafen and leave a Discord voice channel from the island.</summary>
    public bool DiscordControls { get; set; } = true;
    /// <summary>Announce Discord mute and deafen changes.</summary>
    public bool DiscordPeek { get; set; } = true;

    // ── Header ──
    public bool ShowClock { get; set; } = true;
    public bool Clock24h { get; set; }
    public bool ShowBattery { get; set; } = true;
    public bool ShowMicButton { get; set; } = true;

    // ── Motion ──
    public double MotionSpeed { get; set; } = 1.0;
    /// <summary>0 = no bounce (iOS "smooth"), up to 0.4 = playful.</summary>
    public double Bounce { get; set; } = 0.0;
    public bool ReduceMotion { get; set; }

    // ── Shortcuts ──
    public bool HotkeyOpen { get; set; } = true;
    public bool HotkeyMic { get; set; } = true;
    public bool HotkeyColor { get; set; } = true;
    public bool HotkeyClip { get; set; } = true;

    public event Action? Changed;

    private static readonly string FilePath = Path.Combine(App.DataDir, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Synced(JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings());
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return Synced(new Settings());
    }

    /// <summary>"Start with Windows" mirrors the real sign-in entry, which the installer may have set.</summary>
    private static Settings Synced(Settings s)
    {
        s.StartWithWindows = StartupService.IsEnabled();
        return s;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Save and tell everyone listening to re-apply.</summary>
    public void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    public void ResetToDefaults()
    {
        var d = new Settings();
        foreach (var p in typeof(Settings).GetProperties())
            if (p.CanWrite && p.Name != nameof(StartWithWindows))
                p.SetValue(this, p.GetValue(d));
        Commit();
    }
}
