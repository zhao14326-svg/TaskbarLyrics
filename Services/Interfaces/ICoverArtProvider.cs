namespace TaskbarLyrics.Services;

/// <summary>封面获取（多源并行 + 打分择优）。</summary>
public interface ICoverArtProvider
{
    /// <summary>并行多源 + 打分择优获取封面：SMTC缩略图 / 本地 / 在线同时启动。</summary>
    Task<byte[]?> GetCoverAsync(MediaTrack track, IReadOnlyList<string> audioFiles, int strategy = 0);

    /// <summary>转 data URI（前端封面显示用）。</summary>
    string? ToDataUri(byte[]? bytes);
}
