using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HprRadio.Core;

namespace HprRadio.UI;

public sealed class MainWindow : Window
{
    private readonly Hub _hub;
    private readonly MediaControls _controls;
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly List<PageBase> _pages = new();
    private readonly Button _back;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly NowPlayingBar _bar;
    private readonly Button _menuButton;
    public VolumeButton Volume { get; }

    public MainWindow(Hub hub, MediaControls controls)
    {
        _hub = hub;
        _controls = controls;
        Title = "HPR Radio";
        Width = 480;
        Height = 860;
        MinWidth = 380;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg");
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/hpr.ico")); } catch { }
        RestorePlacement();

        // ---- header: back, title, volume, menu
        var header = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _back = W.IconButton(W.Back, "Back (Esc)", GoBack, 14);
        header.Children.Add(_back);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        _title = W.Text("Heartland Public Radio", 14.5, "Text", FontWeights.SemiBold);
        _subtitle = W.Text("", 11.5, "SubText");
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Volume = new VolumeButton(hub.Player);
        Grid.SetColumn(Volume, 2);
        header.Children.Add(Volume);
        var menu = W.IconButton(W.Menu, "Menu", () => { });
        menu.Click += (_, _) => ShowMenu(menu);
        _menuButton = menu;
        Grid.SetColumn(menu, 3);
        header.Children.Add(menu);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.SetResourceReference(Border.BorderBrushProperty, "Divider");

        _bar = new NowPlayingBar(hub, OpenNowPlaying);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerBorder, Dock.Top);
        DockPanel.SetDock(_bar, Dock.Bottom);
        root.Children.Add(headerBorder);
        root.Children.Add(_bar);
        root.Children.Add(_host);
        Content = root;

        Push(new HomePage(hub, this));

