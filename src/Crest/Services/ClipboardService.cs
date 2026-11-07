using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Crest.Services;

public sealed class ClipItem : INotifyPropertyChanged
{
    private bool _pinned;

    public string Text { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;

    public bool Pinned
    {
        get => _pinned;
        set { _pinned = value; Raise(); Raise(nameof(PinGlyph)); }
    }

    public string Preview => Text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
    public string PinGlyph => Pinned ? "" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Text clipboard history that survives restarts, encrypted with DPAPI for the
/// current Windows account. Skips anything a password manager marks as private.
/// </summary>
public sealed class ClipboardService
{
    public static int MaxUnpinned { get; set; } = 60;
    private static readonly string FilePath = Path.Combine(App.DataDir, "clipboard.bin");
    private bool _ignoreNext;

    public ObservableCollection<ClipItem> Items { get; } = new();
    public event Action<string>? Copied;

    public ClipboardService()
    {
        Load();
    }

    /// <summary>Called on WM_CLIPBOARDUPDATE (posted back onto the UI thread).</summary>
    public void OnClipboardUpdate()
    {
        string? text = null;
        for (int attempt = 0; attempt < 4 && text == null; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return;
                if (IsPrivate(data)) return;
                if (!data.GetDataPresent(DataFormats.UnicodeText)) return;
                text = data.GetData(DataFormats.UnicodeText) as string;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(25); // clipboard is briefly locked by the writer
            }
            catch
            {
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > 20000) text = text[..20000];

        bool own = _ignoreNext;
        _ignoreNext = false;

        var existing = Items.FirstOrDefault(i => i.Text == text);
        if (existing != null)
        {
            if (!existing.Pinned)
            {
                Items.Remove(existing);
                Items.Insert(PinnedCount(), existing);
            }
            existing.Time = DateTime.Now;
        }
        else
        {
            Items.Insert(PinnedCount(), new ClipItem { Text = text });
            var extra = Items.Where(i => !i.Pinned).Skip(MaxUnpinned).ToList();
            foreach (var e in extra) Items.Remove(e);
        }

        Save();
        if (!own) Copied?.Invoke(text);
    }

    private static bool IsPrivate(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return true;
        if (data.GetDataPresent("Clipboard Viewer Ignore")) return true;
        foreach (var fmt in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            if (!data.GetDataPresent(fmt)) continue;
            if (data.GetData(fmt) is MemoryStream ms && ms.Length >= 4)
            {
                var buf = new byte[4];
                ms.Position = 0;
                ms.Read(buf, 0, 4);
                if (BitConverter.ToInt32(buf, 0) == 0) return true;
            }
        }
        return false;
    }

    private int PinnedCount() => Items.Count(i => i.Pinned);

    public void Copy(ClipItem item)
    {
        try
        {
            _ignoreNext = true;
            Clipboard.SetDataObject(item.Text, true);
        }
        catch
        {
            _ignoreNext = false;
        }
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Items.Remove(item);
        Items.Insert(item.Pinned ? 0 : PinnedCount(), item);
        Save();
    }

    public void ClearUnpinned()
    {
        foreach (var i in Items.Where(i => !i.Pinned).ToList()) Items.Remove(i);
        Save();
    }

    private sealed record Stored(string Text, bool Pinned, DateTime Time);

    private void Save()
    {
        try
        {
            var list = Items.Select(i => new Stored(i.Text, i.Pinned, i.Time)).ToList();
            var json = JsonSerializer.SerializeToUtf8Bytes(list);
            var enc = ProtectedData.Protect(json, Encoding.UTF8.GetBytes("Crest.Clipboard"), DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, enc);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Encoding.UTF8.GetBytes("Crest.Clipboard"), DataProtectionScope.CurrentUser);
            var list = JsonSerializer.Deserialize<List<Stored>>(json) ?? new();
            foreach (var s in list.OrderByDescending(s => s.Pinned))
                Items.Add(new ClipItem { Text = s.Text, Pinned = s.Pinned, Time = s.Time });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
