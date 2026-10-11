using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace HprRadio.Core;

/// <summary>One live channel, as listed in channels.json.</summary>
public sealed record Station(
    string Id, string Channel, string Name, string Description,
    IReadOnlyList<string> Streams, string? RecentTracksUrl, string? Note, string? Color)
{
    public bool Available => Streams.Count > 0;
    public string FullName => $"{Channel}: {Name}";
}

/// <summary>One web page that lists a show's episodes.</summary>
public sealed record ShowSource(string Label, string? RestUrl, string? HtmlUrl, bool IsArchive)
{
    public string CacheKey => RestUrl ?? HtmlUrl ?? "";
}

public sealed record Show(
    string Id, string Title, string Badge, string Host, string Channel, string Schedule,
    string Description, string? ImageUrl, string? PageUrl, IReadOnlyList<ShowSource> Sources)
{
    public ShowSource RecentSource => Sources.FirstOrDefault(s => !s.IsArchive) ?? Sources[0];
    public ShowSource? ArchiveSource => Sources.FirstOrDefault(s => s.IsArchive && s != RecentSource);
}

public sealed record Content(IReadOnlyList<Station> Stations, IReadOnlyList<Show> Shows);

/// <summary>
/// The channel and show lists, from the same channels.json the Android app uses.
/// Where the list comes from, in order: the server copy (downloaded at every start), the last
/// server copy that loaded, then the copy shipped with the app. A server file that can't be
/// parsed, or has no playable channel, is ignored.
/// </summary>
public sealed class Catalog
{
    public const string SourceBuiltIn = "built-in";
    public const string SourceSaved = "saved copy";
    public const string SourceServer = "live from server";

    private readonly string _cacheFile = Path.Combine(Paths.Cache, "channels.json");
    private readonly string _cacheFrom = Path.Combine(Paths.Cache, "channels.url");

    public string ConfigUrl { get; }
    public Content Content { get; private set; }
    public string Source { get; private set; } = SourceBuiltIn;

    /// <summary>Raised on the UI thread when a new list has loaded.</summary>
    public event Action? Changed;

    public Catalog(string configUrl)
    {
        ConfigUrl = configUrl;
        var builtIn = Parse(Paths.ReadText(Path.Combine(Paths.AppDir, "Assets", "channels.json")) ?? "")
                      ?? throw new InvalidOperationException("The built-in channels.json is missing or invalid");
        Content = builtIn;
        // Use the saved server copy only if it came from the address we're using now.
        if ((Paths.ReadText(_cacheFrom) ?? "").Trim() == configUrl
            && Parse(Paths.ReadText(_cacheFile) ?? "") is { } saved)
        {
            Content = saved;
            Source = SourceSaved;
        }
    }

    public IReadOnlyList<Station> Stations => Content.Stations;
    public List<Station> Available => Content.Stations.Where(s => s.Available).ToList();
    public IReadOnlyList<Show> Shows => Content.Shows;
    public Station? FindStation(string? id) => Content.Stations.FirstOrDefault(s => s.Id == id);
    public Show? FindShow(string? id) => Content.Shows.FirstOrDefault(s => s.Id == id);

    public async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(ConfigUrl)) return;
        var url = ConfigUrl;
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url += (url.Contains('?') ? "&" : "?") + "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60; // sidestep stale caches
        var text = await Net.GetTextAsync(url, 10, 500_000);
        if (text is null || Parse(text) is not { } parsed) return;
        Paths.WriteAtomic(_cacheFile, text);
        Paths.WriteAtomic(_cacheFrom, ConfigUrl);
        Content = parsed;
        Source = SourceServer;
        Changed?.Invoke();
    }

    // ---- parsing (same rules as the Android app) -----------------------------------------------

    private static string? Str(JsonElement o, string key)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(key, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static bool Enabled(JsonElement o) =>
        !o.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;

    private static IEnumerable<JsonElement> Array(JsonElement root, string key) =>
        root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static Content? Parse(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var stations = new List<Station>();
            var seen = new HashSet<string>();
            foreach (var o in Array(root, "channels"))
            {
                if (o.ValueKind != JsonValueKind.Object || !Enabled(o)) continue;
                var id = Str(o, "id");
                if (id is null || !seen.Add(id)) continue;
                var channel = Str(o, "channel") ?? id.ToUpperInvariant();
                var streams = Array(o, "streams")
                    .Where(s => s.ValueKind == JsonValueKind.String)
                    .Select(s => s.GetString()!.Trim())
                    .Where(s => s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var recent = Str(o, "recentTracksUrl");
                stations.Add(new Station(id, channel, Str(o, "name") ?? channel, Str(o, "description") ?? "",
                    streams, recent is not null && recent.StartsWith("http") ? recent : null,
                    Str(o, "note"), Str(o, "color")));
            }

            var shows = new List<Show>();
            seen.Clear();
            foreach (var o in Array(root, "shows"))
            {
                if (o.ValueKind != JsonValueKind.Object || !Enabled(o)) continue;
                var id = Str(o, "id");
                var title = Str(o, "title");
                if (id is null || title is null || seen.Contains(id)) continue;
                var pages = new List<ShowSource>();
                foreach (var p in Array(o, "episodePages"))
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var rest = Str(p, "restUrl");
                    var html = Str(p, "htmlUrl");
                    if (rest is null && html is null) continue;
                    var archive = p.TryGetProperty("archive", out var a) && a.ValueKind == JsonValueKind.True;
                    pages.Add(new ShowSource(Str(p, "label") ?? "Episodes", rest, html, archive));
                }
                if (pages.Count == 0) continue;
                seen.Add(id);
                shows.Add(new Show(id, title, Str(o, "badge") ?? title[..Math.Min(3, title.Length)].ToUpperInvariant(),
                    Str(o, "host") ?? "", Str(o, "channel") ?? "", Str(o, "schedule") ?? "",
                    Str(o, "description") ?? "", Str(o, "imageUrl"), Str(o, "pageUrl"), pages));
            }

            if (!stations.Any(s => s.Available)) return null; // never accept a list with nothing to play
            return new Content(stations, shows);
        }
    }
}
