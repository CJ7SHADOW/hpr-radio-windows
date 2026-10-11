using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HprRadio.Core;

public sealed record Episode(
    string Key,          // archive.org item name, e.g. "good-n-country-10-3-2026"
    string Title,        // "Saturday, October 3, 2026"
    string? Note,        // e.g. "Tribute to Dolly Parton"
    int DateKey,         // yyyymmdd; 0 if unknown
    string AudioUrl,
    string? PlaylistUrl,
    bool FromArchive)
{
    public int Year => DateKey / 10_000;
}

/// <summary>
/// Episode lists read from hpr.org show pages. Each episode there is a paragraph with the air
/// date and a playlist PDF link, followed by a WordPress audio player:
///   &lt;p&gt;&lt;strong&gt;Saturday, October 3, 2026&lt;/strong&gt;&lt;a href="/hprplaylists/…/20261003.pdf"&gt;playlist&lt;/a&gt;&lt;/p&gt;
///   &lt;audio class="wp-audio-shortcode" …&gt;&lt;source type="audio/mpeg" src="https://archive.org/download/…mp3?_=1" /&gt;
/// </summary>
public static class Episodes
{
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex Audio = new(@"<audio\b[\s\S]*?</audio>", I);
    private static readonly Regex SourceSrc = new(@"<source[^>]*\bsrc=[""']([^""']+)[""']", I);
    private static readonly Regex Mp3Href = new(@"href=[""']([^""']+\.mp3[^""']*)[""']", I);
    private static readonly Regex Para = new(@"<p\b[^>]*>([\s\S]*?)</p>", I);
    private static readonly Regex Pdf = new(@"href=[""']([^""']+\.pdf)[""']", I);
    private static readonly Regex Date = new(
        @"(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2}),?\s+(\d{4})", I);
    private static readonly Regex Weekday = new(@"\b(Mon|Tues|Wednes|Thurs|Fri|Satur|Sun)day,?\s*", I);
    private static readonly Regex IaDirect = new(@"^https?://ia\d+\.us\.archive\.org/\d+/items/", I);
    private static readonly Regex IaItem = new(@"archive\.org/download/([^/]+)/", I);
    private static readonly string[] Months =
        { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

    /// <summary>Strips WordPress's "?_=1" and points at archive.org's stable download address.</summary>
    private static string NormalizeAudio(string src)
    {
        src = src.Replace("&amp;", "&");
        src = Regex.Replace(src, @"[?&]_=\d+$", "");
        src = IaDirect.Replace(src, "https://archive.org/download/");
        return Regex.Replace(src, @"^http://((?:[\w-]+\.)*archive\.org/)", "https://$1", RegexOptions.IgnoreCase);
    }

    private static string Absolute(string pageUrl, string href)
    {
        href = href.Replace("&amp;", "&").Trim();
        if (href.StartsWith("//")) return "https:" + href;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https")) return href;
        return Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && Uri.TryCreate(page, href, out var joined)
            ? joined.ToString() : href;
    }

    public static string DisplayDate(int dateKey)
    {
        try
        {
            var d = new DateTime(dateKey / 10_000, dateKey / 100 % 100, dateKey % 100);
            return d.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);
        }
        catch { return dateKey.ToString(); }
    }

    public static List<Episode> Parse(string html, bool fromArchive, string pageUrl)
    {
        var list = new List<Episode>();
        var seen = new HashSet<string>();
        var last = 0;
        foreach (Match m in Audio.Matches(html))
        {
            var before = html[last..m.Index];
            last = m.Index + m.Length;
            var src = SourceSrc.Match(m.Value);
            if (!src.Success) src = Mp3Href.Match(m.Value);
            if (!src.Success) continue;
            var audio = NormalizeAudio(Absolute(pageUrl, src.Groups[1].Value));
            var item = IaItem.Match(audio);
            var key = item.Success ? item.Groups[1].Value : audio;
            if (!seen.Add(key)) continue;

            // The label is the paragraph(s) from the last one carrying a date up to the player.
            var paras = Para.Matches(before).Select(p => p.Groups[1].Value).ToList();
            var start = paras.FindLastIndex(p => Date.IsMatch(Net.HtmlToText(p) ?? ""));
            var labelHtml = string.Join(" ", start >= 0 ? paras.Skip(start) : paras.Skip(Math.Max(0, paras.Count - 1)));
            var text = Regex.Replace(Net.HtmlToText(labelHtml) ?? "", @"\bplaylist\b", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\s+", " ").Trim();

            var date = Date.Match(text);
            var dateKey = 0;
            if (date.Success)
            {
                var month = Array.IndexOf(Months, date.Groups[1].Value.ToLowerInvariant()) + 1;
                dateKey = int.Parse(date.Groups[3].Value) * 10_000 + month * 100 + int.Parse(date.Groups[2].Value);
            }
            string title;
            if (dateKey > 0) title = DisplayDate(dateKey);
            else if (text.Length > 0) title = text;
            else title = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(audio.Split('?')[0]));

            string? note = null;
            if (date.Success)
            {
                var n = Weekday.Replace(text.Replace(date.Value, " "), " ");
                n = Regex.Replace(n, @"\s+", " ").Trim(' ', '-', '–', '—', ':', ',', '.', '|');
                note = n.Length > 1 ? n : null;
            }
            var pdf = Pdf.Match(labelHtml);
            list.Add(new Episode(key, title, note, dateKey, audio,
                pdf.Success ? Absolute(pageUrl, pdf.Groups[1].Value) : null, fromArchive));
        }
        return list.OrderByDescending(e => e.DateKey).ToList();
    }

