using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace HprRadio.Core;

public enum PlayStatus { Stopped, Connecting, Playing, Paused, Reconnecting, Error }

/// <summary>
/// Plays live channels and on-demand episodes with VLC's engine.
///
/// Live streams get the same treatment as the Android and Debian apps: when the server drops
/// the connection or the network goes away, it reconnects with a back-off (forever, for live
/// radio); an address that won't start is tried twice, then the next address is tried. The
/// address that works is remembered per channel.
///
/// Every VLC call runs on one worker thread, so a slow server never freezes the window, and
/// VLC's events are passed back to the UI thread. Each start gets a generation number so a late
/// event from the previous stream can't be mistaken for one from the new stream.
/// </summary>
public sealed class Player : IDisposable
{
    private const int EpisodeRetries = 5;
    private const int StartTimeoutSeconds = 25;   // no audio this long after starting: try again
    private const int StallSeconds = 15;          // playing, but the clock stopped this long: reconnect

    private readonly Settings _settings;
    private readonly ProgressStore _progress;
    private readonly Catalog _catalog;
    private readonly Dispatcher _ui;
    private readonly VlcThread _vlc = new();
    private LibVLC? _libvlc;
    private VlcPlayer? _mp;
    private Media? _media;

    private int _gen;                    // UI side: the current start
    private volatile int _activeGen;     // VLC side: the start whose events are coming in
    private long _timeMs;                // updated straight from VLC's thread
    private long _lengthMs;
    private long _lastTick;              // Environment.TickCount64 of the last clock movement

    private int _streamIndex;
    private int _failuresOnUrl;
    private int _consecutiveFailures;
    private bool _playedThisAttempt;
    private long _attemptStarted;
    private double? _pendingSeek;
    private double _lastPosition;

    private readonly DispatcherTimer _retryTimer;
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _progressTimer;

    public Station? Station { get; private set; }
    public Show? Show { get; private set; }
    public Episode? Episode { get; private set; }
    public PlayStatus Status { get; private set; } = PlayStatus.Stopped;
    public string? SongTitle { get; private set; }
    public string? SongArtist { get; private set; }
    public double Volume { get; private set; }   // gain 0..1 (the slider shows its cube root)
    public bool Muted { get; private set; }

    /// <summary>Anything the screens show may have changed. Always raised on the UI thread.</summary>
    public event Action? Changed;
    /// <summary>The position jumped (seek or skip), in seconds.</summary>
    public event Action<double>? Seeked;

    public bool IsLive => Episode is null && Station is not null;
    public bool IsEpisode => Episode is not null;
    public bool HasItem => Station is not null || Episode is not null;
    public bool IsActive => Status is PlayStatus.Connecting or PlayStatus.Playing or PlayStatus.Reconnecting;

