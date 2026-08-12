using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TaskbarLyrics.Services;

/// <summary>
/// 直接查询本地播放器的播放状态（SMTC 不可用时的备选方案）。
/// 网易云音乐通过本地 HTTP API（端口从配置文件读取）。
/// </summary>
public static class PlayerLocalApiService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(800) };
    private static int _neteasePort;
    private static DateTime _neteasePortCheck;

    /// <summary>尝试通过本地 API 获取当前播放信息。</summary>
    public static async Task<MediaTrack?> GetTrackAsync()
    {
        var track = await GetFromNeteaseApi();
        if (track != null) return track;
        return null;
    }

    /// <summary>Try to get lyrics directly from NetEase local API. Returns raw LRC text.</summary>
    public static async Task<string?> GetLyricsAsync()
    {
        if (_neteasePort == 0 && (DateTime.UtcNow - _neteasePortCheck).TotalSeconds < 30)
            return null;
        _neteasePortCheck = DateTime.UtcNow;

        try
        {
            if (Process.GetProcessesByName("cloudmusic").Length == 0) return null;
            if (_neteasePort == 0) _neteasePort = FindNeteasePort();
            if (_neteasePort <= 0) return null;

            // Try BetterNCM lyric endpoints
            var lyricUrls = new[]
            {
                $"http://127.0.0.1:{_neteasePort}/api/player/lyric",
                $"http://127.0.0.1:{_neteasePort}/api/music/lyric",
            };

            // 并行请求两个端点（各 800ms 超时），最坏情况从 1.6s 降到 0.8s
            var jsonResponses = await Task.WhenAll(lyricUrls.Select(async url =>
            {
                try { return await _http.GetStringAsync(url); }
                catch { return ""; }
            }));
            foreach (var json in jsonResponses)
            {
                if (string.IsNullOrEmpty(json)) continue;
                try
                {
                    var lrcText = ParseLyricFromJson(json);
                    if (!string.IsNullOrWhiteSpace(lrcText)) return lrcText;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string? ParseLyricFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // { data: { lyric: "..." } }
            if (root.TryGetProperty("data", out var data))
            {
                // Try various lyric field names
                if (data.TryGetProperty("lyric", out var lr) && lr.ValueKind == JsonValueKind.String)
                    return lr.GetString();
                if (data.TryGetProperty("lrc", out var lrc) && lrc.ValueKind == JsonValueKind.String)
                {
                    var lrcObj = JsonDocument.Parse(lrc.GetString()!);
                    if (lrcObj.RootElement.TryGetProperty("lyric", out var ll) && ll.ValueKind == JsonValueKind.String)
                        return ll.GetString();
                }
            }

            // { lyric: "..." }
            if (root.TryGetProperty("lyric", out var lyric) && lyric.ValueKind == JsonValueKind.String)
                return lyric.GetString();
            if (root.TryGetProperty("lrc", out var lrc2) && lrc2.ValueKind == JsonValueKind.String)
            {
                try
                {
                    var lrcObj = JsonDocument.Parse(lrc2.GetString()!);
                    if (lrcObj.RootElement.TryGetProperty("lyric", out var ll) && ll.ValueKind == JsonValueKind.String)
                        return ll.GetString();
                }
                catch { return lrc2.GetString(); }
            }
        }
        catch { }
        return null;
    }

    // ==================== 网易云本地 API ====================

    private static async Task<MediaTrack?> GetFromNeteaseApi()
    {
        // 30 秒缓存：已尝试过且失败就不再重复扫描
        if (_neteasePort == 0 && (DateTime.UtcNow - _neteasePortCheck).TotalSeconds < 30)
            return null;
        _neteasePortCheck = DateTime.UtcNow;

        try
        {
            if (Process.GetProcessesByName("cloudmusic").Length == 0) return null;

            // 查找端口
            if (_neteasePort == 0) _neteasePort = FindNeteasePort();
            if (_neteasePort <= 0) return null;

            // 查询播放状态 API
            var urls = new[]
            {
                $"http://127.0.0.1:{_neteasePort}/api/player/status",
                $"http://127.0.0.1:{_neteasePort}/player",
            };

            foreach (var url in urls)
            {
                try
                {
                    var json = await _http.GetStringAsync(url);
                    var track = ParseNeteaseJson(json);
                    if (track != null) return track;
                }
                catch { }
            }

            // 如果端口不可达，下次重新扫描
            _neteasePort = 0;
        }
        catch { }
        return null;
    }

    /// <summary>读取网易云本地端口配置文件。</summary>
    private static int FindNeteasePort()
    {
        try
        {
            var portFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Netease\CloudMusic\webdata\port");
            if (File.Exists(portFile))
            {
                var content = File.ReadAllText(portFile).Trim();
                if (int.TryParse(content, out var p) && p > 0) return p;
            }
        }
        catch { }
        return 0;
    }

    private static MediaTrack? ParseNeteaseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? title = null, artist = null, album = null;
            bool playing = false;
            long progressMs = 0, durationMs = 0;

            // { data: { title, artist, album, playing, progress, duration } }
            if (root.TryGetProperty("data", out var data))
            {
                title = data.TryGetProperty("title", out var t) ? t.GetString() : null;
                artist = data.TryGetProperty("artist", out var a) ? a.GetString() : null;
                album = data.TryGetProperty("album", out var al) ? al.GetString() : null;
                playing = data.TryGetProperty("playing", out var pl) && pl.GetBoolean();
                if (data.TryGetProperty("progress", out var pg) && pg.TryGetInt64(out var pm))
                    progressMs = pm;
                if (data.TryGetProperty("duration", out var dr) && dr.TryGetInt64(out var dm))
                    durationMs = dm;
            }

            // { track: { name, artists: [{name}] } }
            if (string.IsNullOrEmpty(title) && root.TryGetProperty("track", out var track))
            {
                title = track.TryGetProperty("name", out var tn) ? tn.GetString() : null;
                if (track.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    foreach (var a in arts.EnumerateArray())
                        if (a.TryGetProperty("name", out var an)) names.Add(an.GetString() ?? "");
                    artist = string.Join(", ", names);
                }
            }

            if (string.IsNullOrWhiteSpace(title)) return null;

            return new MediaTrack(title!, artist ?? "", album ?? "", "NeteaseLocal",
                playing ? "Playing" : "Paused",
                TimeSpan.FromMilliseconds(progressMs),
                TimeSpan.FromMilliseconds(durationMs),
                null);
        }
        catch { return null; }
    }
}
