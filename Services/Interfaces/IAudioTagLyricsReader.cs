using TaskbarLyrics.Models;

namespace TaskbarLyrics.Services;

/// <summary>音频标签读取(内嵌歌词/封面/时长)。</summary>
public interface IAudioTagLyricsReader
{
    (string Artist, string Title) ReadMetaCached(string filePath);
    Task<double> ReadDurationAsync(string filePath);
    Task<LyricsData?> ReadFromFileAsync(string filePath);
    Task<byte[]?> ReadMp3CoverAsync(string path);
    Task<byte[]?> ReadFlacCoverAsync(string path);
}
