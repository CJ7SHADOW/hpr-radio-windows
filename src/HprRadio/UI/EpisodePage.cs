using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using HprRadio.Core;

namespace HprRadio.UI;

/// <summary>The full on-demand player: cover, seek bar, 15 s back / play-pause / 30 s forward.</summary>
public sealed class EpisodePage : PageBase
{
    private readonly Hub _hub;
    private readonly MainWindow _win;
    private readonly ArtTile _art;
    private readonly TextBlock _eyebrow;
    private readonly TextBlock _title;
    private readonly TextBlock _sub;
    private readonly Slider _seek;
    private readonly TextBlock _pos;
    private readonly TextBlock _left;
    private readonly PlayButton _play;
    private readonly TextBlock _status;
    private readonly Button _playlist;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _seekDebounce;
    private bool _dragging;
    private bool _updating;

    public override string Route => "player";
    public override string PageTitle => _hub.Player.Show?.Title ?? "On demand";
    public override string? PageSubtitle => "Heartland Public Radio";
    public override bool HasOwnControls(Player p) => true;

    public EpisodePage(Hub hub, MainWindow win)
    {
        _hub = hub;
        _win = win;
        var (scroller, body) = W.Page();
        Content = scroller;

        _art = new ArtTile(hub.Images, 260, 22) { HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(_art);
        _eyebrow = W.Eyebrow("");
        _eyebrow.HorizontalAlignment = HorizontalAlignment.Center;
        _eyebrow.Margin = new Thickness(0, 20, 0, 2);
        body.Children.Add(_eyebrow);
        _title = W.Title("", 22);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.TextAlignment = TextAlignment.Center;
        body.Children.Add(_title);
        _sub = W.Text("", 14, "SubText", wrap: true, maxLines: 2);
        _sub.HorizontalAlignment = HorizontalAlignment.Center;
        _sub.TextAlignment = TextAlignment.Center;
        body.Children.Add(_sub);

        _seek = new Slider { Maximum = 1, Margin = new Thickness(0, 18, 0, 0) };
        _seek.SetResourceReference(StyleProperty, "StationSlider");
        _seek.ValueChanged += OnSeekValue;
        _seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _dragging = false;
            _hub.Player.Seek(_seek.Value);
        }));
        System.Windows.Automation.AutomationProperties.SetName(_seek, "Position");
        body.Children.Add(_seek);
        var times = new Grid();
        _pos = W.Text("0:00", 12, "SubText");
        _left = W.Text("", 12, "SubText");
        _left.HorizontalAlignment = HorizontalAlignment.Right;
        times.Children.Add(_pos);
        times.Children.Add(_left);
        body.Children.Add(times);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 4) };
        controls.Children.Add(SkipButton(W.SkipBack, "15", "Back 15 seconds", -15));
        _play = new PlayButton(72) { Margin = new Thickness(26, 0, 26, 0) };
        _play.Click += (_, _) => _hub.Player.Toggle();
        controls.Children.Add(_play);
        controls.Children.Add(SkipButton(W.SkipFwd, "30", "Forward 30 seconds", 30));
        body.Children.Add(controls);
        _status = W.Text("", 13, "SubText");
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(_status);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        _playlist = W.Pill("Song playlist (PDF)", () => { if (_hub.Player.Episode?.PlaylistUrl is { } u) MainWindow.OpenUrl(u); }, glyph: W.ListIcon);
        _playlist.Margin = new Thickness(5);
        buttons.Children.Add(_playlist);
        var all = W.Pill("All episodes", () => { if (_hub.Player.Show is { } s) _win.OpenShow(s); });
        all.Margin = new Thickness(5);
        buttons.Children.Add(all);
        body.Children.Add(buttons);

        _seekDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _seekDebounce.Tick += (_, _) => { _seekDebounce.Stop(); _hub.Player.Seek(_seek.Value); };
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal, (_, _) => UpdatePosition(), Dispatcher);

        Refresh();
        On(() => hub.Player.Changed += Refresh, () => hub.Player.Changed -= Refresh);
        On(() => hub.Player.Seeked += OnSeeked, () => hub.Player.Seeked -= OnSeeked);
    }

    private Button SkipButton(string glyph, string label, string tip, double delta)
    {
        var g = new Grid();
        g.Children.Add(W.Glyph(glyph, 22));
        var n = W.Text(label, 9, "Text", FontWeights.Bold);
        n.HorizontalAlignment = HorizontalAlignment.Center;
        n.VerticalAlignment = VerticalAlignment.Bottom;
        n.Margin = new Thickness(0, 0, 0, -14);
        g.Children.Add(n);
        var b = W.IconButton(W.SkipBack, tip, () => _hub.Player.Skip(delta));
        b.Content = g;
        b.Width = b.Height = 52;
        return b;
    }

    private void OnSeeked(double _) => UpdatePosition(force: true);

    private void OnSeekValue(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _pos.Text = W.Clock(e.NewValue);
        if (_dragging) return;
        _seekDebounce.Stop();
        _seekDebounce.Start(); // clicks on the bar and arrow keys
    }

    private void Refresh()
    {
        var p = _hub.Player;
        if (p.Episode is not { } ep || p.Show is not { } show) { _win.GoBack(); return; }
        _art.Set(show.ImageUrl, show.Badge, Theme.FromHex("#8A4B1F"));
        _eyebrow.Text = show.Title.ToUpperInvariant();
        _title.Text = ep.Title;
        _sub.Text = string.Join(" · ", new[] { ep.Note, show.Host.Length > 0 ? "with " + show.Host : null }.AsEnumerableNonNull());
        _play.Update(p.Status, pauses: true);
        _status.Text = W.StatusText(p);
        _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _playlist.Visibility = ep.PlaylistUrl is null ? Visibility.Collapsed : Visibility.Visible;
        if (p.Status == PlayStatus.Playing) _tick.Start(); else _tick.Stop();
        UpdatePosition();
    }

    private void UpdatePosition(bool force = false)
    {
        if (_dragging && !force) return;
        var p = _hub.Player;
        var dur = p.Duration;
        var pos = p.Position;
        _updating = true;
        try
        {
            _seek.Maximum = dur > 0 ? dur : Math.Max(1, pos);
            _seek.Value = Math.Min(pos, _seek.Maximum);
            _seek.IsEnabled = dur > 0;
        }
        finally { _updating = false; }
        _pos.Text = W.Clock(pos);
        _left.Text = dur > 0 ? "-" + W.Clock(dur - pos) : "--:--";
    }

    public override void Teardown()
    {
        _tick.Stop();
        _seekDebounce.Stop();
        base.Teardown();
    }
}

internal static class SeqExtensions
{
    public static System.Collections.Generic.IEnumerable<string> AsEnumerableNonNull(this string?[] items)
    {
        foreach (var s in items) if (!string.IsNullOrEmpty(s)) yield return s;
    }
}
