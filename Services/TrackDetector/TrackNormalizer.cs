using System.Text.RegularExpressions;

namespace TaskbarLyrics.Services;

/// <summary>
/// 曲目名规范化：去除 "(Live)"、"[Remaster]"、"(伴奏)" 等版本/后缀标记，
/// 使来自不同来源（窗口标题 / SMTC / 本地API）的同一首歌名可相互匹配。
/// </summary>
public class TrackNormalizer
{
    // 常见版本后缀（大小写不敏感）：现场版 / 重制版 / 伴奏 / 官方MV / 翻唱 / 混音 等
    private static readonly string[] SuffixPatterns =
    [
        @"\((?:live|live at[^)]*)\)",
        @"\[(?:live|live at[^\]]*)\]",
        @"\((?:remaster|remastered)[^)]*\)",
        @"\[(?:remaster|remastered)[^\]]*\]",
        @"\((?:伴奏|伴奏版|纯伴奏|钢琴版|吉他版)\)",
        @"\[(?:伴奏|伴奏版|纯伴奏|钢琴版|吉他版)\]",
        @"\((?:official (?:audio|video|music video|lyric video))\)",
        @"\[(?:official (?:audio|video|music video|lyric video))\]",
        @"\((?:官方mv|官方版|完整版|现场版|演唱会版|高清版)\)",
        @"\[(?:官方mv|官方版|完整版|现场版|演唱会版|高清版)\]",
        @"\((?:翻唱|cover|remix|karaoke|instrumental|acoustic|demo)\)",
        @"\[(?:翻唱|cover|remix|karaoke|instrumental|acoustic|demo)\]",
    ];

    private static readonly Regex[] SuffixRegexes =
        SuffixPatterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();

    /// <summary>去除版本后缀后归一化（去符号 + 小写），用于曲目匹配。</summary>
    public string NormalizeTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var t = title;
        foreach (var re in SuffixRegexes)
            t = re.Replace(t, " ");
        // 清理尾部残留的 " - " 分隔符与空格
        t = t.Trim().TrimEnd('-', '–', '—').TrimStart('-', '–', '—').Trim();
        return LyricsManager.Normalize(t);
    }

    /// <summary>归一化 "曲目名|歌手" 组合键（用于跨来源曲目匹配）。</summary>
    public string NormalizeTrack(string title, string artist) =>
        $"{NormalizeTitle(title)}|{LyricsManager.Normalize(artist)}";
}