    private static readonly Dictionary<string, List<Episode>> Cache = new();

    public static List<Episode>? Cached(ShowSource source)
    {
        lock (Cache) return Cache.TryGetValue(source.CacheKey, out var l) ? l : null;
    }

    public static async Task<List<Episode>?> LoadAsync(ShowSource source)
    {
        string? html = null;
        if (source.RestUrl is not null && await Net.GetTextAsync(source.RestUrl).ConfigureAwait(false) is { } body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                html = doc.RootElement.GetProperty("content").GetProperty("rendered").GetString();
            }
            catch { html = null; }
        }
        if (html is null && source.HtmlUrl is not null)
            html = await Net.GetTextAsync(source.HtmlUrl).ConfigureAwait(false);
        if (html is null) return null;
        var eps = await Task.Run(() => Parse(html, source.IsArchive, source.HtmlUrl ?? source.RestUrl ?? "")).ConfigureAwait(false);
        if (eps.Count > 0) lock (Cache) Cache[source.CacheKey] = eps;
        return eps;
    }

    public static Episode? Find(Show show, string key) =>
        show.Sources.SelectMany(s => Cached(s) ?? new List<Episode>()).FirstOrDefault(e => e.Key == key);
}

public sealed record Progress(double Position, double Duration, bool Done)
{
    public double Fraction => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
    public double Remaining => Math.Max(0, Duration - Position);
}

/// <summary>Where you stopped in each episode, saved to %APPDATA%\HPR Radio\progress.json.</summary>
public sealed class ProgressStore
{
    public const double MinSave = 30;     // don't remember the first 30 seconds
    public const double DoneWithin = 90;  // within 90 s of the end counts as played

    private readonly string _file = Path.Combine(Paths.Data, "progress.json");
    private readonly JsonObject _data;

    /// <summary>Raised whenever an episode's saved state changes.</summary>
    public event Action? Changed;

    public ProgressStore()
    {
        try { _data = JsonNode.Parse(Paths.ReadText(_file) ?? "{}") as JsonObject ?? new JsonObject(); }
        catch { _data = new JsonObject(); }
        if (_data["episodes"] is not JsonObject) _data["episodes"] = new JsonObject();
    }

    private JsonObject Eps => (JsonObject)_data["episodes"]!;

    public Progress? Get(string key)
    {
        if (Eps[key] is not JsonObject v) return null;
        double D(string k) => v[k] is JsonValue j && j.TryGetValue(out double d) ? d : 0;
        return new Progress(D("pos"), D("dur"), v["done"] is JsonValue b && b.TryGetValue(out bool done) && done);
    }

    public double ResumePosition(string key) => Get(key) is { Done: false } p ? p.Position : 0;

    public void Save(string key, double position, double duration)
    {
        if (position < 0) return;
        if (duration > 0 && position >= duration - DoneWithin) { MarkPlayed(key, duration); return; }
        var existing = Get(key);
        if (position < MinSave && existing is not { Done: true }) return;
        Write(key, position, Math.Max(duration, 0), false);
    }

    public void MarkPlayed(string key, double? duration = null) =>
        Write(key, 0, duration ?? Get(key)?.Duration ?? 0, true);

    public void Clear(string key)
    {
        Eps.Remove(key);
        Flush();
    }

    /// <summary>(show id, episode key) of the most recent episode you haven't finished.</summary>
    public (string ShowId, string Key)? Last
    {
        get
        {
            if (_data["last"] is not JsonArray a || a.Count != 2) return null;
            var show = a[0]?.GetValue<string>();
            var key = a[1]?.GetValue<string>();
            if (show is null || key is null) return null;
            return Get(key) is { Done: false } ? (show, key) : null;
        }
    }

    public void SetLast(string showId, string key)
    {
        _data["last"] = new JsonArray(showId, key);
        Flush();
    }

    private void Write(string key, double pos, double dur, bool done)
    {
        Eps[key] = new JsonObject { ["pos"] = pos, ["dur"] = dur, ["done"] = done };
        Flush();
    }

    private void Flush()
    {
        Paths.WriteAtomic(_file, _data.ToJsonString());
        Changed?.Invoke();
    }
}