    public Player(Settings settings, ProgressStore progress, Catalog catalog)
    {
        _settings = settings;
        _progress = progress;
        _catalog = catalog;
        _ui = Dispatcher.CurrentDispatcher;
        Volume = Math.Clamp(settings.GetDouble("volume", Math.Pow(0.7, 3)), 0, 1); // first run: 70% on the slider
        Muted = settings.GetBool("muted", false);

        _retryTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui);
        _retryTimer.Tick += (_, _) => { _retryTimer.Stop(); Retry(); };
        _watchdog = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, (_, _) => Watch(), _ui);
        _progressTimer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Normal, (_, _) => SaveEpisodeProgress(), _ui);
        _progressTimer.Stop();

        _vlc.Run(InitVlc);
    }

    private void InitVlc()
    {
        LibVLCSharp.Shared.Core.Initialize();
        var options = new List<string> { "--no-video", "--no-sub-autodetect-file", "--no-stats" };
        // HPR_AUDIO=dummy plays silently (automated tests on machines without a sound card).
        if (Environment.GetEnvironmentVariable("HPR_AUDIO") is { Length: > 0 } aout)
            options.Add(aout == "dummy" ? "--aout=adummy" : "--aout=" + aout);
        _libvlc = new LibVLC(options.ToArray());
        if (Log.FilePath is not null)
        {
            // HPR_VLC_MODULES=1 also logs which VLC components get used (to keep only those).
            var modules = Environment.GetEnvironmentVariable("HPR_VLC_MODULES") is { Length: > 0 };
            _libvlc.Log += (_, e) =>
            {
                if (e.Level >= LogLevel.Error) Log.Write($"vlc {e.Module}: {e.Message}");
                else if (modules && (e.Message.StartsWith("using ") || e.Message.Contains("module \""))) Log.Write($"vlcmod {e.Module}: {e.Message}");
            };
        }
        _mp = new VlcPlayer(_libvlc);
        _mp.Playing += (_, _) => Post(OnPlaying);
        _mp.EndReached += (_, _) => Post(() => OnFailure("ended"));
        _mp.EncounteredError += (_, _) => Post(() => OnFailure("error"));
        _mp.TimeChanged += (_, e) =>
        {
            if (Interlocked.Exchange(ref _timeMs, e.Time) != e.Time) Interlocked.Exchange(ref _lastTick, Environment.TickCount64);
        };
        _mp.LengthChanged += (_, e) => Interlocked.Exchange(ref _lengthMs, e.Length);
        Log.Write("vlc ready " + _libvlc.Version);
    }

    /// <summary>Runs on the UI thread, but only if no newer stream has started since.</summary>
    private void Post(Action action)
    {
        var g = _activeGen;
        _ui.InvokeAsync(() => { if (g == _gen) action(); });
    }

    // ---- live channels ---------------------------------------------------------------------

    public void PlayStation(Station station)
    {
        if (!station.Available) return;
        if (Station?.Id == station.Id && Episode is null && IsActive) return;
        SaveEpisodeProgress();
        Reset();
        Station = station;
        Show = null;
        Episode = null;
        SongTitle = SongArtist = null;
        var idx = _settings.GetStreamIndex(station.Id);
        _streamIndex = idx >= 0 && idx < station.Streams.Count ? idx : 0;
        _settings.Set("last_station", station.Id);
        Start(station.Streams[_streamIndex], PlayStatus.Connecting);
    }

    // ---- episodes ---------------------------------------------------------------------------

    public void PlayEpisode(Show show, Episode episode, bool fromStart = false)
    {
        if (fromStart) _progress.Clear(episode.Key);
        var same = Episode?.Key == episode.Key;
        if (same && !fromStart && Status == PlayStatus.Paused) { Resume(); return; }
        if (same && !fromStart && IsActive) return;
        SaveEpisodeProgress();
        Reset();
        Station = null;
        Show = show;
        Episode = episode;
        SongTitle = SongArtist = null;
        var resume = _progress.ResumePosition(episode.Key);
        _lastPosition = resume;
        Interlocked.Exchange(ref _lengthMs, 0);
        _progress.SetLast(show.Id, episode.Key);
        Start(episode.AudioUrl, PlayStatus.Connecting, resume);
    }

    public double Position
    {
        get
        {
            if (!IsEpisode) return 0;
            if (_pendingSeek is { } p) return p;
            if (Status is PlayStatus.Playing or PlayStatus.Paused)
            {
                var ms = Interlocked.Read(ref _timeMs);
                if (ms > 0) _lastPosition = ms / 1000.0;
            }
            return _lastPosition;
        }
    }

    public double Duration => IsEpisode ? Math.Max(0, Interlocked.Read(ref _lengthMs) / 1000.0) : 0;

    public void Seek(double seconds)
    {
        if (!IsEpisode) return;
        seconds = Math.Max(0, seconds);
        if (Duration > 0) seconds = Math.Min(seconds, Math.Max(0, Duration - 1));
        _lastPosition = seconds;
        Interlocked.Exchange(ref _timeMs, (long)(seconds * 1000));
        Interlocked.Exchange(ref _lastTick, Environment.TickCount64);
        if (Status is PlayStatus.Connecting or PlayStatus.Reconnecting) _pendingSeek = seconds;
        else
        {
            var ms = (long)(seconds * 1000);
            _vlc.Run(() => { if (_mp is not null) _mp.Time = ms; });
        }
        SaveEpisodeProgress();
        Seeked?.Invoke(seconds);
    }

    public void Skip(double delta) => Seek(Position + delta);

    // ---- either -------------------------------------------------------------------------------

    /// <summary>Episodes pause in place; live channels stop (a live stream can't be paused).</summary>
    public void Pause()
    {
        if (IsEpisode && Status == PlayStatus.Playing)
        {
            _retryTimer.Stop();
            SaveEpisodeProgress();
            _vlc.Run(() => _mp?.SetPause(true));
            SetStatus(PlayStatus.Paused);
        }
        else Stop();
    }

    public void Resume()
    {
        if (Episode is { } ep)
        {
            if (Status == PlayStatus.Paused)
            {
                _vlc.Run(() => _mp?.SetPause(false));
                SetStatus(PlayStatus.Playing);
            }
            else if (!IsActive)
            {
                Reset();
                Start(ep.AudioUrl, PlayStatus.Connecting, _lastPosition > 0 ? _lastPosition : _progress.ResumePosition(ep.Key));
            }
        }
        else if (Station is { } st && !IsActive)
        {
            Reset();
            Start(st.Streams[_streamIndex], PlayStatus.Connecting);
        }
    }

    public void Toggle()
    {
        if (IsActive) Pause();
        else if (HasItem) Resume();
        else
        {
            var st = _catalog.FindStation(_settings.GetString("last_station"));
            if (st is null || !st.Available) st = _catalog.Available.Count > 0 ? _catalog.Available[0] : null;
            if (st is not null) PlayStation(st);
        }
    }

    public void Stop()
    {
        SaveEpisodeProgress();
        _retryTimer.Stop();
        _gen++;
        _vlc.Run(() => _mp?.Stop());
        SetStatus(PlayStatus.Stopped);
    }

    public void SetVolume(double gain)
    {
        Volume = Math.Clamp(gain, 0, 1);
        _settings.Set("volume", Volume);
        ApplyVolume();
        Changed?.Invoke();
    }

    public void SetMuted(bool muted)
    {
        Muted = muted;
        _settings.Set("muted", muted);
        ApplyVolume();
        Changed?.Invoke();
    }

    private void ApplyVolume()
    {
        var v = (int)Math.Round(Volume * 100);
        var m = Muted;
        _vlc.Run(() => { if (_mp is null) return; _mp.Volume = v; _mp.Mute = m; });
    }

    public void Dispose()
    {
        SaveEpisodeProgress();
        _retryTimer.Stop();
        _watchdog.Stop();
        _progressTimer.Stop();
        _gen++;
        _vlc.Run(() => { _mp?.Stop(); _media?.Dispose(); _mp?.Dispose(); _libvlc?.Dispose(); });
        _vlc.Finish(TimeSpan.FromSeconds(3));
    }

    // ---- internals -------------------------------------------------------------------------

    private void Reset()
    {
        _retryTimer.Stop();
        _failuresOnUrl = 0;
        _consecutiveFailures = 0;
        _pendingSeek = null;
    }

    private void Start(string url, PlayStatus status, double startAt = 0)
    {
        var g = ++_gen;
        _playedThisAttempt = false;
        _attemptStarted = Environment.TickCount64;
        _pendingSeek = startAt > 0 ? startAt : null;
        Interlocked.Exchange(ref _timeMs, (long)(startAt * 1000));
        SetStatus(status);
        _watchdog.Start();
        Log.Write($"start #{g} {url}{(startAt > 0 ? $" at {startAt:F0}s" : "")}");

        var opts = new List<string>
        {
            ":network-caching=" + (IsEpisode ? 3000 : 2500),
            ":http-user-agent=" + AppInfo.UserAgent,
        };
        if (startAt > 0) opts.Add(":start-time=" + startAt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var live = IsLive;
        var volume = (int)Math.Round(Volume * 100);
        var muted = Muted;

        _vlc.Run(() =>
        {
            if (_mp is null || _libvlc is null) return;
            _mp.Stop();                     // no events from the old stream after this returns
            _media?.Dispose();
            _media = new Media(_libvlc, new Uri(url), opts.ToArray());
            if (live)
            {
                var media = _media;
                media.MetaChanged += (_, e) =>
                {
                    if (e.MetadataType == MetadataType.NowPlaying)
                        _vlc.Run(() => { var t = media.Meta(MetadataType.NowPlaying); Post(() => OnTitle(t)); });
                };
            }
            _activeGen = g;
            _mp.Volume = volume;
            _mp.Mute = muted;
            if (!_mp.Play(_media)) Post(() => OnFailure("could not start"));
        });
    }

    private void SetStatus(PlayStatus status)
    {
        Status = status;
        if (status == PlayStatus.Playing && IsEpisode) _progressTimer.Start(); else _progressTimer.Stop();
        if (status is PlayStatus.Stopped or PlayStatus.Paused or PlayStatus.Error) _watchdog.Stop();
        Changed?.Invoke();
    }

    private void OnPlaying()
    {
        if (Status == PlayStatus.Paused) return;
        _playedThisAttempt = true;
        _consecutiveFailures = 0;
        _failuresOnUrl = 0;
        _pendingSeek = null;
        Interlocked.Exchange(ref _lastTick, Environment.TickCount64);
        ApplyVolume(); // VLC can drop a volume set before its audio output existed
        if (IsLive && Station is { } st && _settings.GetStreamIndex(st.Id) != _streamIndex)
            _settings.SetStreamIndex(st.Id, _streamIndex);
        Log.Write($"playing #{_gen}");
        SetStatus(PlayStatus.Playing);
    }

    private void OnTitle(string? raw)
    {
        if (!IsLive || string.IsNullOrWhiteSpace(raw)) return;
        raw = raw.Trim();
        string artist = "", title = raw;
        var dash = raw.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) { artist = raw[..dash].Trim(); title = raw[(dash + 3)..].Trim(); }
        if (title == SongTitle && artist == (SongArtist ?? "")) return;
        SongTitle = title;
        SongArtist = artist.Length > 0 ? artist : null;
        Log.Write($"title: {SongArtist} - {SongTitle}");
        Changed?.Invoke();
    }

    /// <summary>The stream ended, failed, or stalled.</summary>
    private void OnFailure(string why)
    {
        Log.Write($"failure #{_gen}: {why} (status {Status}, played {_playedThisAttempt})");
        if (Status is PlayStatus.Stopped or PlayStatus.Paused or PlayStatus.Error) return;

        if (Episode is { } ep)
        {
            if (_playedThisAttempt) _lastPosition = Math.Max(_lastPosition, Interlocked.Read(ref _timeMs) / 1000.0);
            var len = Duration;
            if (why == "ended" && _playedThisAttempt && (len <= 0 || Position >= len - ProgressStore.DoneWithin))
            {
                _progress.MarkPlayed(ep.Key, len);
                _lastPosition = 0;
                _gen++;
                SetStatus(PlayStatus.Stopped);
                return;
            }
            if (_consecutiveFailures >= EpisodeRetries) { _gen++; SetStatus(PlayStatus.Error); return; }
            _consecutiveFailures++;
            ScheduleRetry(2.0 * _consecutiveFailures);
            return;
        }

        if (Station is not { } st) { SetStatus(PlayStatus.Error); return; }
        if (_playedThisAttempt)
        {
            // It was playing and the connection dropped: reconnect to the same address soon.
            _failuresOnUrl = 0;
            ScheduleRetry(_consecutiveFailures == 0 ? 1.0 : Backoff());
            _consecutiveFailures++;
            return;
        }
        // It never started: after two tries, move on to the next address.
        _failuresOnUrl++;
        if (_failuresOnUrl >= 2 && st.Streams.Count > 1)
        {
            _streamIndex = (_streamIndex + 1) % st.Streams.Count;
            _failuresOnUrl = 0;
            ScheduleRetry(0.5);
            _consecutiveFailures++;
            return;
        }
        ScheduleRetry(Backoff());
        _consecutiveFailures++;
    }

    private double Backoff() => Math.Min(2.0 * Math.Pow(2, Math.Min(_consecutiveFailures, 4)), 30.0);

    private void ScheduleRetry(double seconds)
    {
        _gen++; // ignore anything else the failed stream says
        _retryTimer.Stop();
        _retryTimer.Interval = TimeSpan.FromSeconds(seconds);
        _retryTimer.Start();
        Log.Write($"retry in {seconds:F1}s");
        SetStatus(PlayStatus.Reconnecting);
    }

    private void Retry()
    {
        if (Episode is { } ep) Start(ep.AudioUrl, PlayStatus.Reconnecting, Position);
        else if (Station is { } st) Start(st.Streams[_streamIndex], PlayStatus.Reconnecting);
    }

    private void Watch()
    {
        var now = Environment.TickCount64;
        if (Status is PlayStatus.Connecting or PlayStatus.Reconnecting && !_retryTimer.IsEnabled
            && now - _attemptStarted > StartTimeoutSeconds * 1000)
            OnFailure("start timeout");
        else if (Status == PlayStatus.Playing && now - Interlocked.Read(ref _lastTick) > StallSeconds * 1000)
            OnFailure("stalled");
    }

    private void SaveEpisodeProgress()
    {
        if (Episode is not { } ep) return;
        var pos = Position;
        if (pos > 0) _progress.Save(ep.Key, pos, Duration);
    }

    /// <summary>One background thread that makes every VLC call, in order.</summary>
    private sealed class VlcThread
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;

        public VlcThread()
        {
            _thread = new Thread(() =>
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    try { action(); }
                    catch (Exception e) { Log.Write($"vlc thread: {e}"); }
                }
            }) { IsBackground = true, Name = "vlc" };
            _thread.Start();
        }

        public void Run(Action action)
        {
            if (!_queue.IsAddingCompleted) _queue.Add(action);
        }

        public void Finish(TimeSpan wait)
        {
            _queue.CompleteAdding();
            _thread.Join(wait);
        }
    }
}
