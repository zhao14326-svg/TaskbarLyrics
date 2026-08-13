namespace TaskbarLyrics.Services;

/// <summary>
/// 本地播放器 API 读取封装（网易云本地 HTTP API）：
/// 播放器关闭窗口且 SMTC 不可用时，从本地 API 获取当前曲目与播放状态。
/// </summary>
public class LocalApiResolver
{
    private readonly IPlayerLocalApiService _localApi;

    public LocalApiResolver(IPlayerLocalApiService localApi)
    {
        _localApi = localApi;
    }

    /// <summary>从本地播放器 API 获取当前曲目（含准确的播放/暂停状态）。</summary>
    public Task<MediaTrack?> GetTrackAsync() => _localApi.GetTrackAsync();
}
