using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TaskbarLyrics.Services;

/// <summary>
/// 网易云音乐 API 封装（搜索 / 歌词 / 封面）。
/// 使用 web 端 cloudsearch/pc 接口并携带 Referer，避免旧版 api/search/get 被风控拦截的问题。
/// 增强：搜索接口增加业务 code 校验 + 备用 web 接口；歌词接口带基础 Cookie 并支持多接口切换。
/// </summary>
public class NeteaseApi : INeteaseApi
{
    private readonly HttpClient _http;
    private readonly ILogger<NeteaseApi> _logger;

    public NeteaseApi(ILogger<NeteaseApi>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NeteaseApi>.Instance;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2) // 缩短超时,避免切歌时等待过久
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) TaskbarLyrics/2.0");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com");
        // 模拟 web 端基础 Cookie，降低接口被风控/返回 -460 的概率
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie",
            "os=pc; appver=2.9.7; NMTID=00O1QmCbaUpF3g1e8xHf3e5g4a2b1c0d");
    }

    /// <summary>搜索结果条目。</summary>
    public sealed record SearchSong(long Id, string Name, string Artist, long DurationMs, string AlbumPicUrl);

    /// <summary>按 歌名+歌手 搜索，返回最多 limit 条结果（按相关度排序）。主接口失败自动切备用接口。</summary>
    public async Task<List<SearchSong>> SearchSongsAsync(string title, string artist, int limit = 10)
    {
        var result = await SearchCloudsearchAsync(title, artist, limit);
        if (result.Count > 0) return result;

        // 主接口被风控/无结果时，改用 web 端搜索接口重试一次
        result = await SearchGetWebAsync(title, artist, limit);
        if (result.Count > 0) _logger.LogDebug("网易云搜索：主接口无效，备用接口命中 {Title}|{Artist}", title, artist);
        return result;
    }

    /// <summary>主搜索接口：cloudsearch/pc（POST 表单）。</summary>
    private async Task<List<SearchSong>> SearchCloudsearchAsync(string title, string artist, int limit)
    {
        var result = new List<SearchSong>();
        try
        {
            var query = $"{title} {artist}".Trim();
            if (query.Length == 0) return result;

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["s"] = query,
                ["type"] = "1",
                ["offset"] = "0",
                ["limit"] = limit.ToString(),
                ["total"] = "true"
            });
            using var resp = await _http.PostAsync("https://music.163.com/api/cloudsearch/pc", form);
            if (!resp.IsSuccessStatusCode) return result;

            var json = await resp.Content.ReadAsStringAsync();
            ParseSongs(json, result);
            if (result.Count == 0) _logger.LogDebug("网易云 cloudsearch 无结果 {Title}|{Artist}", title, artist);
        }
        catch (Exception ex) { _logger.LogDebug("网易云 cloudsearch 异常: {Msg}", ex.Message); }
        return result;
    }

    /// <summary>备用搜索接口：search/get/web（GET）。</summary>
    private async Task<List<SearchSong>> SearchGetWebAsync(string title, string artist, int limit)
    {
        var result = new List<SearchSong>();
        try
        {
            var query = $"{title} {artist}".Trim();
            if (query.Length == 0) return result;

            var url = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&offset=0&limit={limit}";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return result;

            var json = await resp.Content.ReadAsStringAsync();
            ParseSongs(json, result);
        }
        catch (Exception ex) { _logger.LogDebug("网易云 search/get/web 异常: {Msg}", ex.Message); }
        return result;
    }

    private static void ParseSongs(string json, List<SearchSong> result)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 校验业务 code：非 200（如 -460 风控）直接视为无结果
        if (root.TryGetProperty("code", out var codeEl) &&
            codeEl.ValueKind == JsonValueKind.Number &&
            codeEl.TryGetInt32(out var code) && code != 200)
            return;

        if (!root.TryGetProperty("result", out var r) ||
            !r.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array)
            return;

        foreach (var s in songs.EnumerateArray())
        {
            if (!s.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;

            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

            string artistName = "";
            if (s.TryGetProperty("ar", out var ar) && ar.ValueKind == JsonValueKind.Array && ar.GetArrayLength() > 0)
                artistName = ar[0].TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";

            long durationMs = 0;
            if (s.TryGetProperty("dt", out var dt) && dt.ValueKind == JsonValueKind.Number && dt.TryGetInt64(out var dm))
                durationMs = dm;

            string picUrl = "";
            if (s.TryGetProperty("al", out var al) && al.ValueKind == JsonValueKind.Object)
                picUrl = al.TryGetProperty("picUrl", out var pu) ? pu.GetString() ?? "" : "";

            result.Add(new SearchSong(idEl.GetInt64(), name, artistName, durationMs, picUrl));
        }
    }

    /// <summary>按歌曲 id 获取逐字歌词与翻译（原始 LRC 文本），失败返回 null。主接口失败自动切备用接口。</summary>
    public async Task<(string? Lrc, string? Translation)> FetchLyricAsync(long songId)
    {
        var urls = new[]
        {
            $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1",
            $"https://music.163.com/api/song/lyric?os=pc&id={songId}&lv=-1&kv=-1&tv=-1",
        };

        foreach (var url in urls)
        {
            var (lrc, tlyric, code) = await FetchLyricCoreAsync(url);
            if (lrc != null) return (lrc, tlyric);
            _logger.LogDebug("网易云歌词接口 id={Id} code={Code}", songId, code);
        }
        return (null, null);
    }

    private async Task<(string? Lrc, string? Translation, int Code)> FetchLyricCoreAsync(string url)
    {
        try
        {
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return (null, null, (int)resp.StatusCode);
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int code = root.TryGetProperty("code", out var c) &&
                       c.ValueKind == JsonValueKind.Number &&
                       c.TryGetInt32(out var cv)
                ? cv : 200;

            string? lrc = null;
            if (root.TryGetProperty("lrc", out var lrcEl) &&
                lrcEl.TryGetProperty("lyric", out var lyricEl) &&
                lyricEl.ValueKind == JsonValueKind.String)
                lrc = lyricEl.GetString();

            string? tlyric = null;
            if (root.TryGetProperty("tlyric", out var tlEl) &&
                tlEl.TryGetProperty("lyric", out var tLyricEl) &&
                tLyricEl.ValueKind == JsonValueKind.String)
                tlyric = tLyricEl.GetString();

            return (lrc, string.IsNullOrWhiteSpace(tlyric) ? null : tlyric, code);
        }
        catch (Exception ex) { _logger.LogDebug("网易云歌词接口异常: {Msg}", ex.Message); }
        return (null, null, 0);
    }
}
