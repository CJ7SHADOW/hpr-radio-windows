using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HprRadio.Core;
using HprRadio.UI;

namespace HprRadio;

/// <summary>
/// Automated run used by the GitHub build: plays the real HPR streams (silently), loads real
/// song histories, album art and Good 'N Country pages, takes screenshots, writes live.log.
/// </summary>
public static class SelfTest
{
    public static void Run(Application app, Hub hub, MainWindow w, string outDir)
    {
        var steps = new List<(int DelayMs, Action Act)>();
        void At(int ms, Action a) => steps.Add((ms, a));
        var p = hub.Player;

        void Shot(string name)
        {
            Log.Write($"[{name}] catalog={hub.Catalog.Source} status={p.Status} song='{p.SongArtist} - {p.SongTitle}' " +
                      $"art={hub.NowArt?.ImageUrl ?? "none"} pos={p.Position:F0}/{p.Duration:F0} vol={p.Volume:F3}");
            try
            {
                var root = (FrameworkElement)w.Content;
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var bg = new DrawingVisual();
                using (var dc = bg.RenderOpen()) dc.DrawRectangle(w.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                rtb.Render(bg);
                rtb.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var f = File.Create(Path.Combine(outDir, name + ".png"));
                enc.Save(f);
            }
            catch (Exception e) { Log.Write("screenshot failed: " + e.Message); }
        }

        At(8000, () => Shot("1-home"));
        At(500, () => { if (hub.Catalog.Available.FirstOrDefault() is { } st) p.PlayStation(st); });
        At(15000, () => Shot("2-home-playing"));
        At(500, () => w.OpenChannel(p.Station ?? hub.Catalog.Available[0]));
        At(7000, () => Shot("3-channel"));
        At(500, () => w.GoBack());
        if (hub.Catalog.Shows.Count > 0)
        {
            var show = hub.Catalog.Shows[0];
            At(500, () => w.OpenShow(show));
            At(10000, () => Shot("4-show"));
            At(500, () =>
            {
                var eps = Episodes.Cached(show.RecentSource);
                Log.Write($"episodes on page: {eps?.Count.ToString() ?? "none"}");
                if (eps is { Count: > 0 }) { p.PlayEpisode(show, eps[0]); w.OpenEpisodePlayer(); }
            });
            At(15000, () => Shot("5-episode"));
            // Seek, pause and resume.
            At(500, () => p.Seek(600));
            At(6000, () => Log.Write($"after seek to 600: pos={p.Position:F0} status={p.Status}"));
            At(500, () => p.Pause());
            At(2000, () => Log.Write($"after pause: pos={p.Position:F0} status={p.Status}"));
            At(500, () => p.Resume());
            At(6000, () => { Log.Write($"after resume: pos={p.Position:F0} status={p.Status}"); Shot("6-episode-seeked"); });
            At(500, () => w.GoBack());
            At(1500, () => Shot("7-show-progress"));
        }
        At(500, () => { w.Volume.Nudge(-0.2); Log.Write($"volume after nudge: {p.Volume:F3}"); });
        At(500, () => { Theme.Force(true); Theme.Apply(); });
        At(1500, () => Shot("8-dark"));
        At(500, () =>
        {
            foreach (var st in hub.Catalog.Available)
                Log.Write($"recent {st.Id}: {(hub.Histories.TryGetValue(st.Id, out var t) ? $"{t.Count} {t[0].Artist} - {t[0].Title}" : "none")}");
            foreach (var show in hub.Catalog.Shows)
                foreach (var src in show.Sources)
                    Log.Write($"episodes {show.Id}/{src.Label}: {Episodes.Cached(src)?.Count.ToString() ?? "none"}");
            Log.Write("SELFTEST DONE");
            app.Shutdown();
        });

        var i = 0;
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { steps[i].Act(); }
            catch (Exception e) { Log.Write($"step {i} failed: {e}"); }
            i++;
            if (i < steps.Count) { timer.Interval = TimeSpan.FromMilliseconds(steps[i].DelayMs); timer.Start(); }
        };
        timer.Interval = TimeSpan.FromMilliseconds(steps[0].DelayMs);
        timer.Start();
    }
}
