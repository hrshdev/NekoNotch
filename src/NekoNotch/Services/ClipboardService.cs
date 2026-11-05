using System.Collections.ObjectModel;
using NekoNotch.Shell;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NekoNotch.Interop;

namespace NekoNotch.Services;

public enum ClipKind
{
    Text,
    Link,
    Image,
    Files,
}

public sealed class ClipItem
{
    private ImageSource? _thumb;

    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string? ImagePath { get; set; }
    public List<string>? Files { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }

    /// <summary>Link cards: "github.com" + "/user/repo".</summary>
    [JsonIgnore]
    public string LinkHost => Kind == ClipKind.Link && Uri.TryCreate(Text?.Trim(), UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "";

    [JsonIgnore]
    public string LinkPath => Kind == ClipKind.Link && Uri.TryCreate(Text?.Trim(), UriKind.Absolute, out var u) ? u.PathAndQuery.TrimEnd('/') : "";

    [JsonIgnore]
    public string Preview
    {
        get
        {
            var s = Kind switch
            {
                ClipKind.Files => string.Join(", ", Files?.Select(Path.GetFileName) ?? []),
                ClipKind.Image => "Image",
                _ => (Text ?? "").Trim().Replace("\r", " ").Replace("\n", " ").Replace("\t", " "),
            };
            return s.Length > 240 ? s[..240] + "…" : s;
        }
    }

    [JsonIgnore]
    public AppIcon Icon => Kind switch
    {
        ClipKind.Link => AppIcon.Link,
        ClipKind.Image => AppIcon.Photo,
        ClipKind.Files => AppIcon.Folder,
        _ => AppIcon.DocText,
    };

    [JsonIgnore]
    public string TimeText
    {
        get
        {
            var d = DateTime.Now - Time;
            if (d.TotalMinutes < 1) return "now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours}h";
            return $"{(int)d.TotalDays}d";
        }
    }

    [JsonIgnore]
    public bool HasThumb => Kind == ClipKind.Image && ImagePath is not null && File.Exists(ImagePath);

    [JsonIgnore]
    public ImageSource? Thumb
    {
        get
        {
            if (_thumb is not null || !HasThumb) return _thumb;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelHeight = 196;
                bmp.UriSource = new Uri(ImagePath!);
                bmp.EndInit();
                bmp.Freeze();
                _thumb = bmp;
            }
            catch
            {
                // Missing or corrupt image file.
            }
            return _thumb;
        }
    }
}

/// <summary>Records clipboard history via AddClipboardFormatListener.</summary>
public sealed class ClipboardService
{
    private const string FileName = "clipboard.json";

    // Formats password managers and other apps set to opt out of clipboard monitoring.
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";
    private const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    private DateTime _ignoreUntil;

    public static ClipboardService Instance { get; } = new();

    public ObservableCollection<ClipItem> Items { get; }

