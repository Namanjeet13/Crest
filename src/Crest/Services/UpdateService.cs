using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Crest.Services;

/// <summary>
/// Updates from Crest's GitHub releases. The only network traffic Crest makes: one small request to
/// GitHub's API to read the latest release, and, when you choose to install, the installer download.
/// The installer is only run if its SHA-256 matches the checksum GitHub publishes for that file.
/// </summary>
public static class UpdateService
{
    private const string Repo = "Namanjeet13/Crest";
    private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    private const long MaxSize = 400L * 1024 * 1024;
    private static readonly Regex AssetName = new(@"^CrestSetup-\d+(\.\d+){1,3}\.exe$", RegexOptions.CultureInvariant);

    public sealed record Release(Version Version, string Url, string Sha256, long Size, string Page);

    public static Version Current { get; } = Normalize(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));
    /// <summary>The newer release found by the last check, or null.</summary>
    public static Release? Available { get; private set; }
    public static DateTime LastChecked { get; private set; }
    public static event Action? Changed;

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Crest-Updater/" + Current.ToString(3));
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Asks GitHub for the latest release. Returns the newer release, or null when up to date. Throws on network errors.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await Http.GetAsync(LatestApi, cts.Token);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            Release? found = null;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            bool draft = root.TryGetProperty("draft", out var d) && d.GetBoolean();
            bool pre = root.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            if (!draft && !pre && Version.TryParse(tag.TrimStart('v', 'V'), out var v) && Normalize(v) > Current
                && root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    string digest = a.TryGetProperty("digest", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "";
                    long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (!AssetName.IsMatch(name) || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                    // No published checksum, no update: the file could not be verified.
                    if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 7 + 64) continue;
                    if (size <= 0 || size > MaxSize) continue;
                    string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
                    found = new Release(Normalize(v), url, digest[7..].ToLowerInvariant(), size, page);
                    break;
                }
            }
            Available = found;
            LastChecked = DateTime.Now;
            Changed?.Invoke();
            return found;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Removes the downloads of earlier updates (the installer can't delete itself while it runs).</summary>
    public static void CleanUp()
    {
        try
        {
            foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "Crest-update-*"))
                if (Directory.GetLastWriteTime(d) < DateTime.Now.AddHours(-1))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// Downloads the installer, checks its size and SHA-256, then runs it in update mode. The installer
    /// closes Crest, replaces it with the same choices (shortcuts, start with Windows) and opens it again.
    /// </summary>
    public static async Task InstallAsync(Release r, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "Crest-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "CrestSetup-" + r.Version.ToString(3) + ".exe");
        try
        {
            using (var resp = await Http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                if (resp.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("The download did not stay on HTTPS.");
                using var src = await resp.Content.ReadAsStreamAsync(ct);
                using var dst = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    done += read;
                    if (done > r.Size) throw new InvalidOperationException("The download is larger than expected.");
                    sha.AppendData(buf, 0, read);
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    progress?.Report((double)done / r.Size);
                }
                if (done != r.Size) throw new InvalidOperationException("The download is incomplete.");
                string got = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(got), System.Text.Encoding.ASCII.GetBytes(r.Sha256)))
                    throw new InvalidOperationException("The download's checksum doesn't match the release. Nothing was installed.");
            }
            Process.Start(new ProcessStartInfo(file, "/update") { UseShellExecute = false, WorkingDirectory = dir });
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch { }
            throw;
        }
    }
}
