using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaskbarLyrics.Models;

namespace TaskbarLyrics.Services;

/// <summary>
/// 在线歌词获取。主源：lrclib.net（稳定、免费）；备源：网易云音乐搜索。
/// </summary>
public partial class OnlineLyricsService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public OnlineLyricsService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 TaskbarLyrics/2.0");
    }

    public async Task<LyricsData?> FetchAsync(string title, string artist, string? album = null, double durationSec = 0)
    {
        // lrclib 精确 / 网易云 / lrclib 宽搜 全部并行，谁先返回有效结果用谁
        // （各请求自带超时，不做整体裁断，避免慢网络下误判“无歌词”）
        var tasks = new List<Task<LyricsData?>>
        {
            FetchFromLrcLib(title, artist, album, durationSec),
            FetchFromNetease(title, artist, durationSec),
            FetchFromLrcLibSearch(title, artist, durationSec),
        };

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            var result = await done;
            if (result is { IsEmpty: false }) return result;
        }
        return null;
    }

    // ==================== lrclib.net exact match ====================

    private async Task<LyricsData?> FetchFromLrcLib(string title, string artist, string? album, double durationSec)
    {
        try
        {
            var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            if (!string.IsNullOrWhiteSpace(album))
                url += $"&album_name={Uri.EscapeDataString(album)}";
            if (durationSec > 0)
                url += $"&duration={durationSec:F0}";

            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            return ParseLrcLibResponse(json, durationSec);
        }
        catch { return null; }
    }

    // ==================== lrclib.net broad search ====================

    private async Task<LyricsData?> FetchFromLrcLibSearch(string title, string artist, double durationSec)
    {
        try
        {
            var url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(title + " " + artist)}";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return null;

            var titleNorm = Normalize(title);
            var artistNorm = Normalize(artist);

            // Find best match from search results
            int bestIdx = -1, bestScore = 0;
            for (int i = 0; i < root.GetArrayLength(); i++)
            {
                var item = root[i];
                var t = Normalize(item.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "");
                var a = Normalize(item.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "");
                var dur = item.TryGetProperty("duration", out var d) ? d.GetDouble() : 0;

                int score = 0;
                if (t == titleNorm) score += 100;
                else if (t.Contains(titleNorm) || titleNorm.Contains(t)) score += 50;
                if (a == artistNorm) score += 50;
                else if (a.Contains(artistNorm) || artistNorm.Contains(a)) score += 25;

                // 兼容“歌手-歌名”被窗口标题颠倒解析：歌名/歌手交叉匹配
                if (t == artistNorm) score += 60;
                else if (t.Contains(artistNorm) || artistNorm.Contains(t)) score += 30;
                if (a == titleNorm) score += 40;
                else if (a.Contains(titleNorm) || titleNorm.Contains(a)) score += 20;

                if (durationSec > 0 && dur > 0 && Math.Abs(dur - durationSec) < 10)
                    score += 30;

                if (score > bestScore) { bestScore = score; bestIdx = i; }
            }

            if (bestIdx < 0 || bestScore < 20) return null;

            // 拉取最佳匹配的歌词（优先逐字 LRC，其次纯文本）
            var best = root[bestIdx];
            var synced = best.TryGetProperty("syncedLyrics", out var sl) ? sl.GetString() : null;
            var plain = best.TryGetProperty("plainLyrics", out var pl) ? pl.GetString() : null;

            var raw = !string.IsNullOrWhiteSpace(synced) ? synced : plain;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var data = LrcParser.ParseLyrics(raw, LrcParser.EstimatePlainInterval(durationSec, raw));
            data.Source = "网络歌词(lrclib)";
            return data;
        }
        catch { return null; }
    }

    private static LyricsData? ParseLrcLibResponse(string json, double durationSec)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var synced = root.TryGetProperty("syncedLyrics", out var s) ? s.GetString() : null;
        var plain = root.TryGetProperty("plainLyrics", out var p) ? p.GetString() : null;

        var raw = !string.IsNullOrWhiteSpace(synced) ? synced : plain;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var data = LrcParser.ParseLyrics(raw, LrcParser.EstimatePlainInterval(durationSec, raw));
        data.Source = "网络歌词(lrclib)";
        return data;
    }

    // ==================== NetEase fallback ====================

    private async Task<LyricsData?> FetchFromNetease(string title, string artist, double durationSec)
    {
        var songs = await NeteaseApi.SearchSongsAsync(title, artist, 10);
        if (songs.Count == 0) return null;

        // 标题 + 歌手 + 时长加权打分，选择最匹配的一首
        var titleNorm = Normalize(title);
        var artistNorm = Normalize(artist);
        long bestId = 0;
        int bestScore = 0;
        foreach (var s in songs)
        {
            var sName = Normalize(s.Name);
            var sArtist = Normalize(s.Artist);

            int score = 0;
            if (sName == titleNorm) score += 100;
            else if (sName.Contains(titleNorm, StringComparison.Ordinal)) score += 50;
            else if (titleNorm.Contains(sName, StringComparison.Ordinal) && sName.Length >= 2) score += 25;
            if (sArtist == artistNorm) score += 50;
            else if (sArtist.Contains(artistNorm, StringComparison.Ordinal)) score += 20;

            // 兼容“歌手-歌名”被窗口标题颠倒解析：歌名/歌手交叉匹配
            if (sName == artistNorm) score += 60;
            else if (sName.Contains(artistNorm, StringComparison.Ordinal)) score += 30;
            if (sArtist == titleNorm) score += 40;
            else if (sArtist.Contains(titleNorm, StringComparison.Ordinal)) score += 20;

            if (durationSec > 0 && s.DurationMs > 0 && Math.Abs(s.DurationMs / 1000.0 - durationSec) < 10)
                score += 30;

            if (score > bestScore) { bestScore = score; bestId = s.Id; }
        }

        if (bestId <= 0 || bestScore < 10) return null;

        var (lrcText, tlyric) = await NeteaseApi.FetchLyricAsync(bestId);
        if (string.IsNullOrWhiteSpace(lrcText)) return null;

        var data = LrcParser.ParseLyrics(lrcText, LrcParser.EstimatePlainInterval(durationSec, lrcText));
        LrcParser.ApplyTranslation(data, tlyric);
        data.Source = "网易云音乐";
        return data;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex NonWordRegex();

    private static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return NonWordRegex().Replace(text, "").ToLowerInvariant();
    }
}
