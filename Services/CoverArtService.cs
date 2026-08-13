using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TaskbarLyrics.Services;

/// <summary>封面获取(多源并行 + 打分择优)。</summary>
public interface ICoverArtService
{
    /// <summary>并行多源 + 打分择优获取封面：SMTC缩略图 / 本地 / 在线同时启动。</summary>
    Task<byte[]?> GetCoverAsync(MediaTrack track, IReadOnlyList<string> audioFiles, int strategy = 0);

    /// <summary>转 data URI（前端封面显示用）。</summary>
    string? ToDataUri(byte[]? bytes);
}

/// <summary>
/// Album cover art provider with configurable source strategy.
/// 0=online-first, 1=local-first, 2=online-only, 3=local-only
/// </summary>
public class CoverArtService : ICoverArtService
{
    private readonly INeteaseApi _netease;
    private readonly IAudioTagLyricsReader _tagReader;
    private readonly HttpClient _http;
    private readonly Dictionary<string, byte[]> _cache = new();
    private readonly Queue<string> _accessOrder = new();
    private const int MaxCache = 50;
    private static readonly string[] ImageExts = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];
    private static readonly string[] CoverNames = ["cover", "album", "folder", "front", "artwork"];

    public CoverArtService(INeteaseApi netease, IAudioTagLyricsReader tagReader)
    {
        _netease = netease;
        _tagReader = tagReader;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 TaskbarLyrics/2.0");
    }

    /// <summary>
    /// 并行多源 + 打分择优获取封面：SMTC缩略图 / 本地 / 在线同时启动，
    /// 按分值(可靠性+策略优先级)从高到低取首个有效结果 —— 高分来源成功即返回，不等待慢的低分来源。
    /// </summary>
    public async Task<byte[]?> GetCoverAsync(MediaTrack track, IReadOnlyList<string> audioFiles, int strategy = 0)
    {
        var key = LyricsManager.Normalize($"cover|{track.Title}|{track.Artist}");
        if (_cache.TryGetValue(key, out var c)) return c;

        var sources = new List<(int Score, Task<byte[]?> Task)>();

        bool includeThumb = strategy != 3;   // local-only 不含缩略图
        bool includeLocal = strategy != 2;   // online-only 不含本地
        bool includeOnline = strategy != 3;  // local-only 不含在线

        // SMTC 缩略图：播放器直接提供，最可靠（几乎即时）
        if (includeThumb && track.ThumbnailBytes is { Length: > 100 })
            sources.Add((strategy == 1 ? 90 : 100, Task.FromResult<byte[]?>(track.ThumbnailBytes)));

        // 本地封面/内嵌（后台任务）
        if (includeLocal)
            sources.Add((strategy == 1 ? 100 : 80, Task.Run(() => LocalOnly(track, audioFiles))));

        // 在线封面（后台任务）
        if (includeOnline)
            sources.Add((strategy == 0 ? 95 : 60, Task.Run(() => FetchCoverFromNeteaseAsync(track.Title, track.Artist))));

        if (sources.Count == 0) return null;

        // 并行已启动；按分值从高到低取首个有效结果
        foreach (var item in sources.OrderByDescending(s => s.Score))
        {
            try
            {
                var bytes = await item.Task;
                if (bytes is { Length: > 100 })
                {
                    PutCache(key, bytes);
                    return bytes;
                }
            }
            catch { }
        }
        return null;
    }

    public string? ToDataUri(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        var mime = SniffMime(bytes);
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    // ==================== 来源实现 ====================

    private async Task<byte[]?> LocalOnly(MediaTrack track, IReadOnlyList<string> audioFiles)
    {
        if (audioFiles.Count == 0) return null;
        var bestFile = LyricsManager.FindBestAudioFile(track, audioFiles, _tagReader);
        if (bestFile == null) return null;

        // 1. Same-directory image files
        var dir = Path.GetDirectoryName(bestFile);
        if (dir != null)
        {
            foreach (var ext in ImageExts)
            {
                foreach (var name in CoverNames)
                {
                    var imgPath = Path.Combine(dir, name + ext);
                    if (File.Exists(imgPath))
                        return Helpers.ImageDecoder.DecodeFile(imgPath);
                }
                // Also scan all image files in dir, pick first match
                try
                {
                    var imgs = Directory.GetFiles(dir, "*" + ext).Take(5);
                    foreach (var img in imgs)
                    {
                        var fn = Path.GetFileNameWithoutExtension(img).ToLowerInvariant();
                        if (CoverNames.Any(n => fn.Contains(n)))
                            return Helpers.ImageDecoder.DecodeFile(img);
                    }
                    // Fallback: first image file
                    var first = Directory.GetFiles(dir, "*" + ext).FirstOrDefault();
                    if (first != null)
                        return Helpers.ImageDecoder.DecodeFile(first);
                }
                catch { }
            }
        }

        // 2. Embedded cover
        var emb = await ReadCoverFromFileAsync(bestFile);
        if (emb != null) return emb;

        return null;
    }

    private async Task<byte[]?> ReadCoverFromFileAsync(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".mp3" => await _tagReader.ReadMp3CoverAsync(filePath),
                ".flac" => await _tagReader.ReadFlacCoverAsync(filePath),
                _ => null
            };
        }
        catch { return null; }
    }

    private string SniffMime(byte[] bytes)
    {
        if (bytes.Length < 4) return "image/jpeg";
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes.Length > 12 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";
        if (bytes[0] == 0x42 && bytes[1] == 0x4D) return "image/bmp";
        return "image/jpeg";
    }

    private void PutCache(string key, byte[] data)
    {
        if (_cache.ContainsKey(key)) { _cache[key] = data; return; }
        while (_cache.Count >= MaxCache && _accessOrder.Count > 0)
            _cache.Remove(_accessOrder.Dequeue());
        _cache[key] = data;
        _accessOrder.Enqueue(key);
    }

    /// <summary>Fetch album cover from NetEase Cloud Music API (with scoring).</summary>
    private async Task<byte[]?> FetchCoverFromNeteaseAsync(string title, string artist)
    {
        var songs = await _netease.SearchSongsAsync(title, artist, 5);
        if (songs.Count == 0) return null;

        var titleNorm = LyricsManager.Normalize(title);
        foreach (var s in songs)
        {
            if (string.IsNullOrEmpty(s.AlbumPicUrl)) continue;

            // 标题匹配校验后再下载
            var sName = LyricsManager.Normalize(s.Name);
            if (titleNorm.Length > 0 && sName.Length > 0 &&
                !sName.Contains(titleNorm, StringComparison.Ordinal) &&
                !titleNorm.Contains(sName, StringComparison.Ordinal))
                continue; // 跳过明显不匹配的结果

            // 升级 HTTPS 并请求 200x200 缩略图
            var url = s.AlbumPicUrl.Replace("http://", "https://");
            if (!url.Contains("?param="))
                url += "?param=200y200";

            var imgBytes = await _http.GetByteArrayAsync(url);
            if (imgBytes is { Length: > 500 }) return imgBytes;
        }
        return null;
    }
}
