using System;
using System.Windows.Threading;
using Windows.Media;
using Windows.Storage.Streams;
using WinMediaPlayer = Windows.Media.Playback.MediaPlayer;

namespace HprRadio.Core;

/// <summary>
/// Windows' own media controls: the keyboard's media keys, the pop-up that appears with the
/// volume keys, the lock screen and Bluetooth headsets. A silent Windows MediaPlayer is used only
/// to get hold of those controls; VLC does the actual playing.
///
/// For live radio Next/Previous switch channels; in an episode they skip forward 30 s / back 15 s.
/// </summary>
public sealed class MediaControls : IDisposable
{
    private readonly Hub _hub;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly WinMediaPlayer? _host;
    private readonly SystemMediaTransportControls? _smtc;
    private string? _shownKey;
    private readonly DispatcherTimer _timeline;

    public MediaControls(Hub hub)
    {
        _hub = hub;
        _timeline = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => UpdateTimeline(), _ui);
        try
        {
            _host = new WinMediaPlayer();
            _host.CommandManager.IsEnabled = false;
            _smtc = _host.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsStopEnabled = true;
            _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
            _smtc.ButtonPressed += (_, e) => _ui.InvokeAsync(() => OnButton(e.Button));
            _smtc.PlaybackPositionChangeRequested += (_, e) =>
                _ui.InvokeAsync(() => _hub.Player.Seek(e.RequestedPlaybackPosition.TotalSeconds));
            hub.Player.Changed += Update;
            hub.Player.Seeked += _ => UpdateTimeline();
            hub.NowArtChanged += () => { _shownKey = null; Update(); };
            Update();
        }
        catch (Exception e)
        {
            // e.g. Windows "N" editions without the Media Feature Pack: everything else still works.
            Log.Write("media controls unavailable: " + e.Message);
            _smtc = null;
        }
    }

    private void OnButton(SystemMediaTransportControlsButton button)
    {
        var p = _hub.Player;
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play: if (!p.IsActive) p.Toggle(); break;
            case SystemMediaTransportControlsButton.Pause: if (p.IsActive) p.Pause(); break;
            case SystemMediaTransportControlsButton.Stop: p.Stop(); break;
            case SystemMediaTransportControlsButton.Next: Step(+1); break;
            case SystemMediaTransportControlsButton.Previous: Step(-1); break;
        }
    }

    /// <summary>Next/Previous: switch channels for live radio, skip within an episode.</summary>
    public void Step(int direction)
    {
        var p = _hub.Player;
        if (p.IsEpisode) { p.Skip(direction > 0 ? 30 : -15); return; }
        var list = _hub.Catalog.Available;
        if (list.Count == 0) return;
        var i = p.Station is null ? -1 : list.FindIndex(s => s.Id == p.Station.Id);
        var next = list[((i < 0 ? 0 : i + direction) % list.Count + list.Count) % list.Count];
        p.PlayStation(next);
    }

    private void Update()
    {
        if (_smtc is null) return;
        var p = _hub.Player;
        try
        {
            _smtc.PlaybackStatus = p.Status switch
            {
                PlayStatus.Playing => MediaPlaybackStatus.Playing,
                PlayStatus.Connecting or PlayStatus.Reconnecting => MediaPlaybackStatus.Changing,
                PlayStatus.Paused => MediaPlaybackStatus.Paused,
                _ => p.HasItem ? MediaPlaybackStatus.Stopped : MediaPlaybackStatus.Closed,
            };
            SleepBlocker.Set(p.IsActive);

            string title, artist, album;
            if (p.IsEpisode)
            {
                title = p.Episode!.Title;
                artist = p.Show?.Host is { Length: > 0 } h ? $"{p.Show.Title} with {h}" : p.Show?.Title ?? "";
                album = "Heartland Public Radio";
            }
            else if (p.Station is { } st)
            {
                var (songArtist, songTitle) = _hub.NowSong;
                title = songTitle ?? st.Name;
                artist = songTitle is not null ? songArtist ?? "" : "Heartland Public Radio";
                album = _hub.NowArt?.Album ?? st.FullName;
            }
            else return;

            var art = _hub.NowArt?.ImageUrl;
            var key = $"{title}|{artist}|{album}|{art}";
            if (key != _shownKey)
            {
                _shownKey = key;
                var d = _smtc.DisplayUpdater;
                d.ClearAll();
                d.Type = MediaPlaybackType.Music;
                d.MusicProperties.Title = title;
                d.MusicProperties.Artist = artist;
                d.MusicProperties.AlbumTitle = album;
                if (art is not null && Uri.TryCreate(art, UriKind.Absolute, out var uri))
                    d.Thumbnail = RandomAccessStreamReference.CreateFromUri(uri);
                d.Update();
            }
            if (p.IsEpisode) { _timeline.Start(); UpdateTimeline(); } else _timeline.Stop();
        }
        catch (Exception e) { Log.Write("media controls: " + e.Message); }
    }

    private void UpdateTimeline()
    {
        if (_smtc is null) return;
        var p = _hub.Player;
        try
        {
            var dur = TimeSpan.FromSeconds(p.IsEpisode ? p.Duration : 0);
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                EndTime = dur,
                MaxSeekTime = dur,
                Position = TimeSpan.FromSeconds(Math.Min(p.Position, dur.TotalSeconds)),
            });
        }
        catch { }
    }

    public void Dispose()
    {
        _timeline.Stop();
        SleepBlocker.Set(false);
        try { if (_smtc is not null) _smtc.IsEnabled = false; _host?.Dispose(); } catch { }
    }
}
