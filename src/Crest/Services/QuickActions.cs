using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace Crest.Services;

/// <summary>What the two buttons on the Controls tab can do.</summary>
public sealed record QuickAction(string Name, string Label, string Glyph)
{
    public const string Custom = "Your own";

    public static readonly QuickAction[] All =
    {
        new("Snip", "Snip", "\uE7A8"),
        new("Colour", "Colour", "\uE790"),
        new("Calculator", "Calc", "\uE8EF"),
        new("Files", "Files", "\uE8B7"),
        new("Task Manager", "Tasks", "\uE9D9"),
        new("Settings", "Settings", "\uE713"),
        new("Lock", "Lock", "\uE72E"),
        new("Clipboard", "Clipboard", "\uE77F"),
        new(Custom, "Open", "\uE8A7"),
    };

    public static QuickAction Find(string name) =>
        Array.Find(All, a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>The button's caption and icon for your own app or website: "youtube", "Notepad"…</summary>
    public static (string Label, string Glyph) ForTarget(string target)
    {
        string t = target.Trim();
        if (t.Length == 0) return ("Open", "\uE8A7");
        bool site = Launcher.IsWebsite(t);
        string name;
        if (site)
        {
            string host = t.Contains("://") ? new Uri(Launcher.AsUrl(t)).Host : t.Split('/')[0];
            if (host.StartsWith("www.")) host = host[4..];
            var parts = host.Split('.');
            name = parts.Length >= 3 && parts[0] is "music" or "open" or "web" or "app" ? parts[1] : parts[0];
        }
        else name = Path.GetFileNameWithoutExtension(t.Trim('"'));
        if (name.Length == 0) name = "Open";
        name = char.ToUpperInvariant(name[0]) + name[1..];
        if (name.Length > 9) name = name[..8] + "…";
        return (name, site ? "\uE774" : "\uE8A7");
    }
}

/// <summary>Opens apps, files and websites the way Run (Win+R) would.</summary>
public static class Launcher
{
    /// <summary>"youtube.com" or "https://…" is a website; "notepad" or "C:\…\app.exe" is not.</summary>
    public static bool IsWebsite(string t)
    {
        t = t.Trim();
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains('\\') || t.Contains(':') || t.Contains(' ')) return false;
        if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        return t.Contains('.');
    }

    public static string AsUrl(string t) => t.Contains("://") ? t : "https://" + t;

    public static bool Open(string target)
    {
        string t = target.Trim();
        if (t.Length == 0) return false;
        try
        {
            if (IsWebsite(t)) t = AsUrl(t);
            Process.Start(new ProcessStartInfo(t.Trim('"')) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }
}

/// <summary>A keyboard shortcut written as text, like "Ctrl+Alt+N".</summary>
public static class HotkeyText
{
    public static bool TryParse(string text, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None;
        key = Key.None;
        foreach (var raw in (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, true, out key)) return false;
                    break;
            }
        }
        return key != Key.None && (mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
    }

    public static string Format(ModifierKeys mods, Key key)
    {
        string k = key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString()
                 : key is >= Key.NumPad0 and <= Key.NumPad9 ? "NumPad" + (key - Key.NumPad0)
                 : key.ToString();
        string s = "";
        if (mods.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
        if (mods.HasFlag(ModifierKeys.Alt)) s += "Alt+";
        if (mods.HasFlag(ModifierKeys.Shift)) s += "Shift+";
        if (mods.HasFlag(ModifierKeys.Windows)) s += "Win+";
        return s + k;
    }

    /// <summary>The same shortcut however it was typed, for comparing two of them.</summary>
    public static string Normal(string text) => TryParse(text, out var m, out var k) ? Format(m, k) : text;
}
