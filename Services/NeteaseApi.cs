using System.Net.Http;
using System.Text.Json;

namespace TaskbarLyrics.Services;

/// <summary>
/// 网易云音乐 API 封装（搜索 / 歌词 / 封面）。
/// 使用 web 端 cloudsearch/pc 接口并携带 Referer，避免旧版 api/search/get 被风控拦截的问题。
/// </summary>
public class NeteaseApi : INeteaseApi
{
    private readonly HttpClient _http;

    public NeteaseApi()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2) // 缩短超时,避免切歌时等待过久
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) TaskbarLyrics/2.0");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com");
    }

    /// <summary>搜索结果条目。</summary>
    public sealed record SearchSong(long Id, string Name, string Artist, long DurationMs, string AlbumPicUrl);

    /// <summary>按 歌名+歌手 搜索，返回最多 limit 条结果（按相关度排序）。</summary>
    public async Task<List<SearchSong>> SearchSongsAsync(string title, string artist, int limit = 10)
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
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("result", out var r) ||
                !r.TryGetProperty("songs", out var songs) ||
                songs.ValueKind != JsonValueKind.Array)
                return result;

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
        catch { }
        return result;
    }

    /// <summary>按歌曲 id 获取逐字歌词与翻译（原始 LRC 文本），失败返回 null。</summary>
    public async Task<(string? Lrc, string? Translation)> FetchLyricAsync(long songId)
    {
        try
        {
            var url = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return (null, null);
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

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

            return (lrc, string.IsNullOrWhiteSpace(tlyric) ? null : tlyric);
        }
        catch { return (null, null); }
    }
}
