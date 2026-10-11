using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HprRadio.Core;

public sealed record Art(string ImageUrl, string? Album);

/// <summary>
/// Album art lookup: iTunes Search first, Deezer second (both free, no key). A result is only
/// accepted when its artist matches, so a wrong cover is less likely than no cover. Results,
/// including misses, are remembered on disk.
/// </summary>
public static class Artwork
{
    private const int Max = 2000;
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(Paths.Cache, "artwork.json");
    private static Dictionary<string, Art?>? _cache;
    private static readonly List<string> Order = new();
    private static readonly Dictionary<string, Task<Art?>> InFlight = new();

    private static string Norm(string s)
    {
        s = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in s)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        s = sb.ToString().Replace("&", " and ");
        s = Regex.Replace(s, "^the ", "");
        s = Regex.Replace(s, "[^a-z0-9 ]", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string Key(string artist, string title) => $"{Norm(artist)}|{Norm(title)}";

    /// <summary>Drops "(Live)", "[Remastered]" and the like from the search.</summary>
    private static string CleanTitle(string t) => Regex.Replace(t, @"[(\[].*?[)\]]", "").Trim();

    public static bool ArtistMatches(string wanted, string found)
    {
        var a = Norm(wanted);
        var b = Norm(found);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b || a.Contains(b) || b.Contains(a)) return true;
        return Regex.Split(a, " and | with | feat | ft ").Select(n => n.Trim())
            .Where(n => n.Length > 2).Any(n => b.Contains(n));
    }

    private static Dictionary<string, Art?> Cache()
    {
        if (_cache is not null) return _cache;
        _cache = new Dictionary<string, Art?>();
        try
        {
            if (JsonNode.Parse(Paths.ReadText(FilePath) ?? "{}") is JsonObject o)
                foreach (var (k, v) in o)
                {
                    _cache[k] = v is JsonObject a && a["image_url"]?.GetValue<string>() is { } url
                        ? new Art(url, a["album"]?.GetValue<string>()) : null;
                    Order.Add(k);
                }
        }
        catch { }
        return _cache;
    }

    private static void Save()
    {
        var o = new JsonObject();
        foreach (var k in Order.Skip(Math.Max(0, Order.Count - Max)))
            o[k] = _cache![k] is { } a ? new JsonObject { ["image_url"] = a.ImageUrl, ["album"] = a.Album } : null;
        Paths.WriteAtomic(FilePath, o.ToJsonString());
    }

    /// <summary>Known result (which may be "no cover"), without a lookup.</summary>
    public static bool TryCached(string artist, string title, out Art? art)
    {
        lock (Gate) return Cache().TryGetValue(Key(artist, title), out art);
    }

    public static Task<Art?> FindAsync(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return Task.FromResult<Art?>(null);
        var k = Key(artist, title);
        lock (Gate)
        {
            if (Cache().TryGetValue(k, out var hit)) return Task.FromResult(hit);
            if (InFlight.TryGetValue(k, out var running)) return running;
            var task = LookUpAsync(k, artist, title);
            InFlight[k] = task;
            return task;
        }
    }

    private static async Task<Art?> LookUpAsync(string key, string artist, string title)
    {
        Art? result = null;
        try { result = await ITunesAsync(artist, title).ConfigureAwait(false) ?? await DeezerAsync(artist, title).ConfigureAwait(false); }
        catch (Exception e) { Log.Write($"art lookup: {e.Message}"); }
        lock (Gate)
        {
            InFlight.Remove(key);
            Cache()[key] = result;
            Order.Remove(key);
            Order.Add(key);
            Save();
        }
        return result;
    }

    private static async Task<Art?> ITunesAsync(string artist, string title)
    {
        var term = Uri.EscapeDataString($"{artist} {CleanTitle(title)}");
        var body = await Net.GetArtTextAsync($"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=10").ConfigureAwait(false);
        if (body is null) return null;
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in results.EnumerateArray())
        {
            var name = r.TryGetProperty("artistName", out var n) ? n.GetString() ?? "" : "";
            if (!ArtistMatches(artist, name)) continue;
            if (r.TryGetProperty("artworkUrl100", out var art) && art.GetString() is { Length: > 0 } url)
                return new Art(url.Replace("100x100bb", "600x600bb"),
                    r.TryGetProperty("collectionName", out var c) ? c.GetString() : null);
        }
        return null;
    }

    private static async Task<Art?> DeezerAsync(string artist, string title)
    {
        var q = Uri.EscapeDataString($"artist:\"{artist}\" track:\"{CleanTitle(title)}\"");
        var body = await Net.GetArtTextAsync($"https://api.deezer.com/search?q={q}&limit=10").ConfigureAwait(false);
        if (body is null) return null;
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in data.EnumerateArray())
        {
            var name = r.TryGetProperty("artist", out var a) && a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
            if (!ArtistMatches(artist, name)) continue;
            if (!r.TryGetProperty("album", out var album)) continue;
            var url = album.TryGetProperty("cover_xl", out var xl) ? xl.GetString() : null;
            url ??= album.TryGetProperty("cover_big", out var big) ? big.GetString() : null;
            if (!string.IsNullOrEmpty(url))
                return new Art(url, album.TryGetProperty("title", out var t) ? t.GetString() : null);
        }
        return null;
    }
}
