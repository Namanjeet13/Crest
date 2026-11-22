using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Crest.Services;

/// <summary>
/// Real download progress from the browser. The Crest Downloads extension reads the browser's own
/// download list and sends it here through native messaging: the browser starts Crest.exe as a small
/// headless "host" (see <see cref="RunHost"/>), which passes each message over a local pipe to the
/// running Crest. Only byte counts travel, never file names or addresses, and nothing leaves the PC.
/// </summary>
public static class BrowserDownloads
{
    public const string HostName = "com.crest.downloads";
    private const string PipeName = "Crest.Downloads";
    private const string GeckoId = "downloads@crest-island";

    /// <summary>
    /// Extensions allowed to talk to Crest. The first is the unpacked developer build (its ID is fixed by
    /// the "key" in extension/manifest.json); the store builds get their own IDs, added here once listed.
    /// </summary>
    private static readonly string[] ChromiumIds =
    {
        "meipglhlgplpipaicfekblgabekmeogh",
    };

    private static readonly string[] ChromiumKeys =
    {
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
        @"Software\Vivaldi\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
    };
    private const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts";

    // ----------------------------- inside Crest -----------------------------

    private sealed record Source(long At, long Received, long Total, int Count, int Unknown);
    private static readonly Dictionary<int, Source> Sources = new();
    private static long _doneAt;
    private static int _nextId;

    /// <summary>What the browsers are downloading right now, summed over every connected browser.</summary>
    public readonly record struct State(int Count, long Received, long Total, int Unknown, long DoneAt)
    {
        /// <summary>Received over total, for the downloads whose size is known; -1 if none is.</summary>
        public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : -1;
    }

    public static State Now
    {
        get
        {
            long now = Environment.TickCount64;
            lock (Sources)
            {
                int n = 0, unknown = 0; long got = 0, total = 0;
                foreach (var s in Sources.Values)
                {
                    if (now - s.At > 3000) continue; // a browser that stopped talking
                    n += s.Count; unknown += s.Unknown; got += s.Received; total += s.Total;
                }
                return new State(n, got, total, unknown, _doneAt);
            }
        }
    }

    /// <summary>Registers the host with each browser and starts listening. Safe to call once at startup.</summary>
    public static void Start()
    {
        Task.Run(() =>
        {
            try { Register(); } catch (Exception ex) { App.Log(ex); }
        });
        var t = new Thread(Listen) { IsBackground = true, Name = "Crest downloads pipe" };
        t.Start();
    }