        hub.Player.Changed += UpdateChrome;
        hub.NowArtChanged += UpdateChrome;
        PreviewKeyDown += OnKey;
        MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; } };
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);
        Closing += (_, _) => SavePlacement();
    }

    // ---- navigation ------------------------------------------------------------------------

    public PageBase Current => _pages[^1];

    private void Push(PageBase page)
    {
        _pages.Add(page);
        Display(page);
    }

    private void Display(PageBase page)
    {
        _host.Content = page;
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        UpdateChrome();
        page.Focusable = true;
        page.Focus();
    }

    public void GoBack()
    {
        if (_pages.Count <= 1) return;
        var top = _pages[^1];
        _pages.RemoveAt(_pages.Count - 1);
        top.Teardown();
        Display(_pages[^1]);
    }

    private void GoHome()
    {
        while (_pages.Count > 1) GoBack();
    }

    public void OpenChannel(Station st)
    {
        if (Current.Route == "ch:" + st.Id) return;
        GoHome();
        Push(new ChannelPage(_hub, st));
    }

    public void OpenShow(Show show)
    {
        // From the player's "All episodes": go back to the show page if it's below.
        var i = _pages.FindIndex(p => p.Route == "show:" + show.Id);
        if (i >= 0)
        {
            while (_pages.Count > i + 1) GoBack();
            return;
        }
        GoHome();
        Push(new ShowPage(_hub, show, this));
    }

    public void OpenEpisodePlayer()
    {
        if (_hub.Player.Episode is null || Current.Route == "player") return;
        Push(new EpisodePage(_hub, this));
    }

    private void OpenNowPlaying()
    {
        var p = _hub.Player;
        if (p.IsEpisode)
        {
            if (p.Show is { } s && _pages.All(pg => pg.Route != "show:" + s.Id)) OpenShow(s);
            OpenEpisodePlayer();
        }
        else if (p.Station is { } st) OpenChannel(st);
    }

    private void UpdateChrome()
    {
        var page = Current;
        _back.Visibility = _pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _title.Text = page.PageTitle;
        _subtitle.Text = page.PageSubtitle ?? "";
        _subtitle.Visibility = page.PageSubtitle is null ? Visibility.Collapsed : Visibility.Visible;
        _bar.Visibility = page.HasOwnControls(_hub.Player) ? Visibility.Collapsed : Visibility.Visible;
        Title = _hub.Player.IsActive && _hub.NowSong.Title is { } t ? $"{t} · HPR Radio" : "HPR Radio";
    }

    // ---- keyboard ------------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var focus = Keyboard.FocusedElement;
        switch (key)
        {
            case Key.Escape when _pages.Count > 1:
            case Key.BrowserBack:
            case Key.Left when alt:
                GoBack();
                break;
            case Key.Space when focus is not ButtonBase and not Slider and not TextBox:
                _hub.Player.Toggle();
                break;
            case Key.Up when ctrl:
                Volume.Nudge(+0.05);
                break;
            case Key.Down when ctrl:
                Volume.Nudge(-0.05);
                break;
            case Key.M when ctrl:
                _hub.Player.SetMuted(!_hub.Player.Muted);
                break;
            case Key.Right when ctrl:
                _controls.Step(+1);
                break;
            case Key.Left when ctrl:
                _controls.Step(-1);
                break;
            case Key.F5:
            case Key.R when ctrl:
                _ = _hub.Catalog.RefreshAsync();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---- menu ----------------------------------------------------------------------------------

    /// <summary>The most recently opened main menu (for the self-test screenshots).</summary>
    public ContextMenu? LastMenu { get; private set; }

    public void OpenMenu() => ShowMenu(_menuButton);

    private void ShowMenu(FrameworkElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, FontFamily = W.UiFont };
        LastMenu = m;
        void Add(string text, string? gesture, Action act)
        {
            var mi = new MenuItem { Header = text, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => act();
            m.Items.Add(mi);
        }
        Add("Reload channel list", "F5", () => _ = _hub.Catalog.RefreshAsync());
        Add("Open hpr.org", null, () => OpenUrl("https://hpr.org"));
        m.Items.Add(new Separator());
        Add("Keyboard shortcuts", null, ShowShortcuts);
        Add("About HPR Radio", null, ShowAbout);
        m.IsOpen = true;
    }

    private void ShowShortcuts() => MessageBox.Show(this,
        "Space — play / stop\n" +
        "Esc or Alt+← — back\n" +
        "Ctrl+← / Ctrl+→ — previous / next channel (skip in an episode)\n" +
        "Ctrl+↑ / Ctrl+↓ — volume up / down\n" +
        "Ctrl+M — mute\n" +
        "F5 — reload channel list\n\n" +
        "The keyboard's media keys work too, even when this window isn't in front.",
        "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.None);

    private void ShowAbout() => MessageBox.Show(this,
        $"HPR Radio {AppInfo.Version}\n\n" +
        "An unofficial player for Heartland Public Radio: Classic Country and Bluegrass Gospel, commercial-free.\n\n" +
        "Programming, streams and the HPR logo belong to Heartland Public Radio, Inc. " +
        "Cover art from Apple Music and Deezer. Playback by VLC (LibVLC, LGPL).\n\n" +
        $"Channel list: {_hub.Catalog.Source}\n{_hub.Catalog.ConfigUrl}",
        "About HPR Radio", MessageBoxButton.OK, MessageBoxImage.Information);

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write("open url: " + e.Message); }
    }

    // ---- window size and position --------------------------------------------------------------

    private void RestorePlacement()
    {
        var s = _hub.Settings;
        var w = s.GetDouble("win_w", 0);
        var h = s.GetDouble("win_h", 0);
        if (w >= MinWidth && h >= MinHeight) { Width = w; Height = h; }
        var x = s.GetDouble("win_x", double.NaN);
        var y = s.GetDouble("win_y", double.NaN);
        if (!double.IsNaN(x) && !double.IsNaN(y)
            && x >= SystemParameters.VirtualScreenLeft && y >= SystemParameters.VirtualScreenTop
            && x + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && y + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = x;
            Top = y;
        }
    }

    private void SavePlacement()
    {
        if (WindowState != WindowState.Normal) return;
        var s = _hub.Settings;
        s.Set("win_w", Width);
        s.Set("win_h", Height);
        s.Set("win_x", Left);
        s.Set("win_y", Top);
    }
}

/// <summary>Bottom bar: what's playing, with a play/stop button. Click it to open that page.</summary>
public sealed class NowPlayingBar : Border
{
    private readonly Hub _hub;
    private readonly ArtTile _art;
    private readonly TextBlock _heading;
    private readonly TextBlock _line;
    private readonly PlayButton _play;

