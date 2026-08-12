namespace TaskbarLyrics.Models;

/// <summary>
/// 一行带时间戳的歌词。Translation 为同一时间戳的原词翻译（可选）。
/// </summary>
public record LyricLine(TimeSpan Time, string Text, string? Translation = null);
