namespace TaskbarLyrics.Services;

/// <summary>本地播放器 API(网易云)。</summary>
public interface IPlayerLocalApiService
{
    /// <summary>尝试通过本地 API 获取当前播放信息。</summary>
    Task<MediaTrack?> GetTrackAsync();

    /// <summary>从网易云本地 API 直接获取歌词原文（LRC 文本）。</summary>
    Task<string?> GetLyricsAsync();
}
