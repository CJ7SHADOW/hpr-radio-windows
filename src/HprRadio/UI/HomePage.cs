using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using HprRadio.Core;

namespace HprRadio.UI;

/// <summary>A screen in the main window.</summary>
public abstract class PageBase : UserControl
{
    public abstract string Route { get; }
    public virtual string PageTitle => "Heartland Public Radio";
    public virtual string? PageSubtitle => null;
    /// <summary>True when this page already shows controls for what's playing (hides the bottom bar).</summary>
    public virtual bool HasOwnControls(Player p) => false;

    private readonly List<Action> _unsubscribe = new();

    protected void On(Action subscribe, Action unsubscribe)
    {
        subscribe();
        _unsubscribe.Add(unsubscribe);
    }

    public virtual void Teardown()
    {
        foreach (var u in _unsubscribe) u();
        _unsubscribe.Clear();
    }
}

public sealed class HomePage : PageBase
{
    private readonly Hub _hub;
    private readonly MainWindow _win;
    private readonly StackPanel _channels = new();
    private readonly StackPanel _shows = new();
    private readonly TextBlock _source;
    private readonly List<ChannelCard> _cards = new();

    public override string Route => "home";

    public HomePage(Hub hub, MainWindow win)
    {
        _hub = hub;
        _win = win;
        var (scroller, body) = W.Page();
        Content = scroller;

        body.Children.Add(W.Eyebrow("Listener-supported · Commercial-free"));
        body.Children.Add(W.Title("Heartland Public Radio", 28));
        var tag = W.Text("Classic Country & Bluegrass Gospel", 15, "SubText");
        tag.Margin = new Thickness(0, 2, 0, 16);
        body.Children.Add(tag);
        body.Children.Add(_channels);
        body.Children.Add(_shows);

        var foot = W.Text("Unofficial player. Programming and streams belong to Heartland Public Radio, Inc. " +
                          "Cover art from Apple Music and Deezer.", 12, "SubText", wrap: true, maxLines: 3);
        foot.Margin = new Thickness(0, 20, 0, 4);
        body.Children.Add(foot);
        _source = W.Text("", 11.5, "SubText");
        body.Children.Add(_source);

        Rebuild();
        On(() => hub.Catalog.Changed += Rebuild, () => hub.Catalog.Changed -= Rebuild);
        On(() => hub.Player.Changed += Refresh, () => hub.Player.Changed -= Refresh);
        On(() => hub.RecentChanged += OnRecent, () => hub.RecentChanged -= OnRecent);
        On(() => hub.NowArtChanged += Refresh, () => hub.NowArtChanged -= Refresh);
    }

    private void OnRecent(string _) => Refresh();

    private void Rebuild()
    {
        _channels.Children.Clear();
        _cards.Clear();
        foreach (var st in _hub.Catalog.Stations)
        {
            var card = new ChannelCard(_hub, st, () => _win.OpenChannel(st));
            _cards.Add(card);
            _channels.Children.Add(card.Root);
        }

        _shows.Children.Clear();
        if (_hub.Catalog.Shows.Count > 0)
        {
            var s = W.Eyebrow("On demand");
            s.Margin = new Thickness(0, 18, 0, 8);
            _shows.Children.Add(s);
        }
        foreach (var show in _hub.Catalog.Shows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var art = new ArtTile(_hub.Images, 64, 10);
            art.Set(show.ImageUrl, show.Badge, Theme.FromHex("#8A4B1F"));
            grid.Children.Add(art);
            var texts = new StackPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(W.Text(show.Title, 15.5, "Text", FontWeights.SemiBold));
            if (show.Host.Length > 0) texts.Children.Add(W.Text("with " + show.Host, 13, "SubText"));
            var more = W.Eyebrow("Recent episodes and archive");
            more.Margin = new Thickness(0, 3, 0, 0);
            texts.Children.Add(more);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            var chev = W.Glyph(W.Chevron, 14);
            chev.SetResourceReference(TextBlock.ForegroundProperty, "SubText");
            Grid.SetColumn(chev, 2);
            grid.Children.Add(chev);
            var s = show;
            _shows.Children.Add(W.Row(grid, () => _win.OpenShow(s)));
        }
        Refresh();
    }

    private void Refresh()
    {
        foreach (var c in _cards) c.Refresh();
        _source.Text = $"Version {AppInfo.Version} · channel list: {_hub.Catalog.Source}";
    }

    /// <summary>One channel: badge or current cover, what's on, and a quick play/stop button.</summary>
    private sealed class ChannelCard
    {
        private readonly Hub _hub;
        private readonly Station _st;
        private readonly ArtTile _art;
        private readonly TextBlock _now;
        private readonly PlayButton _play;
        public Button Root { get; }

        public ChannelCard(Hub hub, Station st, Action open)
        {
            _hub = hub;
            _st = st;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _art = new ArtTile(hub.Images, 64, 10);
            grid.Children.Add(_art);

            var texts = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(W.Eyebrow(st.Channel));
            texts.Children.Add(W.Text(st.Name, 15.5, "Text", FontWeights.SemiBold));
            _now = W.Text("", 13, "SubText", wrap: true, maxLines: 2);
            texts.Children.Add(_now);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            _play = new PlayButton(40);
            _play.Click += (_, e) =>
            {
                e.Handled = true; // don't also open the channel
                var p = _hub.Player;
                if (p.IsLive && p.Station?.Id == _st.Id && p.IsActive) p.Stop();
                else p.PlayStation(_st);
            };
            Grid.SetColumn(_play, 2);
            grid.Children.Add(_play);

            Root = W.Row(grid, open);
            Root.IsEnabled = st.Available;
            if (!st.Available) Root.Opacity = 0.55;
        }

        public void Refresh()
        {
            var p = _hub.Player;
            var mine = p.IsLive && p.Station?.Id == _st.Id;
            _play.Update(mine ? p.Status : PlayStatus.Stopped, pauses: false);
            _play.Visibility = _st.Available ? Visibility.Visible : Visibility.Collapsed;

            string? cover = mine ? _hub.NowArt?.ImageUrl : null;
            _art.Set(cover, _st.Channel, Theme.FromHex(_st.Color));

            if (!_st.Available) { _now.Text = _st.Note ?? "Off the air for now"; return; }
            if (mine && p.Status is PlayStatus.Connecting or PlayStatus.Reconnecting or PlayStatus.Error)
            {
                _now.Text = W.StatusText(p);
                return;
            }
            var (artist, title) = _hub.OnAir(_st);
            _now.Text = title is null ? _st.Description : string.IsNullOrEmpty(artist) ? title : $"{title} · {artist}";
        }
    }
}