    private static void Listen()
    {
        while (true)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                var p = pipe; pipe = null;
                Task.Run(() => Serve(p));
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                App.Log(ex);
                Thread.Sleep(2000);
            }
        }
    }

    private static void Serve(NamedPipeServerStream pipe)
    {
        int id = Interlocked.Increment(ref _nextId);
        try
        {
            using var r = new StreamReader(pipe, Encoding.UTF8);
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Length > 1 << 20) break;
                try { Take(id, line); } catch (JsonException) { }
            }
        }
        catch (IOException) { }
        finally
        {
            lock (Sources) Sources.Remove(id);
            pipe.Dispose();
        }
    }

    private static void Take(int id, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("downloads", out var list) || list.ValueKind != JsonValueKind.Array) return;
        int n = 0, unknown = 0; long got = 0, total = 0;
        foreach (var d in list.EnumerateArray())
        {
            if (n++ > 500) break;
            long rec = Num(d, "received"), tot = Num(d, "total");
            if (tot > 0) { got += Math.Min(rec, tot); total += tot; }
            else unknown++;
        }
        long now = Environment.TickCount64;
        lock (Sources)
        {
            Sources[id] = new Source(now, got, total, n, unknown);
            if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.Number && done.GetInt32() > 0) _doneAt = now;
        }

        static long Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? Math.Max(0, x) : 0;
    }

    /// <summary>Writes the host manifests to %APPDATA%\Crest\native and points each browser at them.</summary>
    private static void Register()
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Crest.exe");
        string dir = Path.Combine(App.DataDir, "native");
        Directory.CreateDirectory(dir);
        var o = new JsonSerializerOptions { WriteIndented = true };
        string chromium = Path.Combine(dir, "chromium.json"), firefox = Path.Combine(dir, "firefox.json");
        WriteIfChanged(chromium, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = HostName,
            ["description"] = "Crest island: download progress",
            ["path"] = exe,
            ["type"] = "stdio",
            ["allowed_origins"] = ChromiumIds.Select(i => $"chrome-extension://{i}/").ToArray(),
        }, o));
        WriteIfChanged(firefox, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = HostName,
            ["description"] = "Crest island: download progress",
            ["path"] = exe,
            ["type"] = "stdio",
            ["allowed_extensions"] = new[] { GeckoId },
        }, o));
        foreach (var k in ChromiumKeys) Point(k, chromium);
        Point(FirefoxKey, firefox);

        static void Point(string parent, string file)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{parent}\{HostName}");
            if (key.GetValue(null) as string != file) key.SetValue(null, file);
        }
        static void WriteIfChanged(string path, string text)
        {
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
            File.WriteAllText(path, text);
        }
    }

    /// <summary>Removes what <see cref="Register"/> added. Used by the uninstaller.</summary>
    public static void Unregister()
    {
        foreach (var k in ChromiumKeys.Append(FirefoxKey))
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{k}\{HostName}", false); } catch { }
    }

    // ----------------------------- as the browser's host -----------------------------

    /// <summary>
    /// True when a browser started Crest.exe for the extension. Chromium passes the caller's origin
    /// (chrome-extension://id/); Firefox passes the manifest path and the extension's ID.
    /// </summary>
    public static bool IsHostLaunch(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) || a == GeckoId);

    /// <summary>
    /// Runs as the browser's native host: reads length-prefixed JSON from stdin and passes each message
    /// to the running Crest as one line. Answers each message with whether Crest is running. Exits when
    /// the browser closes stdin.
    /// </summary>
    public static int RunHost()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        NamedPipeClientStream? pipe = null;
        StreamWriter? w = null;
        bool? told = null;
        var len = new byte[4];
        try
        {
            while (ReadExactly(stdin, len))
            {
                int n = BitConverter.ToInt32(len, 0);
                if (n <= 0 || n > 1 << 20) return 1;
                var buf = new byte[n];
                if (!ReadExactly(stdin, buf)) return 0;
                string msg = Encoding.UTF8.GetString(buf).Replace('\r', ' ').Replace('\n', ' ');

                bool up = false;
                for (int attempt = 0; attempt < 2 && !up; attempt++)
                {
                    try
                    {
                        if (pipe is not { IsConnected: true })
                        {
                            w?.Dispose(); pipe?.Dispose();
                            pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                            pipe.Connect(300);
                            w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                        }
                        w!.WriteLine(msg);
                        up = true;
                    }
                    catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
                    {
                        w?.Dispose(); pipe?.Dispose(); w = null; pipe = null;
                    }
                }

                // Reply on the first message, to a ping, and whenever Crest starts or stops.
                if (told != up || msg.Contains("\"ping\""))
                {
                    told = up;
                    var reply = Encoding.UTF8.GetBytes($"{{\"crest\":{(up ? "true" : "false")},\"version\":\"{typeof(BrowserDownloads).Assembly.GetName().Version?.ToString(3)}\"}}");
                    stdout.Write(BitConverter.GetBytes(reply.Length));
                    stdout.Write(reply);
                    stdout.Flush();
                }
            }
            return 0;
        }
        catch (IOException) { return 0; }
        finally { w?.Dispose(); pipe?.Dispose(); }

        static bool ReadExactly(Stream s, byte[] b)
        {
            int got = 0;
            while (got < b.Length)
            {
                int r = s.Read(b, got, b.Length - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }
    }
}
