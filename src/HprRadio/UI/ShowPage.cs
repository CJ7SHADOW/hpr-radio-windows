using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HprRadio.Core;

namespace HprRadio.UI;

/// <summary>A show: about it, "continue listening", recent episodes, then the archive by year.</summary>
public sealed class ShowPage : PageBase
{
    private readonly Hub _hub;
    private readonly Show _show;
    private readonly MainWindow _win;
    private readonly StackPanel _continue = new();
    private readonly StackPanel _sections = new();
    private readonly List<EpisodeRow> _rows = new();
    private List<Episode>? _recent;
    private List<Episode>? _archive;
    private bool _recentFailed, _archiveFailed;

    public Show Show => _show;
    public override string Route => "show:" + _show.Id;
    public override string PageTitle => _show.Title;
    public override string? PageSubtitle => "Heartland Public Radio";

    public ShowPage(Hub hub, Show show, MainWindow win)
    {
        _hub = hub;
        _show = show;
        _win = win;
        var (scroller, body) = W.Page();
        Content = scroller;

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var art = new ArtTile(hub.Images, 112, 16);
        art.Set(show.ImageUrl, show.Badge, Theme.FromHex("#8A4B1F"));
        head.Children.Add(art);
        var texts = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(W.Eyebrow("On demand"));
        texts.Children.Add(W.Title(show.Title, 24));
        if (show.Host.Length > 0) texts.Children.Add(W.Text("with " + show.Host, 14, "Text"));
        if (show.Schedule.Length > 0) texts.Children.Add(W.Text(show.Schedule, 13, "SubText", wrap: true, maxLines: 2));
        Grid.SetColumn(texts, 1);
        head.Children.Add(texts);
        body.Children.Add(head);
        if (show.Description.Length > 0)
        {
            var d = W.Text(show.Description, 14, "Text", wrap: true, maxLines: 8);
            d.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(d);
        }
        body.Children.Add(_continue);
        body.Children.Add(_sections);
        var foot = W.Text("Episodes stream from the Internet Archive, courtesy of Heartland Public Radio.", 12, "SubText", wrap: true, maxLines: 2);
        foot.Margin = new Thickness(0, 18, 0, 0);
        body.Children.Add(foot);

        _recent = Episodes.Cached(show.RecentSource);
        _archive = show.ArchiveSource is { } a ? Episodes.Cached(a) : null;
        Rebuild();
        _ = LoadAsync();
        On(() => hub.Player.Changed += Refresh, () => hub.Player.Changed -= Refresh);
        On(() => hub.Progress.Changed += Refresh, () => hub.Progress.Changed -= Refresh);
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        _recentFailed = _archiveFailed = false;
        var recentTask = Episodes.LoadAsync(_show.RecentSource);
        var archiveTask = _show.ArchiveSource is { } a ? Episodes.LoadAsync(a) : null;
        var r = await recentTask;
        if (r is not null) _recent = r; else _recentFailed = _recent is null;
        Rebuild();
        if (archiveTask is null) return;
        var ar = await archiveTask;
        if (ar is not null) _archive = ar; else _archiveFailed = _archive is null;
        Rebuild();
    }

    private void Rebuild()
    {
        _sections.Children.Clear();
        _rows.Clear();

        _sections.Children.Add(W.Section("Recent episodes"));
        if (_recent is not null) _sections.Children.Add(List(_recent));
        else _sections.Children.Add(LoadingOrError(_recentFailed, "Loading episodes…"));

        if (_show.ArchiveSource is not null)
        {
            if (_archive is not null)
            {
                var recentKeys = new HashSet<string>((_recent ?? new List<Episode>()).Select(e => e.Key));
                var years = _archive.Where(e => !recentKeys.Contains(e.Key)).GroupBy(e => e.Year).OrderByDescending(g => g.Key).ToList();
                for (var i = 0; i < years.Count; i++) _sections.Children.Add(YearSection(years[i].Key, years[i].ToList(), i == 0));
            }
            else if (_recent is not null)
            {
                _sections.Children.Add(W.Section("Archive"));
                _sections.Children.Add(LoadingOrError(_archiveFailed, "Loading the archive…"));
            }
        }
        Refresh();
    }

    private StackPanel List(IEnumerable<Episode> eps)
    {
        var panel = new StackPanel();
        foreach (var ep in eps)
        {
            var row = new EpisodeRow(_hub, _show, ep, _win);
            _rows.Add(row);
            row.Refresh();
            panel.Children.Add(row.Root);
        }
        return panel;
    }

    /// <summary>A year of the archive, collapsed except the newest; rows are built when opened.</summary>
    private UIElement YearSection(int year, List<Episode> eps, bool open)
    {
        var header = W.Text(year > 0 ? $"Archive · {year}   ({eps.Count})" : $"Archive   ({eps.Count})", 16, "Text", FontWeights.SemiBold);
        var exp = new Expander { Header = header, Margin = new Thickness(0, 12, 0, 0), IsExpanded = false };
        exp.Expanded += (_, _) =>
        {
            if (exp.Content is null)
            {
                var list = List(eps);
                list.Margin = new Thickness(0, 8, 0, 0);
                exp.Content = list;
            }
        };
        exp.IsExpanded = open;
        return exp;
    }

    private UIElement LoadingOrError(bool failed, string text)
    {
        if (!failed) return W.Text(text, 13, "SubText");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var t = W.Text("Couldn't load episodes from hpr.org.", 13, "SubText");
        t.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(t);
        var retry = W.Pill("Try again", () => { _ = LoadAsync(); Rebuild(); });
        retry.Margin = new Thickness(12, 0, 0, 0);
        sp.Children.Add(retry);
        return sp;
    }

