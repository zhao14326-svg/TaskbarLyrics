using System.Text;
using System.Text.RegularExpressions;
using TaskbarLyrics.Models;

namespace TaskbarLyrics.Services;

/// <summary>解析标准 LRC 歌词文本（支持多时间戳行、offset 元数据、原词+翻译合并）</summary>
public static partial class LrcParser
{
    // 匹配 [mm:ss.xx] 时间标签（支持 . 或 : 分隔小数）
    [GeneratedRegex(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimeTagRegex();

    /// <summary>同一时间戳的原词与翻译行合并容差（毫秒）。</summary>
    public static readonly TimeSpan TranslationTolerance = TimeSpan.FromMilliseconds(60);

    /// <summary>解析 LRC 文本为歌词数据</summary>
    public static LyricsData Parse(string lrc)
    {
        var result = new LyricsData { Source = "LRC歌词" };
        if (string.IsNullOrWhiteSpace(lrc))
            return result;

        var lines = lrc.Split('\n');

        long offsetMs = 0;
        foreach (var raw in lines)
        {
            var m = Regex.Match(raw.TrimEnd('\r'), @"\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var off))
                offsetMs = off;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (Regex.IsMatch(line, @"^\[(ti|ar|al|by|re|ve|length|t_time|offset):", RegexOptions.IgnoreCase))
                continue;

            var matches = TimeTagRegex().Matches(line);
            if (matches.Count == 0)
            {
                // 无时间戳的行通常是元数据/歌词整理信息，跳过（避免全部堆到 0 时刻）
                continue;
            }

            var text = TimeTagRegex().Replace(line, "").Trim();
            if (text.Length == 0) continue; // 空歌词行（如 [00:00.00]）无意义，跳过

            foreach (Match m in matches)
            {
                var time = ParseTimeTag(m);
                if (time < TimeSpan.Zero) time = TimeSpan.Zero;
                result.Lines.Add(new LyricLine(time + TimeSpan.FromMilliseconds(offsetMs), text));
            }
        }

        // 稳定排序（保持同一时间戳行的原始先后顺序：原词在前、翻译在后）
        var sorted = result.Lines.OrderBy(l => l.Time).ToList();
        result.Lines.Clear();
        result.Lines.AddRange(sorted);
        return result;
    }

    /// <summary>
    /// 合并内联翻译：同一时间戳（60ms 容差）内紧随的连续行视为前一行原词的翻译，
    /// 挂到 Translation 字段，不再作为独立歌词行（否则会导致歌词显示与播放错位）。
    /// </summary>
    public static void MergeInlineTranslations(LyricsData data)
    {
        if (data.Lines.Count <= 1) return;

        var merged = new List<LyricLine>(data.Lines.Count);
        foreach (var line in data.Lines)
        {
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                if (prev.Translation == null && line.Time - prev.Time <= TranslationTolerance)
                {
                    merged[^1] = prev with { Translation = line.Text };
                    continue;
                }
            }
            merged.Add(line);
        }

        if (merged.Count != data.Lines.Count)
        {
            data.Lines.Clear();
            data.Lines.AddRange(merged);
        }
    }

    /// <summary>
    /// 把独立的翻译歌词文本（如网易云 tlyric）按时间戳作为独立歌词行插入，
    /// 与原词同时间戳、紧随其后 —— 悬浮窗直接显示两行歌词（原词 + 翻译）。
    /// </summary>
    public static void ApplyTranslation(LyricsData data, string? translationLrc)
    {
        if (string.IsNullOrWhiteSpace(translationLrc)) return;

        var translations = ParseTimeLines(translationLrc);
        if (translations.Count == 0) return;

        foreach (var tr in translations)
            data.Lines.Add(new LyricLine(tr.Time, tr.Text));

        // 稳定排序：原词在前、翻译在后
        var sorted = data.Lines.OrderBy(l => l.Time).ToList();
        data.Lines.Clear();
        data.Lines.AddRange(sorted);
    }

    /// <summary>仅解析歌词中的“时间戳+文本”行（用于翻译文本），忽略元数据。</summary>
    private static List<(TimeSpan Time, string Text)> ParseTimeLines(string lrc)
    {
        var result = new List<(TimeSpan, string)>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (Regex.IsMatch(line, @"^\[(ti|ar|al|by|re|ve|length|t_time|offset):", RegexOptions.IgnoreCase))
                continue;

            var matches = TimeTagRegex().Matches(line);
            if (matches.Count == 0) continue;
            var text = TimeTagRegex().Replace(line, "").Trim();
            if (text.Length == 0) continue;

            foreach (Match m in matches)
                result.Add((ParseTimeTag(m), text));
        }
        return result;
    }

    private static TimeSpan ParseTimeTag(Match m)
    {
        int min = int.Parse(m.Groups[1].Value);
        int sec = int.Parse(m.Groups[2].Value);
        double frac = 0;
        if (m.Groups[3].Success && m.Groups[3].Length > 0)
        {
            var fracStr = m.Groups[3].Value.PadRight(3, '0');
            frac = int.Parse(fracStr) / 1000.0;
        }
        return TimeSpan.FromMilliseconds((min * 60 + sec) * 1000 + frac * 1000);
    }

    public static bool LooksLikeLrc(string text)
    {
        return TimeTagRegex().IsMatch(text);
    }

    /// <summary>
    /// 解析歌词文本：带时间戳按标准 LRC 解析；纯文本（无时间戳）则按每行固定间隔生成时间戳。
    /// plainLineIntervalSec 为 0 时使用默认 4 秒；调用方可传入歌曲总时长估算的间隔。
    /// 纯文本歌词标记 IsSynced=false（歌词与音频内容无法精确对齐，仅按时长分布）。
    /// </summary>
    public static LyricsData ParseLyrics(string text, double plainLineIntervalSec = 0)
    {
        if (LooksLikeLrc(text))
            return Parse(text);

        if (plainLineIntervalSec <= 0) plainLineIntervalSec = 4;
        var result = new LyricsData { Source = "歌词", IsSynced = false, RawText = text };
        int index = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            result.Lines.Add(new LyricLine(TimeSpan.FromSeconds(index++ * plainLineIntervalSec), line));
        }
        return result;
    }

    /// <summary>
    /// 纯文本歌词按歌曲总时长估算每行间隔（限制 1.5~8 秒，未知时长时默认 4 秒）。
    /// 使纯文本歌词整体覆盖整首歌，而非固定 4 秒导致末尾无歌词或分布不均。
    /// </summary>
    public static double EstimatePlainInterval(double durationSec, string text)
    {
        if (durationSec <= 10) return 4;
        int lines = 0;
        foreach (var raw in text.Split('\n'))
            if (!string.IsNullOrWhiteSpace(raw.Trim())) lines++;
        if (lines <= 0) return 4;
        return Math.Clamp(durationSec / lines, 1.5, 8);
    }

    /// <summary>Extract [ti:Title] and [ar:Artist] from LRC metadata.</summary>
    public static (string Title, string Artist) GetMetadata(string lrc)
    {
        string title = "", artist = "";
        var ti = Regex.Match(lrc, @"\[ti:\s*(.+?)\]", RegexOptions.IgnoreCase);
        if (ti.Success) title = ti.Groups[1].Value.Trim();
        var ar = Regex.Match(lrc, @"\[ar:\s*(.+?)\]", RegexOptions.IgnoreCase);
        if (ar.Success) artist = ar.Groups[1].Value.Trim();
        return (title, artist);
    }
}
