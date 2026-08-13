namespace TaskbarLyrics.Services;

/// <summary>网易云音乐 API(搜索/歌词)。</summary>
public interface INeteaseApi
{
    /// <summary>按 歌名+歌手 搜索，返回最多 limit 条结果（按相关度排序）。</summary>
    Task<List<NeteaseApi.SearchSong>> SearchSongsAsync(string title, string artist, int limit = 10);

    /// <summary>按歌曲 id 获取逐字歌词与翻译（原始 LRC 文本），失败返回 null。</summary>
    Task<(string? Lrc, string? Translation)> FetchLyricAsync(long songId);
}