    private void Refresh()
    {
        foreach (var r in _rows) r.Refresh();
        _continue.Children.Clear();
        var last = _hub.Progress.Last;
        var p = _hub.Player;
        if (last is { } l && l.ShowId == _show.Id && p.Episode?.Key != l.Key)
        {
            var ep = (_recent ?? new List<Episode>()).Concat(_archive ?? new List<Episode>()).FirstOrDefault(e => e.Key == l.Key);
            if (ep is not null) _continue.Children.Add(ContinueCard(ep));
        }
    }

    private UIElement ContinueCard(Episode ep)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(W.Eyebrow("Continue listening"));
        texts.Children.Add(W.Text(ep.Title, 15, "Text", FontWeights.SemiBold));
        if (_hub.Progress.Get(ep.Key) is { Duration: > 0 } pr)
        {
            texts.Children.Add(W.Text(W.Remaining(pr.Remaining), 12.5, "SubText"));
            var bar = new ProgressBar { Maximum = 1, Value = pr.Fraction, Margin = new Thickness(0, 6, 12, 0), MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, Width = 260 };
            bar.SetResourceReference(StyleProperty, "ThinBar");
            texts.Children.Add(bar);
        }
        grid.Children.Add(texts);
        var resume = W.Pill("Resume", () => { _hub.Player.PlayEpisode(_show, ep); _win.OpenEpisodePlayer(); }, accent: true);
        resume.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(resume, 1);
        grid.Children.Add(resume);
        var card = new Border { Child = grid, Padding = new Thickness(16, 12, 14, 12), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 16, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "Card");
        return card;
    }
}

/// <summary>One episode: title, note, progress, play/pause, and a ⋯ menu.</summary>
public sealed class EpisodeRow
{
    private readonly Hub _hub;
    private readonly Show _show;
    private readonly Episode _ep;
    private readonly TextBlock _title;
    private readonly StackPanel _progressBox;
    private readonly ProgressBar _bar;
    private readonly TextBlock _progressText;
    private readonly PlayButton _play;
    private object? _state;
    public Button Root { get; }

    public EpisodeRow(Hub hub, Show show, Episode ep, MainWindow win)
    {
        _hub = hub;
        _show = show;
        _ep = ep;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _title = W.Text(ep.Title, 14.5, "Text", FontWeights.SemiBold);
        texts.Children.Add(_title);
        if (ep.Note is not null) texts.Children.Add(W.Text(ep.Note, 13, "SubText", wrap: true, maxLines: 2));
        _progressBox = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _bar = new ProgressBar { Maximum = 1, Width = 96, VerticalAlignment = VerticalAlignment.Center };
        _bar.SetResourceReference(FrameworkElement.StyleProperty, "ThinBar");
        _progressText = W.Text("", 12, "SubText");
        _progressText.Margin = new Thickness(8, 0, 0, 0);
        _progressBox.Children.Add(_bar);
        _progressBox.Children.Add(_progressText);
        texts.Children.Add(_progressBox);
        grid.Children.Add(texts);

        _play = new PlayButton(36) { Margin = new Thickness(8, 0, 4, 0) };
        _play.Click += (_, e) =>
        {
            e.Handled = true;
            var p = _hub.Player;
            if (p.Episode?.Key == _ep.Key && p.IsActive) p.Pause(); else p.PlayEpisode(_show, _ep);
        };
        Grid.SetColumn(_play, 1);
        grid.Children.Add(_play);

        var more = W.IconButton(W.More, "More options", () => { });
        more.Click += (_, e) => { e.Handled = true; ShowMenu(more); };
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);

        Root = W.Row(grid, () =>
        {
            var p = _hub.Player;
            if (p.Episode?.Key == _ep.Key) win.OpenEpisodePlayer();
            else p.PlayEpisode(_show, _ep);
        }, new Thickness(14, 8, 8, 8));
    }

    private void ShowMenu(FrameworkElement anchor)
    {
        var pr = _hub.Progress.Get(_ep.Key);
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string text, Action act)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        if (pr is { Done: false, Position: > 0 })
            Add("Play from the beginning", () => { _hub.Player.PlayEpisode(_show, _ep, fromStart: true); });
        if (_ep.PlaylistUrl is { } pdf) Add("View song playlist (PDF)", () => MainWindow.OpenUrl(pdf));
        if (pr is { Done: true }) Add("Mark as unplayed", () => _hub.Progress.Clear(_ep.Key));
        else Add("Mark as played", () => _hub.Progress.MarkPlayed(_ep.Key));
        menu.IsOpen = true;
    }

    public void Refresh()
    {
        var p = _hub.Player;
        var current = p.Episode?.Key == _ep.Key;
        var pr = _hub.Progress.Get(_ep.Key);
        var state = (current, current ? p.Status : PlayStatus.Stopped, pr?.Done, Math.Round(pr?.Fraction ?? 0, 3), (int)((pr?.Remaining ?? 0) / 60));
        if (Equals(state, _state)) return;
        _state = state;

        _title.SetResourceReference(TextBlock.ForegroundProperty, current ? "AccentText" : "Text");
        _play.Update(current ? p.Status : PlayStatus.Stopped, pauses: true);
        if (pr is { Done: true })
        {
            _bar.Visibility = Visibility.Collapsed;
            _progressText.Margin = new Thickness(0);
            _progressText.Text = "✓ Played";
            _progressBox.Visibility = Visibility.Visible;
        }
        else if (pr is { Duration: > 0 })
        {
            _bar.Visibility = Visibility.Visible;
            _bar.Value = pr.Fraction;
            _progressText.Margin = new Thickness(8, 0, 0, 0);
            _progressText.Text = W.Remaining(pr.Remaining);
            _progressBox.Visibility = Visibility.Visible;
        }
        else _progressBox.Visibility = Visibility.Collapsed;
    }
}
