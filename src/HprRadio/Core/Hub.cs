using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HprRadio.Core;

/// <summary>Shared state the screens read: catalog, player, song histories, cover art.</summary>
public sealed class Hub : IDisposable
{
    private const int PollSeconds = 20;

    public Settings Settings { get; }
    public Catalog Catalog { get; }
    public ProgressStore Progress { get; }
    public Player Player { get; }
    public ImageCache Images { get; } = new();
    public Dictionary<string, List<Track>> Histories { get; } = new();
    public bool RecentLoaded { get; private set; }

    /// <summary>Cover for what's playing now (live song or show), if found.</summary>
    public Art? NowArt { get; private set; }

    public event Action<string>? RecentChanged;   // station id
    public event Action? NowArtChanged;

    private readonly DispatcherTimer _poll;
    private readonly HashSet<string> _fetching = new();
    private string? _nowKey;

    public Hub(Settings settings)
    {
        Settings = settings;
        Catalog = new Catalog(settings.ConfigUrl);
        Progress = new ProgressStore();
        Player = new Player(settings, Progress, Catalog);
        Player.Changed += OnPlayerChanged;
        Catalog.Changed += RefreshRecent;
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(PollSeconds), DispatcherPriority.Normal, (_, _) => RefreshRecent(),
            Dispatcher.CurrentDispatcher);
    }

    public void Start()
    {
        _ = Catalog.RefreshAsync();
        RefreshRecent();
        _poll.Start();
    }

    // ---- song histories ------------------------------------------------------------------

    public void RefreshRecent()
    {
        foreach (var st in Catalog.Available)
        {
            if (st.RecentTracksUrl is null || !_fetching.Add(st.Id)) continue; // last request still out
            _ = FetchRecentAsync(st);
        }
    }

    private async Task FetchRecentAsync(Station st)
    {
        try
        {
            var tracks = await Core.Recent.FetchAsync(st);
            RecentLoaded = true;
            if (tracks is not null)
                Histories[st.Id] = Core.Recent.Merge(Histories.TryGetValue(st.Id, out var old) ? old : null, tracks);
            RecentChanged?.Invoke(st.Id);
            if (Player.IsLive && Player.Station?.Id == st.Id) OnPlayerChanged(); // song may have changed
        }
        finally { _fetching.Remove(st.Id); }
    }

    /// <summary>The song on the channel that's playing (stream title, else its song history).</summary>
    public (string? Artist, string? Title) NowSong =>
        Player.IsLive && Player.Station is { } st ? OnAir(st) : (null, null);

    /// <summary>(artist, title) on the air now: the stream's own title if we're listening to
    /// this channel, otherwise the top of its song history.</summary>
    public (string? Artist, string? Title) OnAir(Station st)
    {
        var p = Player;
        if (p.IsLive && p.Station?.Id == st.Id && p.SongTitle is not null) return (p.SongArtist, p.SongTitle);
        if (Histories.TryGetValue(st.Id, out var tracks) && tracks.Count > 0) return (tracks[0].Artist, tracks[0].Title);
        return (null, null);
    }

    // ---- cover art -----------------------------------------------------------------------

    private void OnPlayerChanged()
    {
        var p = Player;
        var (artist, title) = NowSong;
        var key = $"{p.IsLive}|{p.Station?.Id}|{artist}|{title}|{p.Show?.Id}";
        if (key == _nowKey) return;
        _nowKey = key;
        NowArt = null;
        NowArtChanged?.Invoke();
        if (p.IsEpisode && p.Show?.ImageUrl is { } img)
        {
            NowArt = new Art(img, p.Show.Title);
            NowArtChanged?.Invoke();
        }
        else if (p.IsLive && title is not null)
            _ = LookUpNowArtAsync(key, artist, title);
    }

    private async Task LookUpNowArtAsync(string key, string? artist, string title)
    {
        var art = await Artwork.FindAsync(artist, title);
        if (key != _nowKey) return;
        NowArt = art;
        NowArtChanged?.Invoke();
    }

    public void Dispose()
    {
        _poll.Stop();
        Player.Dispose();
    }
}

/// <summary>Downloads images once, keeps them on disk and the recent ones in memory.</summary>
public sealed class ImageCache
{
    private const int MemoryMax = 200;
    private readonly Dictionary<string, BitmapSource> _memory = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, Task<BitmapSource?>> _inFlight = new();
    private readonly Dictionary<string, long> _failedUntil = new();   // url -> TickCount64; don't retry for a minute

    public static string DiskPath(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(Paths.Art, hash + ".img");
    }

    /// <summary>The image, decoded at (about) the given pixel width. Null if it can't be had.</summary>
    public Task<BitmapSource?> GetAsync(string url, int pixelWidth)
    {
        var key = $"{pixelWidth}|{url}";
        if (_memory.TryGetValue(key, out var hit))
        {
            _order.Remove(key);
            _order.AddLast(key);
            return Task.FromResult<BitmapSource?>(hit);
        }
        if (_inFlight.TryGetValue(key, out var running)) return running;
        if (_failedUntil.TryGetValue(url, out var until) && Environment.TickCount64 < until)
            return Task.FromResult<BitmapSource?>(null);
        var task = LoadAsync(key, url, pixelWidth);
        _inFlight[key] = task;
        return task;
    }

    private async Task<BitmapSource?> LoadAsync(string key, string url, int pixelWidth)
    {
        try
        {
            var file = DiskPath(url);
            byte[]? bytes = null;
            try { if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file); } catch { }
            if (bytes is null)
            {
                bytes = await Net.GetArtBytesAsync(url);
                if (bytes is null)
                {
                    _failedUntil[url] = Environment.TickCount64 + 60_000;
                    return null;
                }
                try { Directory.CreateDirectory(Paths.Art); await File.WriteAllBytesAsync(file, bytes); } catch { }
            }
            var data = bytes;
            var bmp = await Task.Run(() =>
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bi.DecodePixelWidth = pixelWidth;
                    bi.StreamSource = new MemoryStream(data);
                    bi.EndInit();
                    bi.Freeze();
                    return (BitmapSource?)bi;
                }
                catch { return null; }
            });
            if (bmp is null)
            {
                try { File.Delete(file); } catch { }
                return null;
            }
            _memory[key] = bmp;
            _order.AddLast(key);
            while (_order.Count > MemoryMax)
            {
                _memory.Remove(_order.First!.Value);
                _order.RemoveFirst();
            }
            return bmp;
        }
        finally { _inFlight.Remove(key); }
    }
}

/// <summary>Keeps Windows from going to sleep while something's playing.</summary>
public static class SleepBlocker
{
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private static bool _on;

    public static void Set(bool on)
    {
        if (on == _on) return;
        _on = on;
        SetThreadExecutionState(on ? EsContinuous | EsSystemRequired : EsContinuous);
    }
}
