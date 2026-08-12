namespace TaskbarLyrics.Models;

/// <summary>完整的歌词数据</summary>
public class LyricsData
{
    /// <summary>已按时间排序的歌词行</summary>
    public List<LyricLine> Lines { get; } = new();

    /// <summary>歌词来源说明</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// 是否有真实时间戳（同步歌词）。纯文本歌词按估算间隔合成时间戳时为 false，
    /// 表示歌词与音频内容无法精确对齐（仅按时长分布估算）。
    /// </summary>
    public bool IsSynced { get; set; } = true;

    /// <summary>纯文本歌词的原始文本（无时间戳）。用于缓存时保留“非同步”标记。</summary>
    public string? RawText { get; set; }

    /// <summary>用于持久化缓存的文本：同步歌词存 LRC；纯文本存原文（保持非同步标记）。</summary>
    public string GetStoreText() => IsSynced ? ToLrcText() : (RawText ?? ToLrcText());

    /// <summary>是否无歌词</summary>
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>
    /// 按播放位置查找当前应显示的歌词行。位置早于第一行时返回第一行。
    /// 同一时间戳内的多行（原词+翻译）视为一组，返回组内第一行（原词），翻译行作为下一行显示。
    /// </summary>
    public LyricLine? GetLineAt(TimeSpan position)
    {
        if (Lines.Count == 0)
            return null;

        int idx = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Time <= position)
                idx = i;
            else
                break;
        }
        if (idx < 0)
            return Lines[0];

        // 回到同一时间戳组的第一行（原词），避免把翻译行当作当前行
        while (idx > 0 && Lines[idx].Time - Lines[idx - 1].Time <= TimeSpan.FromMilliseconds(60))
            idx--;
        return Lines[idx];
    }

    /// <summary>获取当前行的下一行（用于显示下一句歌词）</summary>
    public LyricLine? GetNextLineAt(TimeSpan position)
    {
        foreach (var line in Lines)
        {
            if (line.Time > position)
                return line;
        }
        return null;
    }

    /// <summary>将歌词序列化为标准 LRC 文本（用于持久化缓存回写，翻译按同时间戳行保存）。</summary>
    public string ToLrcText()
    {
        var sb = new System.Text.StringBuilder(Lines.Count * 32);
        foreach (var line in Lines)
        {
            var ts = line.Time.ToString(@"mm\:ss\.ff");
            sb.Append('[').Append(ts).Append(']').AppendLine(line.Text);
            if (!string.IsNullOrEmpty(line.Translation))
                sb.Append('[').Append(ts).Append(']').AppendLine(line.Translation);
        }
        return sb.ToString();
    }
}