    public NowPlayingBar(Hub hub, Action open)
    {
        _hub = hub;
        SetResourceReference(BackgroundProperty, "NpBar");
        BorderThickness = new Thickness(0, 1, 0, 0);
        SetResourceReference(BorderBrushProperty, "Divider");
        Padding = new Thickness(14, 10, 14, 10);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var target = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        target.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        target.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _art = new ArtTile(hub.Images, 46, 8);
        target.Children.Add(_art);
        var texts = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _heading = W.Text("", 13.5, "AccentText", FontWeights.SemiBold);
        _line = W.Text("", 13, "Text");
        texts.Children.Add(_heading);
        texts.Children.Add(_line);
        Grid.SetColumn(texts, 1);
        target.Children.Add(texts);
        target.MouseLeftButtonUp += (_, _) => { if (_hub.Player.HasItem) open(); };
        grid.Children.Add(target);

        _play = new PlayButton(44);
        _play.Click += (_, _) => _hub.Player.Toggle();
        Grid.SetColumn(_play, 1);
        grid.Children.Add(_play);
        Child = grid;

        hub.Player.Changed += Refresh;
        hub.NowArtChanged += Refresh;
        hub.RecentChanged += _ => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        var p = _hub.Player;
        _play.Update(p.Status, pauses: p.IsEpisode);
        if (p.IsEpisode)
        {
            _heading.Text = p.Show?.Title ?? "";
            _line.Text = p.Status is PlayStatus.Playing or PlayStatus.Stopped ? p.Episode!.Title : W.StatusText(p);
            _art.Set(p.Show?.ImageUrl, p.Show?.Badge ?? "", Theme.FromHex("#8A4B1F"));
        }
        else if (p.Station is { } st)
        {
            _heading.Text = st.FullName;
            var (artist, title) = _hub.NowSong;
            _line.Text = p.Status == PlayStatus.Playing && title is not null
                ? (string.IsNullOrEmpty(artist) ? title : $"{title} · {artist}")
                : p.Status == PlayStatus.Stopped ? st.Description : W.StatusText(p);
            _art.Set(_hub.NowArt?.ImageUrl, st.Channel, Theme.FromHex(st.Color));
        }
        else
        {
            _heading.Text = "";
            _line.Text = "Pick a channel to start listening";
            _line.FontWeight = FontWeights.SemiBold;
            _art.Set(null, "HPR", Theme.FromHex("#8A4B1F"));
            return;
        }
        _line.FontWeight = FontWeights.Normal;
    }
}

/// <summary>Speaker button in the header: click for a slider, scroll over it to change the volume.
/// The slider is perceptual: position p sets a gain of p³, like Windows' own volume slider.</summary>
public sealed class VolumeButton : Button
{
    private const double Step = 0.05;
    private readonly Player _player;
    private readonly TextBlock _icon;
    private readonly Popup _popup;
    private readonly Slider _slider;
    private readonly Button _mute;
    private readonly TextBlock _percent;
    private bool _updating;

    /// <summary>The pop-up's contents (for the self-test screenshots).</summary>
    public Border PopupRoot { get; }

    public void Open() => _popup.IsOpen = true;

    public VolumeButton(Player player)
    {
        _player = player;
        SetResourceReference(StyleProperty, "IconButton");
        _icon = W.Glyph(W.Volume[3], 16);
        Content = _icon;

        _mute = W.IconButton(W.Mute, "Mute", () => _player.SetMuted(!_player.Muted));
        _slider = new Slider { Maximum = 100, Width = 200, VerticalAlignment = VerticalAlignment.Center, SmallChange = 5, LargeChange = 10 };
        _slider.SetResourceReference(StyleProperty, "StationSlider");
        System.Windows.Automation.AutomationProperties.SetName(_slider, "Volume");
        _slider.ValueChanged += (_, e) =>
        {
            if (_updating) return;
            _player.SetVolume(Math.Pow(e.NewValue / 100, 3));
            if (_player.Muted) _player.SetMuted(false); // moving the slider unmutes, like Windows does
        };
        _percent = W.Text("", 12.5, "Text");
        _percent.Width = 40;
        _percent.TextAlignment = TextAlignment.Right;
        _percent.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 14, 6) };
        row.Children.Add(_mute);
        _slider.Margin = new Thickness(6, 0, 6, 0);
        row.Children.Add(_slider);
        row.Children.Add(_percent);
        var card = new Border { Child = row, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Bg");
        card.SetResourceReference(Border.BorderBrushProperty, "Divider");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.25 };
        _popup = new Popup
        {
            Child = PopupRoot = new Border { Child = card, Padding = new Thickness(10) },
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };
        TextElement.SetFontFamily(PopupRoot, W.UiFont);
        _popup.Opened += (_, _) => _slider.Focus();
        Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;
        PreviewMouseWheel += (_, e) => { Nudge(e.Delta > 0 ? Step : -Step); e.Handled = true; };
        player.Changed += Sync;
        Sync();
    }

    public void Nudge(double delta)
    {
        var pos = Math.Cbrt(_player.Volume) + delta;
        pos = Math.Clamp(Math.Round(pos / Step) * Step, 0, 1);
        _player.SetVolume(Math.Pow(pos, 3));
        if (_player.Muted && delta > 0) _player.SetMuted(false);
    }

    private void Sync()
    {
        var pct = (int)Math.Round(Math.Cbrt(_player.Volume) * 100);
        _updating = true;
        try { if (Math.Abs(_slider.Value - pct) > 0.5) _slider.Value = pct; }
        finally { _updating = false; }
        var muted = _player.Muted;
        _percent.Text = muted ? "Muted" : $"{pct}%";
        _icon.Text = muted || pct == 0 ? W.Mute : W.Volume[pct < 34 ? 1 : pct < 67 ? 2 : 3];
        ((TextBlock)_mute.Content).Text = muted ? W.Volume[3] : W.Mute;
        _mute.ToolTip = muted ? "Unmute" : "Mute";
        ToolTip = muted ? "Volume: muted" : $"Volume: {pct}%  (scroll to change)";
    }
}
