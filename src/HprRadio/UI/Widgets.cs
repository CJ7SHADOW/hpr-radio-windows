using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HprRadio.Core;

namespace HprRadio.UI;

/// <summary>Small building blocks shared by the screens.</summary>
public static class W
{
    public const string Play = "";
    public const string Pause = "";
    public const string Stop = "";
    public const string Back = "";
    public const string More = "";
    public const string Menu = "";
    public const string Chevron = "";
    public const string SkipBack = "";
    public const string SkipFwd = "";
    public const string ListIcon = "";
    public const string Mute = "";
    public static readonly string[] Volume = { "", "", "", "" };

    public static TextBlock Text(string text, double size = 14, string brush = "Text",
        FontWeight? weight = null, bool wrap = false, int maxLines = 1)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (wrap && maxLines > 1) t.MaxHeight = size * 1.45 * maxLines;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>Small capitals label, e.g. "ON THE AIR".</summary>
    public static TextBlock Eyebrow(string text) => Spaced(Text(text.ToUpperInvariant(), 11.5, "AccentText", FontWeights.SemiBold));

    public static TextBlock Title(string text, double size = 24)
    {
        var t = Text(text, size, "Text", FontWeights.Bold, wrap: true, maxLines: 3);
        t.SetResourceReference(TextBlock.FontFamilyProperty, "TitleFont");
        return t;
    }

    public static TextBlock Section(string text)
    {
        var t = Text(text, 16, "Text", FontWeights.SemiBold);
        t.Margin = new Thickness(0, 18, 0, 8);
        return t;
    }

    private static TextBlock Spaced(TextBlock t)
    {
        t.Margin = new Thickness(0, 0, 0, 2);
        return t;
    }

    public static TextBlock Glyph(string glyph, double size = 16)
    {
        var t = new TextBlock { Text = glyph, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        return t;
    }

    public static Button IconButton(string glyph, string tip, Action onClick, double size = 16)
    {
        var b = new Button { Content = Glyph(glyph, size), ToolTip = tip };
        b.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        b.Click += (_, _) => onClick();
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    public static Button Pill(string text, Action onClick, bool accent = false, string? glyph = null)
    {
        object content = text;
        if (glyph is not null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var g = Glyph(glyph, 14);
            g.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(g);
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        var b = new Button { Content = content };
        b.SetResourceReference(FrameworkElement.StyleProperty, accent ? "AccentPill" : "PillButton");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Row(UIElement content, Action onClick, Thickness? padding = null)
    {
        var b = new Button { Content = content, Margin = new Thickness(0, 0, 0, 4) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "RowButton");
        if (padding is { } p) b.Padding = p;
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>A centered column of at most 640 px, like the other apps.</summary>
    public static (ScrollViewer Scroller, StackPanel Body) Page()
    {
        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 24), MaxWidth = 640 };
        var sv = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        return (sv, body);
    }

    public static string Clock(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Remaining(double seconds)
    {
        var m = (int)Math.Ceiling(seconds / 60);
        if (m <= 1) return "1 min left";
        return m >= 60 ? $"{m / 60} h {m % 60} min left" : $"{m} min left";
    }

    public static string StatusText(Player p) => p.Status switch
    {
        PlayStatus.Connecting => "Connecting…",
        PlayStatus.Reconnecting => "Connection dropped, retrying…",
        PlayStatus.Error => p.IsEpisode ? "Couldn't play this episode." : "Couldn't connect to this channel.",
        PlayStatus.Paused => "Paused",
        PlayStatus.Playing => p.IsLive ? "Listening live" : "",
        _ => "",
    };
}

/// <summary>Album art with a colored placeholder tile (channel badge) until the image loads.</summary>
public sealed class ArtTile : Border
{
    private readonly ImageCache _images;
    private readonly TextBlock _badge;
    private readonly double _size;
    private string? _url;
    private Brush _color = Brushes.SaddleBrown;

    public ArtTile(ImageCache images, double size, double radius = 12)
    {
        _images = images;
        _size = size;
        Width = Height = size;
        CornerRadius = new CornerRadius(radius);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        _badge = new TextBlock
        {
            FontWeight = FontWeights.Bold,
            FontSize = Math.Max(11, size * 0.24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _badge.SetResourceReference(TextBlock.ForegroundProperty, "Placeholder");
        Child = _badge;
    }

    public void Set(string? url, string badge, Brush color)
    {
        _color = color;
        _badge.Text = badge;
        _badge.FontSize = Math.Max(11, _size * (badge.Length > 4 ? 0.17 : 0.24));
        if (url == _url && Background is ImageBrush) return;
        _url = url;
        Background = color;
        _badge.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(url)) return;
        _ = LoadAsync(url);
    }

    private async System.Threading.Tasks.Task LoadAsync(string url)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var bmp = await _images.GetAsync(url, (int)Math.Ceiling(_size * Math.Max(1, dpi)));
        if (url != _url) return; // something else was set meanwhile
        if (bmp is null) return;
        Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        _badge.Visibility = Visibility.Collapsed;
    }
}

/// <summary>The round accent play / pause / stop button.</summary>
public sealed class PlayButton : Button
{
    private readonly TextBlock _glyph;

    public PlayButton(double size)
    {
        Width = Height = size;
        SetResourceReference(StyleProperty, "PlayButton");
        _glyph = W.Glyph(W.Play, size * 0.36);
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        Content = _glyph;
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>pauses: episodes pause; live channels stop.</summary>
    public void Update(PlayStatus status, bool pauses)
    {
        var active = status is PlayStatus.Connecting or PlayStatus.Playing or PlayStatus.Reconnecting;
        _glyph.Text = active ? (pauses ? W.Pause : W.Stop) : W.Play;
        var tip = active ? (pauses ? "Pause" : "Stop") : "Play";
        ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(this, tip);
    }
}
