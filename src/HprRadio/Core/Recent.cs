using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace HprRadio.Core;

public sealed record Track(string Artist, string Title, long PlayedAt)
{
    public string Key => $"{Artist.ToLowerInvariant()}|{Title.ToLowerInvariant()}";

    public string TimeText => PlayedAt <= 0 ? "" :
        DateTimeOffset.FromUnixTimeSeconds(PlayedAt).ToLocalTime().ToString("h:mm tt");
}

/// <summary>Recently played songs from HPR's stream host (Centova Cast).</summary>
public static class Recent
{
    public const int Limit = 15;

    public static List<Track> Parse(string text)
    {
        // {"type":"result","rid":"hpr1hi","data":[[{"artist":"..","title":"..","time":1791054005}, ...]]}
        var outList = new List<Track>();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.GetString() != "result"
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return outList;
        var items = data.GetArrayLength() > 0 && data[0].ValueKind == JsonValueKind.Array ? data[0] : data;
        foreach (var o in items.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object) continue;
            string? Get(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var title = Net.HtmlToText(Get("title"));
            if (title is null) continue;
            long played = 0;
            if (o.TryGetProperty("time", out var t))
            {
                if (t.ValueKind == JsonValueKind.Number) t.TryGetInt64(out played);
                else if (t.ValueKind == JsonValueKind.String) long.TryParse(t.GetString(), out played);
            }
            outList.Add(new Track(Net.HtmlToText(Get("artist")) ?? "", title, played));
            if (outList.Count >= Limit) break;
        }
        return outList;
    }

    public static async Task<List<Track>?> FetchAsync(Station station)
    {
        var url = station.RecentTracksUrl;
        if (url is null) return null;
        var body = await Net.GetTextAsync(url).ConfigureAwait(false);
        if (body is null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            body = await Net.GetTextAsync("http://" + url["https://".Length..]).ConfigureAwait(false); // Centova's port 2199 is often http only
        if (body is null) return null;
        try { return Parse(body); }
        catch (JsonException) { return null; }
    }

    /// <summary>Keeps songs seen in earlier refreshes so the list fills out to the full length.</summary>
    public static List<Track> Merge(IReadOnlyList<Track>? old, IReadOnlyList<Track> fresh)
    {
        var seen = new HashSet<string>();
        return fresh.Concat(old ?? Array.Empty<Track>())
            .OrderByDescending(t => t.PlayedAt)
            .Where(t => seen.Add($"{t.PlayedAt}|{t.Key}"))
            .Take(Limit + 1)
            .ToList();
    }
}