    private ClipboardService()
    {
        var raw = Storage.Load<List<ClipItem>>(FileName);
        var deduped = new List<ClipItem>();
        foreach (var item in raw)
        {
            var isDup = deduped.Any(x =>
                x.Kind == item.Kind &&
                ((x.Kind == ClipKind.Image && x.ImagePath != null && item.ImagePath != null &&
                  File.Exists(x.ImagePath) && File.Exists(item.ImagePath) &&
                  (x.ImagePath.Equals(item.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                   (new FileInfo(x.ImagePath).Length == new FileInfo(item.ImagePath).Length && FilesMatch(x.ImagePath, item.ImagePath)))) ||
                 (x.Kind != ClipKind.Image && x.Text == item.Text && (x.Files ?? []).SequenceEqual(item.Files ?? []))));

            if (!isDup) deduped.Add(item);
            else if (item.Kind == ClipKind.Image && item.ImagePath != null && File.Exists(item.ImagePath))
            {
                // Delete duplicate image file from disk
                try { File.Delete(item.ImagePath); } catch { }
            }
        }
        Items = new ObservableCollection<ClipItem>(deduped);
    }

    private static bool FilesMatch(string path1, string path2)
    {
        try
        {
            var info1 = new FileInfo(path1);
            var info2 = new FileInfo(path2);
            if (info1.Length != info2.Length) return false;
            using var s1 = File.OpenRead(path1);
            using var s2 = File.OpenRead(path2);
            var b1 = new byte[4096];
            var b2 = new byte[4096];
            int read;
            while ((read = s1.Read(b1, 0, b1.Length)) > 0)
            {
                if (s2.Read(b2, 0, b2.Length) != read) return false;
                for (var i = 0; i < read; i++) if (b1[i] != b2[i]) return false;
            }
            return true;
        }
        catch { return false; }
    }

    public void Attach(IntPtr hwnd) => Win32.AddClipboardFormatListener(hwnd);

    public void OnClipboardUpdate()
    {
        if (DateTime.Now < _ignoreUntil || !App.Settings.ClipboardEnabled) return;
        Application.Current.Dispatcher.BeginInvoke(CaptureWithRetry, DispatcherPriority.Background);
    }

    private async void CaptureWithRetry()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Capture();
                return;
            }
            catch (COMException)
            {
                // Another process still holds the clipboard open.
                await Task.Delay(80);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                return;
            }
        }
    }

    private void Capture()
    {
        var data = Clipboard.GetDataObject();
        if (data is null) return;
        if (App.Settings.ClipboardSkipSensitive && IsSensitive(data)) return;

        ClipItem? item = null;
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            item = new ClipItem { Kind = ClipKind.Files, Files = files.ToList() };
        }
        else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var trimmed = text.Trim();
            var isLink = !trimmed.Contains(' ') && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            item = new ClipItem { Kind = isLink ? ClipKind.Link : ClipKind.Text, Text = text };
        }
        else if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image is null) return;
            var path = Path.Combine(Storage.Dir("clipboard"), $"{Guid.NewGuid():N}.png");
            using (var fs = File.Create(path))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(fs);
            }
            item = new ClipItem { Kind = ClipKind.Image, ImagePath = path };
        }

        if (item is null) return;

        ClipItem? duplicate = null;
        if (item.Kind == ClipKind.Image && item.ImagePath is not null && File.Exists(item.ImagePath))
        {
            var newLen = new FileInfo(item.ImagePath).Length;
            duplicate = Items.FirstOrDefault(x => x.Kind == ClipKind.Image &&
                                                  x.ImagePath is not null && File.Exists(x.ImagePath) &&
                                                  new FileInfo(x.ImagePath).Length == newLen &&
                                                  FilesMatch(x.ImagePath, item.ImagePath));
        }
        else
        {
            duplicate = Items.FirstOrDefault(x => x.Kind == item.Kind &&
                                                  x.Text == item.Text &&
                                                  (x.Files ?? []).SequenceEqual(item.Files ?? []));
        }

        if (duplicate is not null)
        {
            if (item.Kind == ClipKind.Image && item.ImagePath is not null)
            {
                try { File.Delete(item.ImagePath); } catch { }
                item.ImagePath = duplicate.ImagePath;
            }
            Items.Remove(duplicate);
        }

        Items.Insert(0, item);
        Trim();
        Save();
    }

    private static bool IsSensitive(IDataObject data)
    {
        if (data.GetDataPresent(ExcludeFormat) || data.GetDataPresent(ViewerIgnoreFormat)) return true;
        if (data.GetDataPresent(HistoryFormat) && data.GetData(HistoryFormat) is MemoryStream ms && ms.Length >= 4)
        {
            var buffer = new byte[4];
            ms.Position = 0;
            ms.ReadExactly(buffer);
            return BitConverter.ToInt32(buffer) == 0;
        }
        return false;
    }

    private void Trim()
    {
        while (Items.Count(i => !i.Pinned) > Math.Max(10, App.Settings.ClipboardMaxItems))
        {
            var last = Items.Last(i => !i.Pinned);
            Items.Remove(last);
            DeleteImage(last);
        }
    }

    public void Recopy(ClipItem item)
    {
        _ignoreUntil = DateTime.Now.AddMilliseconds(500);
        try
        {
            switch (item.Kind)
            {
                case ClipKind.Files when item.Files is not null:
                    var list = new StringCollection();
                    list.AddRange(item.Files.ToArray());
                    Clipboard.SetFileDropList(list);
                    break;
                case ClipKind.Image when item.ImagePath is not null && File.Exists(item.ImagePath):
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(item.ImagePath);
                    bmp.EndInit();
                    Clipboard.SetImage(bmp);
                    break;
                default:
                    Clipboard.SetText(item.Text ?? "");
                    break;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return;
        }

        var index = Items.IndexOf(item);
        if (index > 0) Items.Move(index, 0);
        item.Time = DateTime.Now;
        Save();
    }

    /// <summary>Copies text without recording it in history (used by Emoji, Converter, …).</summary>
    public void CopyQuiet(string text)
    {
        _ignoreUntil = DateTime.Now.AddMilliseconds(500);
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    public void Remove(ClipItem item)
    {
        Items.Remove(item);
        DeleteImage(item);
        Save();
    }

    /// <summary>Removes everything except pinned clips.</summary>
    public void Clear()
    {
        foreach (var item in Items.Where(i => !i.Pinned).ToList())
        {
            Items.Remove(item);
            DeleteImage(item);
        }
        Save();
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Save();
    }

    private static void DeleteImage(ClipItem item)
    {
        if (item.ImagePath is null) return;
        try
        {
            File.Delete(item.ImagePath);
        }
        catch
        {
            // Ignore locked files.
        }
    }

    private void Save() => Storage.Save(FileName, Items.ToList());
}
