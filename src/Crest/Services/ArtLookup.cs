using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Crest.Services;

/// <summary>
/// Finds a song's cover online when the music app doesn't give Windows one. Only the title and artist
/// are sent, to Apple's public iTunes search (no account, no key). A cover is used only when the
/// song's name matches, so a wrong picture is never shown.
/// </summary>
public static class ArtLookup
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Dictionary<string, BitmapSource?> Cache = new();

    public static async Task<BitmapSource?> FindAsync(string title, string artist)
    {
        string key = Clean(title) + "\u0001" + Clean(artist);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        BitmapSource? art = null;
        try
        {
            string firstArtist = artist.Split(new[] { ',', '&', ';' }, 2)[0].Trim();
            string term = Uri.EscapeDataString((Bare(title) + " " + firstArtist).Trim());
            string json = await Http.GetStringAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=10");
            using var doc = JsonDocument.Parse(json);
            string want = Clean(Bare(title));
            // The same song name wins; a longer or shorter name ("8 Parche Remix") only if nothing matches exactly.
            string? exact = null, close = null;
            foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                string name = r.TryGetProperty("trackName", out var n) ? n.GetString() ?? "" : "";
                if (!r.TryGetProperty("artworkUrl100", out var a) || a.GetString() is not { Length: > 0 } s) continue;
                string got = Clean(Bare(name));
                if (got.Length == 0) continue;
                if (got == want) { exact = s; break; }
                if (close == null && (got.StartsWith(want) || want.StartsWith(got))) close = s;
            }
            string? url = exact ?? close;
            if (url != null)
            {
                var bytes = await Http.GetByteArrayAsync(url.Replace("100x100bb", "600x600bb"));
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 256;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                art = bmp;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or NotSupportedException) { return null; }
        catch (Exception ex) { App.Log(ex); return null; }
        if (Cache.Count > 100) Cache.Clear();
        Cache[key] = art;
        return art;
    }

    /// <summary>"Song (feat. X) - Remastered" → "Song".</summary>
    private static string Bare(string t)
    {
        string b = Regex.Replace(t, @"\s*(\(|\[|\s-\s).*$", "").Trim();
        return b.Length > 0 ? b : t.Trim();
    }

    private static string Clean(string t) => new string(t.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
