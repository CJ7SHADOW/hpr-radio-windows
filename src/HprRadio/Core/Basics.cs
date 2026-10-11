using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HprRadio.Core;

/// <summary>Where the app keeps its files.</summary>
public static class Paths
{
    /// <summary>%APPDATA%\HPR Radio: settings and listening progress (roams with the user).</summary>
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HPR Radio");

    /// <summary>%LOCALAPPDATA%\HPR Radio\Cache: saved channel list, cover art (safe to delete).</summary>
    public static readonly string Cache = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HPR Radio", "Cache");

    public static readonly string Art = Path.Combine(Cache, "art");

    /// <summary>Next to HprRadio.exe.</summary>
    public static readonly string AppDir = AppContext.BaseDirectory;

    public static void WriteAtomic(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) { Log.Write($"write {path}: {e.Message}"); }
    }

    public static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
        catch { return null; }
    }
}

/// <summary>Debug log: written only when HPR_DEBUG is set or during the self-test.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static string? FilePath { get; set; } =
        Environment.GetEnvironmentVariable("HPR_DEBUG") is { Length: > 0 }
            ? Path.Combine(Path.GetTempPath(), "hpr-radio.log") : null;

    public static double Seconds => Clock.Elapsed.TotalSeconds;

    public static void Write(string line)
    {
        if (FilePath is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"[{Seconds,6:F1}] {line}{Environment.NewLine}"); }
            catch { }
        }
    }
}

public static class AppInfo
{
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0)).ToString(3);

    public static string UserAgent => $"HPRRadio/{Version} (Windows; +https://hpr.org)";
}

/// <summary>User settings, %APPDATA%\HPR Radio\settings.json.</summary>
public sealed class Settings
{
    public const string DefaultConfigUrl = "https://hpr.org/apps/android/channels.json";
    private readonly string _file = Path.Combine(Paths.Data, "settings.json");
    private readonly JsonObject _data;

    public Settings()
    {
        try { _data = JsonNode.Parse(Paths.ReadText(_file) ?? "{}") as JsonObject ?? new JsonObject(); }
        catch { _data = new JsonObject(); }
    }

    /// <summary>For testing: HPR_CONFIG_URL, then --config-url, then settings, then hpr.org.</summary>
    public string? OverrideConfigUrl { get; set; } = Environment.GetEnvironmentVariable("HPR_CONFIG_URL") is { Length: > 0 } u ? u : null;

    public string ConfigUrl => OverrideConfigUrl ?? GetString("config_url") ?? DefaultConfigUrl;

    public string? GetString(string key) => _data[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public double GetDouble(string key, double fallback) =>
        _data[key] is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    public bool GetBool(string key, bool fallback) =>
        _data[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    public int GetStreamIndex(string stationId) =>
        _data["stream_index"] is JsonObject o && o[stationId] is JsonValue v && v.TryGetValue(out int i) ? i : 0;

    public void SetStreamIndex(string stationId, int index)
    {
        if (_data["stream_index"] is not JsonObject o) { o = new JsonObject(); _data["stream_index"] = o; }
        o[stationId] = index;
        Save();
    }

    public void Set(string key, JsonNode? value) { _data[key] = value; Save(); }

    private void Save() => Paths.WriteAtomic(_file, _data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>HTTP helpers. Every failure comes back as null; callers decide what to show.</summary>
public static class Net
{
    private static readonly HttpClient Client = Create();

    // Separate limits so a long tail of cover-art lookups never delays what the user is waiting on.
    private static readonly SemaphoreSlim ArtLane = new(4);

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        return c;
    }

    public static async Task<byte[]?> GetBytesAsync(string url, int timeoutSeconds = 10, int maxBytes = 2_000_000)
    {
        var started = Log.Seconds;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { Log.Write($"net {(int)resp.StatusCode} {url}"); return null; }
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buf = new byte[16384];
            int n;
            while ((n = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > maxBytes) break;
            }
            Log.Write($"net ok {ms.Length}B in {Log.Seconds - started:F1}s {url}");
            return ms.ToArray();
        }
        catch (Exception e)
        {
            Log.Write($"net fail {e.GetType().Name} {url}");
            return null;
        }
    }

    public static async Task<string?> GetTextAsync(string url, int timeoutSeconds = 10, int maxBytes = 2_000_000)
    {
        var b = await GetBytesAsync(url, timeoutSeconds, maxBytes).ConfigureAwait(false);
        return b is null ? null : Encoding.UTF8.GetString(b);
    }

    /// <summary>Same as GetTextAsync, but waits its turn behind at most 3 other art requests.</summary>
    public static async Task<string?> GetArtTextAsync(string url)
    {
        await ArtLane.WaitAsync().ConfigureAwait(false);
        try { return await GetTextAsync(url).ConfigureAwait(false); }
        finally { ArtLane.Release(); }
    }

    public static async Task<byte[]?> GetArtBytesAsync(string url)
    {
        await ArtLane.WaitAsync().ConfigureAwait(false);
        try { return await GetBytesAsync(url, 15, 5_000_000).ConfigureAwait(false); }
        finally { ArtLane.Release(); }
    }

    private static readonly Regex Tag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>"Loretta Lynn &amp;amp; &lt;b&gt;Conway&lt;/b&gt;" -> "Loretta Lynn &amp; Conway"</summary>
    public static string? HtmlToText(string? s)
    {
        if (s is null) return null;
        var t = WebUtility.HtmlDecode(Tag.Replace(s, " ")).Replace(' ', ' ');
        t = Spaces.Replace(t, " ").Trim();
        return t.Length == 0 ? null : t;
    }
}
