using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using HprRadio.Core;

namespace HprRadio.UI;

/// <summary>One channel: big cover for the song on the air, play/stop, and the last 15 songs.</summary>
public sealed class ChannelPage : PageBase
{
    private readonly Hub _hub;
    private readonly Station _st;
    private readonly ArtTile _art;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly TextBlock _album;
    private readonly PlayButton _play;
    private readonly TextBlock _status;
    private readonly StackPanel _list = new();
    private readonly TextBlock _listNote;
    private string _shownList = "";

    public override string Route => "ch:" + _st.Id;
    public override string PageTitle => _st.FullName;
    public override string? PageSubtitle => "Heartland Public Radio";
    public override bool HasOwnControls(Player p) => p.IsLive && p.Station?.Id == _st.Id;

    public ChannelPage(Hub hub, Station st)
    {
        _hub = hub;
        _st = st;
        var (scroller, body) = W.Page();
        Content = scroller;

        _art = new ArtTile(hub.Images, 260, 22) { HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(_art);
        var onAir = W.Eyebrow("On the air");
        onAir.HorizontalAlignment = HorizontalAlignment.Center;
        onAir.Margin = new Thickness(0, 20, 0, 2);
        body.Children.Add(onAir);
        _title = Centered(W.Title("", 22));
        _artist = Centered(W.Text("", 15, "Text"));
        _album = Centered(W.Text("", 13, "SubText"));
        body.Children.Add(_title);
        body.Children.Add(_artist);
        body.Children.Add(_album);

        _play = new PlayButton(68) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 6) };
        _play.Click += (_, _) =>
        {
            var p = _hub.Player;
            if (HasOwnControls(p) && p.IsActive) p.Stop(); else p.PlayStation(_st);
        };
        body.Children.Add(_play);
        _status = Centered(W.Text("", 13, "SubText"));
        body.Children.Add(_status);

        body.Children.Add(W.Section("Recently played"));
        _listNote = W.Text("Loading…", 13, "SubText");
        body.Children.Add(_listNote);
        body.Children.Add(_list);

        Refresh();
        On(() => hub.Player.Changed += Refresh, () => hub.Player.Changed -= Refresh);
        On(() => hub.NowArtChanged += Refresh, () => hub.NowArtChanged -= Refresh);
        On(() => hub.RecentChanged += OnRecent, () => hub.RecentChanged -= OnRecent);
        _hub.RefreshRecent();
    }

    private static TextBlock Centered(TextBlock t)
    {
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        return t;
    }

    private void OnRecent(string id)
    {
        if (id == _st.Id) Refresh();
    }

    private void Refresh()
    {
        var p = _hub.Player;
        var mine = HasOwnControls(p);
        _play.Update(mine ? p.Status : PlayStatus.Stopped, pauses: false);
        _play.IsEnabled = _st.Available;

        var (artist, title) = _hub.OnAir(_st);
        _title.Text = title ?? _st.Name;
        _artist.Text = title is null ? _st.Description : artist ?? "";
        var art = mine ? _hub.NowArt : null;
        if (!mine && title is not null && artist is not null && Artwork.TryCached(artist, title, out var known)) art = known;
        _album.Text = art?.Album ?? "";
        _album.Visibility = _album.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _art.Set(art?.ImageUrl, _st.Channel, Theme.FromHex(_st.Color));
        if (!mine && title is not null && art is null) _ = FetchArt(artist, title);

        _status.Text = !_st.Available ? _st.Note ?? "Off the air for now" : mine ? W.StatusText(p) : "";
        _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshList();
    }

    private async System.Threading.Tasks.Task FetchArt(string? artist, string title)
    {
        var found = await Artwork.FindAsync(artist, title);
        if (found is null) return;
        var (a, t) = _hub.OnAir(_st);
        if (a == artist && t == title) Refresh();
    }

    private void RefreshList()
    {
        _hub.Histories.TryGetValue(_st.Id, out var tracks);
        if (_st.RecentTracksUrl is null) { _listNote.Text = "No song history for this channel."; return; }
        if (tracks is null || tracks.Count == 0)
        {
            _listNote.Text = _hub.RecentLoaded ? "Couldn't load the song history." : "Loading…";
            _listNote.Visibility = Visibility.Visible;
            return;
        }
        _listNote.Visibility = Visibility.Collapsed;
        // The first entry is the song on the air (shown above), so the list starts with the one before.
        var shown = new List<Track>();
        var (_, nowTitle) = _hub.OnAir(_st);
        foreach (var t in tracks)
        {
            if (shown.Count == 0 && t.Title == nowTitle) continue;
            shown.Add(t);
            if (shown.Count == Recent.Limit) break;
        }
        var key = string.Join("\n", shown.ConvertAll(t => $"{t.PlayedAt}|{t.Key}"));
        if (key == _shownList) return;
        _shownList = key;
        _list.Children.Clear();
        foreach (var t in shown) _list.Children.Add(TrackRow(t));
    }

    private UIElement TrackRow(Track t)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var art = new ArtTile(_hub.Images, 48, 8);
        art.Set(null, _st.Channel, Theme.FromHex(_st.Color));
        grid.Children.Add(art);
        _ = LoadRowArt(art, t);
        var texts = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(W.Text(t.Title, 14, "Text", FontWeights.SemiBold));
        texts.Children.Add(W.Text(t.Artist, 13, "SubText"));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var time = W.Text(t.TimeText, 12, "SubText");
        time.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);
        var card = new Border { Child = grid, Padding = new Thickness(10, 8, 12, 8), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 4) };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        return card;
    }

    private async System.Threading.Tasks.Task LoadRowArt(ArtTile tile, Track t)
    {
        var art = await Artwork.FindAsync(t.Artist, t.Title);
        if (art is not null) tile.Set(art.ImageUrl, _st.Channel, Theme.FromHex(_st.Color));
    }
}
