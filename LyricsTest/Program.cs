using System.Text;
using System.Diagnostics;
using TaskbarLyrics.Services;

// ============ 测试1: LRC 解析 ============
Console.WriteLine("=== 测试1: LRC 解析器 ===");
var lrc = @"[ti:晴天]
[ar:周杰伦]
[00:00.00] 前奏
[00:12.50] 故事的小黄花
[00:16.00][00:20.00] 从出生那年就飘着
[00:24.50] 童年的荡秋千
[offset:+50]";
var parsed = LrcParser.Parse(lrc);
Console.WriteLine($"解析到 {parsed.Lines.Count} 行歌词, 来源: {parsed.Source}");
foreach (var line in parsed.Lines)
    Console.WriteLine($"  [{line.Time:mm\\:ss\\.ff}] {line.Text}");
var lineAt = parsed.GetLineAt(TimeSpan.FromSeconds(13));
Console.WriteLine($"位置00:13的歌词: {lineAt?.Text}");
var next = parsed.GetNextLineAt(TimeSpan.FromSeconds(13));
Console.WriteLine($"下一句: {next?.Text}");
bool ok1 = parsed.Lines.Count == 5; // 00:00, 00:12.5, 00:16, 00:20, 00:24.5 (+offset 50ms)
Console.WriteLine(ok1 ? "✅ LRC解析通过" : "❌ LRC解析失败");

// ============ 测试2: MP3 ID3v2.3 USLT 内嵌歌词 ============
Console.WriteLine("\n=== 测试2: MP3 ID3v2 USLT 内嵌歌词 ===");
var mp3Path = Path.Combine(Path.GetTempPath(), "test_lyrics.mp3");
CreateTestMp3WithUslt(mp3Path);
var embedded = await AudioTagLyricsReader.ReadFromFileAsync(mp3Path);
Console.WriteLine($"读取到: {(embedded == null ? "null" : $"{embedded.Lines.Count}行, 来源: {embedded.Source}")}");
if (embedded != null)
    foreach (var line in embedded.Lines.Take(3))
        Console.WriteLine($"  [{line.Time:mm\\:ss\\.ff}] {line.Text}");
bool ok2 = embedded != null && embedded.Lines.Count > 0 && embedded.Lines[0].Text.Contains("内嵌歌词");
Console.WriteLine(ok2 ? "✅ MP3内嵌歌词读取通过" : "❌ MP3内嵌歌词读取失败");

// ============ 测试3: FLAC Vorbis 注释内嵌歌词 ============
Console.WriteLine("\n=== 测试3: FLAC Vorbis 注释内嵌歌词 ===");
var flacPath = Path.Combine(Path.GetTempPath(), "test_lyrics.flac");
CreateTestFlacWithVorbisLyrics(flacPath);
var flacLyrics = await AudioTagLyricsReader.ReadFromFileAsync(flacPath);
Console.WriteLine($"读取到: {(flacLyrics == null ? "null" : $"{flacLyrics.Lines.Count}行, 来源: {flacLyrics.Source}")}");
if (flacLyrics != null)
    foreach (var line in flacLyrics.Lines.Take(3))
        Console.WriteLine($"  [{line.Time:mm\\:ss\\.ff}] {line.Text}");
bool ok3 = flacLyrics != null && flacLyrics.Lines.Count > 0;
Console.WriteLine(ok3 ? "✅ FLAC内嵌歌词读取通过" : "❌ FLAC内嵌歌词读取失败");

File.Delete(mp3Path);
File.Delete(flacPath);

// ============ 测试4: 纯文本歌词 ParseLyrics 合成时间戳 ============
Console.WriteLine("\n=== 测试4: 纯文本歌词时间戳合成 (ParseLyrics) ===");
var plainData = LrcParser.ParseLyrics("第一句\n第二句\n\n第三句");
Console.WriteLine($"解析到 {plainData.Lines.Count} 行, 来源: {plainData.Source}");
foreach (var line in plainData.Lines)
    Console.WriteLine($"  [{line.Time:mm\\:ss\\.ff}] {line.Text}");
bool ok4 = plainData.Lines.Count == 3
    && plainData.Lines[0].Time == TimeSpan.Zero
    && plainData.Lines[1].Time == TimeSpan.FromSeconds(4)
    && plainData.Lines[2].Time == TimeSpan.FromSeconds(8)
    && plainData.IsSynced == false
    && plainData.RawText != null;
Console.WriteLine(ok4 ? "✅ 纯文本时间戳合成通过" : "❌ 纯文本时间戳合成失败");

// ============ 测试5: ToLrcText 序列化往返 ============
Console.WriteLine("\n=== 测试5: ToLrcText 序列化往返 ===");
var lrcText2 = parsed.ToLrcText();
Console.WriteLine(lrcText2.TrimEnd());
var reparsed = LrcParser.Parse(lrcText2);
bool ok5 = reparsed.Lines.Count == parsed.Lines.Count &&
    reparsed.Lines.SequenceEqual(parsed.Lines);
Console.WriteLine(ok5 ? "✅ ToLrcText 往返通过" : "❌ ToLrcText 往返失败");

// ============ 测试6: 同一时间戳原词+翻译 → 直接两行歌词 ============
Console.WriteLine("\n=== 测试6: 原词+翻译两行显示 ===");
var lrcWithTr = @"[00:10.00]故事的小黄花
[00:10.00]The little yellow flower
[00:14.00]从出生那年就飘着
[00:14.00]Has been drifting since birth";
var trData = LrcParser.Parse(lrcWithTr);
Console.WriteLine($"解析到 {trData.Lines.Count} 行");
foreach (var line in trData.Lines)
    Console.WriteLine($"  [{line.Time:mm\\:ss\\.ff}] {line.Text}");
// 同一时间戳两行保持独立；GetLineAt 取组内第一行(原词)
var curAt10 = trData.GetLineAt(TimeSpan.FromSeconds(10.5));
bool ok6 = trData.Lines.Count == 4
    && trData.Lines[0].Text == "故事的小黄花" && trData.Lines[1].Text == "The little yellow flower"
    && curAt10 != null && curAt10.Text == "故事的小黄花";
Console.WriteLine(ok6 ? "✅ 原词+翻译两行显示通过" : "❌ 原词+翻译两行显示失败");

// ============ 测试7: 独立翻译文本插入为两行 ============
Console.WriteLine("\n=== 测试7: 独立翻译文本插入 (ApplyTranslation) ===");
var tlyric = @"[00:10.00]The little yellow flower
[00:14.00]Has been drifting since birth";
var trData2 = LrcParser.Parse("[00:10.00]故事的小黄花\n[00:14.00]从出生那年就飘着");
LrcParser.ApplyTranslation(trData2, tlyric);
foreach (var line in trData2.Lines)
    Console.WriteLine($"  [{line.Time:mm\\:ss\\.ff}] {line.Text}");
bool ok7 = trData2.Lines.Count == 4
    && trData2.Lines[0].Text == "故事的小黄花" && trData2.Lines[1].Text == "The little yellow flower";
Console.WriteLine(ok7 ? "✅ 独立翻译插入通过" : "❌ 独立翻译插入失败");

// ============ 测试8: 带翻译的 ToLrcText 往返 ============
Console.WriteLine("\n=== 测试8: 带翻译的 ToLrcText 往返 ===");
var trReparsed = LrcParser.Parse(trData.ToLrcText());
Console.WriteLine($"往返后 {trReparsed.Lines.Count} 行");
bool ok8 = trReparsed.Lines.Count == 4
    && trReparsed.Lines[0].Text == "故事的小黄花" && trReparsed.Lines[1].Text == "The little yellow flower";
Console.WriteLine(ok8 ? "✅ 带翻译往返通过" : "❌ 带翻译往返失败");

// ============ 测试9: 两阶段渐进加载 - 缓存命中立即返回（不等待在线） ============
Console.WriteLine("\n=== 测试9: 两阶段渐进加载 - 缓存命中立即返回 ===");
var lm = new LyricsManager { MusicFolders = Array.Empty<string>(), PlayerCache = new PlayerLyricsCache { Enabled = false }, EnableOnline = true };
LyricCacheService.Store("测试歌曲两阶段", "测试歌手", "[00:00.00]第一句\n[00:05.00]第二句", "本地");
var sw9 = Stopwatch.StartNew();
var cachedLyrics = await lm.GetLyricsAsync(new MediaTrack("测试歌曲两阶段", "测试歌手", "", "test", "Playing", TimeSpan.Zero, TimeSpan.Zero, null));
sw9.Stop();
bool ok9 = cachedLyrics is { IsEmpty: false } && cachedLyrics.Lines.Count == 2 && sw9.ElapsedMilliseconds < 1000;
Console.WriteLine($"  耗时 {sw9.ElapsedMilliseconds}ms, 行数: {cachedLyrics?.Lines.Count ?? 0}");
Console.WriteLine(ok9 ? "✅ 缓存命中快速返回通过" : "❌ 缓存命中快速返回失败");

// ============ 测试10: 两阶段渐进加载 - 无本地/无在线时快速返回 null ============
Console.WriteLine("\n=== 测试10: 两阶段渐进加载 - 无来源快速失败 ===");
var lm2 = new LyricsManager { MusicFolders = Array.Empty<string>(), PlayerCache = new PlayerLyricsCache { Enabled = false }, EnableOnline = false };
var randomKey = $"NoSuchSong{Environment.TickCount64}";
var sw10 = Stopwatch.StartNew();
var noneLyrics = await lm2.GetLyricsAsync(new MediaTrack(randomKey, "NoArtist", "", "test", "Playing", TimeSpan.Zero, TimeSpan.Zero, null));
sw10.Stop();
bool ok10 = noneLyrics == null && sw10.ElapsedMilliseconds < 1500;
Console.WriteLine($"  耗时 {sw10.ElapsedMilliseconds}ms, 结果: {(noneLyrics == null ? "null" : "有歌词")}");
Console.WriteLine(ok10 ? "✅ 无来源快速失败通过" : "❌ 无来源快速失败");

// ============ 测试11: 作词作曲信息保留 + 时间戳稳定切换（不来回跳） ============
Console.WriteLine("\n=== 测试11: 作词作曲信息保留 + 不来回跳 ===");
var neteaseLrc = @"[00:00.00]作词 : 方文山
[00:00.00]作曲 : 周杰伦
[00:00.00]编曲 : 林迈可
[00:00.00]制作人 : 周杰伦
[00:00.00]
[00:12.00]故事的小黄花
[00:16.00]从出生那年就飘着";
var md = LrcParser.Parse(neteaseLrc);
var mdTexts = md.Lines.Select(l => l.Text).ToList();
bool ok11 = md.Lines.Count == 6
    && mdTexts.Contains("作词 : 方文山")      // 作词作曲信息保留
    && mdTexts.Contains("作曲 : 周杰伦")
    && mdTexts.Contains("编曲 : 林迈可")
    && mdTexts.Contains("制作人 : 周杰伦")
    && !mdTexts.Any(t => t.Length == 0)        // 空行仍跳过
    && md.GetLineAt(TimeSpan.Zero)?.Text == "作词 : 方文山"          // 开头显示作词信息
    && md.GetLineAt(TimeSpan.FromSeconds(12.1))?.Text == "故事的小黄花" // 12s 切到首句
    && md.GetLineAt(TimeSpan.FromSeconds(16.1))?.Text == "从出生那年就飘着"; // 16s 切到第二句
Console.WriteLine($"  解析行数: {md.Lines.Count}: [{string.Join("] [", mdTexts)}]");
Console.WriteLine(ok11 ? "✅ 作词作曲保留且稳定切换通过" : "❌ 作词作曲保留失败");

// ============ 测试12: 内存保留 - ABAB 来回切歌零重新解析 ============
Console.WriteLine("\n=== 测试12: 内存保留 - ABAB 来回切歌零重新解析 ===");
var lm3 = new LyricsManager { MusicFolders = Array.Empty<string>(), PlayerCache = new PlayerLyricsCache { Enabled = false }, EnableOnline = false };
var storeKey = $"内存保留{Environment.TickCount64}";
LyricCacheService.Store(storeKey, "测试歌手", "[00:00.00]第一句\n[00:05.00]第二句", "本地");
var trackA = new MediaTrack(storeKey, "测试歌手", "", "test", "Playing", TimeSpan.Zero, TimeSpan.Zero, null);
var first12 = await lm3.GetLyricsAsync(trackA);
lm3.Current = null; // 模拟切歌：Overlay 清空 Current
var sw12 = Stopwatch.StartNew();
var second12 = await lm3.GetLyricsAsync(trackA);
sw12.Stop();
bool ok12 = first12 is { IsEmpty: false } && second12 is { IsEmpty: false }
    && ReferenceEquals(first12, second12) && sw12.ElapsedMilliseconds < 5;
Console.WriteLine($"  耗时 {sw12.ElapsedMilliseconds}ms, 同一实例: {ReferenceEquals(first12, second12)}");
Console.WriteLine(ok12 ? "✅ 内存保留通过" : "❌ 内存保留失败");

// ============ 测试13: 歌词滚动序列验证（过滤后仍能随播放进度滚动） ============
Console.WriteLine("\n=== 测试13: 歌词滚动序列验证 ===");
var rollLrc = @"[00:00.00]作词 : 周杰伦
[00:00.00]作曲 : 周杰伦
[00:00.00]编曲 : 周杰伦
[00:00.00]
[00:00.00]故事的小黄花
[00:12.50]从出生那年就飘着
[00:24.50]童年的荡秋千
[00:36.50]随记忆一直晃到现在";
var roll = LrcParser.Parse(rollLrc);
var rollIdx = new List<int>();
for (int s = 0; s <= 40; s += 2)
{
    var cur = roll.GetLineAt(TimeSpan.FromSeconds(s));
    if (cur != null) rollIdx.Add(roll.Lines.IndexOf(cur));
}
// 作词作曲行保留后:开头(0~12.5s)稳定显示"作词"行(index 0,不跳),
// 12.5s/24.5s/36.5s 依次切到后续歌词行(4/5/6)
var expected = new List<int> { 0,0,0,0,0,0,0, 4,4,4,4,4,4, 5,5,5,5,5,5, 6,6 };
bool ok13 = roll.Lines.Count == 7
    && rollIdx.SequenceEqual(expected);
Console.WriteLine($"  解析行数: {roll.Lines.Count}, index 序列: [{string.Join(",", rollIdx)}]");
Console.WriteLine(ok13 ? "✅ 滚动序列通过" : "❌ 滚动序列失败");

// ============ 测试14: 拖进度条回滚 AlignTo（歌词跟随进度条） ============
Console.WriteLine("\n=== 测试14: 拖进度条回滚 AlignTo ===");
var det = new TrackDetector();
// 通过反射注入当前曲目（生产环境由窗口/SMTC 检测设置），使 GetPosition 可用
var field = typeof(TrackDetector).GetField("_currentTrack",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
field?.SetValue(det, new MediaTrack("测试歌曲", "测试歌手", "", "test", "Playing", TimeSpan.Zero, TimeSpan.Zero, null));
det.AlignTo(TimeSpan.FromSeconds(30));   // 模拟进度条拖到 30s
var seekPos1 = det.GetPosition().TotalSeconds;
Thread.Sleep(300);                        // 播放推进
var seekPos2 = det.GetPosition().TotalSeconds;
det.AlignTo(TimeSpan.FromSeconds(10));   // 模拟往回拖到 10s → 歌词应回滚
var seekPos3 = det.GetPosition().TotalSeconds;
Thread.Sleep(200);
var seekPos4 = det.GetPosition().TotalSeconds; // 回滚后继续前进
bool ok14 = seekPos1 > 29 && seekPos1 < 31
    && seekPos2 > seekPos1                 // 正常前进
    && seekPos3 > 9 && seekPos3 < 11       // 回滚到 10s 附近
    && seekPos4 > seekPos3;                // 回滚后继续推进
Console.WriteLine($"  30s→{seekPos1:F1}s → {seekPos2:F1}s → 回拖10s→{seekPos3:F1}s → {seekPos4:F1}s");
Console.WriteLine(ok14 ? "✅ 进度条回滚跟随通过" : "❌ 进度条回滚跟随失败");

// ============ 测试15: 纯文本歌词按歌曲总时长估算行间隔 ============
Console.WriteLine("\n=== 测试15: 纯文本歌词按时长估算行间隔 ===");
var estLong = LrcParser.EstimatePlainInterval(200, "第一句\n第二句\n第三句\n第四句\n第五句"); // 5行,200s → 40s → 钳制 8s
var estShort = LrcParser.EstimatePlainInterval(30, "a\nb\nc\nd\ne\nf");                          // 6行,30s → 5s
var estNone = LrcParser.EstimatePlainInterval(0, "a\nb");                                        // 无时长 → 4s
var estData = LrcParser.ParseLyrics("a\nb\nc\nd", 5);                                            // 4行,5s间隔 → 末行 15s
bool ok15 = estLong == 8 && estShort == 5 && estNone == 4
    && estData.Lines[3].Time == TimeSpan.FromSeconds(15)
    && estData.IsSynced == false;
Console.WriteLine($"  200s/5行→{estLong}s, 30s/6行→{estShort}s, 无时长→{estNone}s, 4行×5s末行={estData.Lines[3].Time.TotalSeconds}s");
Console.WriteLine(ok15 ? "✅ 时长估算通过" : "❌ 时长估算失败");

// ============ 测试16: 音频文件时长读取 (MP3码率估算 / FLAC STREAMINFO) ============
Console.WriteLine("\n=== 测试16: 音频文件时长读取 ===");
var durMp3 = Path.Combine(Path.GetTempPath(), $"test_dur_{Environment.TickCount64}.mp3");
CreateTestMp3WithDuration(durMp3, 128, 16 * 1024); // 128kbps + 16KB 音频数据 → ≈1.0s
var mp3Dur = await AudioTagLyricsReader.ReadDurationAsync(durMp3);
Console.WriteLine($"  MP3 128kbps/16KB → {mp3Dur:F3}s (期望≈1.0s)");
var durFlac = Path.Combine(Path.GetTempPath(), $"test_dur_{Environment.TickCount64}.flac");
CreateTestFlacWithDuration(durFlac, 8192, 8192);   // 8192Hz / 8192 样本 → 1.0s
var flacDur = await AudioTagLyricsReader.ReadDurationAsync(durFlac);
Console.WriteLine($"  FLAC 8192Hz/8192样本 → {flacDur:F3}s (期望1.0s)");
bool ok16 = Math.Abs(mp3Dur - 1.0) < 0.3 && Math.Abs(flacDur - 1.0) < 0.05;
Console.WriteLine(ok16 ? "✅ 音频时长读取通过" : "❌ 音频时长读取失败");
File.Delete(durMp3); File.Delete(durFlac);

// ============ 测试17: SMTC 时长=0 时纯文本歌词用音频文件时长估算 ============
Console.WriteLine("\n=== 测试17: 无时长纯文本歌词从音频文件取时长估算 ===");
var lrDir = Path.Combine(Path.GetTempPath(), $"lyrics_test_{Environment.TickCount64}");
Directory.CreateDirectory(lrDir);
bool ok17 = false;
try
{
    CreateTestFlacWithDuration(Path.Combine(lrDir, "测试歌曲A.flac"), 8000, 160000); // 20s
    File.WriteAllText(Path.Combine(lrDir, "测试歌曲A.lrc"),
        string.Join("\n", Enumerable.Range(1, 10).Select(i => $"第{i}句")));          // 10行
    var lm17 = new LyricsManager { MusicFolders = new[] { lrDir }, PlayerCache = new PlayerLyricsCache { Enabled = false }, EnableOnline = false };
    lm17.ResetIndex();
    // ResetIndex 为后台异步重建索引；生产环境由启动 WarmUp 预构建，这里轮询等待就绪
    var idxField = typeof(LyricsManager).GetField("_indexReady",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    for (int i = 0; i < 100 && idxField?.GetValue(lm17) is not true; i++)
        await Task.Delay(50);
    var d17 = await lm17.GetLyricsAsync(new MediaTrack("测试歌曲A", "", "", "test", "Playing", TimeSpan.Zero, TimeSpan.Zero, null));
    // 20s/10行 → 2s/行（而非 SMTC 时长=0 时的默认 4s）
    ok17 = d17 is { IsEmpty: false } && d17.Lines.Count == 10
        && Math.Abs(d17.Lines[1].Time.TotalSeconds - 2) < 0.01
        && d17.IsSynced == false;
    Console.WriteLine($"  行数: {d17?.Lines.Count}, 第2行时刻: {d17?.Lines[1].Time.TotalSeconds}s, 同步: {d17?.IsSynced}");
    Console.WriteLine(ok17 ? "✅ 音频时长兜底估算通过" : "❌ 音频时长兜底估算失败");
}
finally { try { Directory.Delete(lrDir, true); } catch { } }

// ============ 测试18: 嵌套子目录递归扫描（本地歌词匹配不到是常见根因） ============
Console.WriteLine("\n=== 测试18: 嵌套子目录递归扫描本地歌词 ===");
var nestDir = Path.Combine(Path.GetTempPath(), $"lyrics_nest_{Environment.TickCount64}");
var subDir = Path.Combine(nestDir, "歌手名", "专辑名");
Directory.CreateDirectory(subDir);
bool ok18 = false;
try
{
    CreateTestFlacWithDuration(Path.Combine(subDir, "嵌套歌曲.flac"), 8000, 80000); // 10s
    File.WriteAllText(Path.Combine(subDir, "嵌套歌曲.lrc"),
        "[00:00.00]嵌套歌词第一句\n[00:05.00]嵌套歌词第二句");
    var lm18 = new LyricsManager { MusicFolders = new[] { nestDir }, PlayerCache = new PlayerLyricsCache { Enabled = false }, EnableOnline = false };
    lm18.ResetIndex();
    var idxField18 = typeof(LyricsManager).GetField("_indexReady",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    for (int i = 0; i < 100 && idxField18?.GetValue(lm18) is not true; i++)
        await Task.Delay(50);
    var d18 = await lm18.GetLyricsAsync(new MediaTrack("嵌套歌曲", "", "", "test", "Playing", TimeSpan.Zero, TimeSpan.Zero, null));
    // 旧实现仅扫顶层目录，子目录中的 .lrc 永远匹配不到
    ok18 = d18 is { IsEmpty: false } && d18.Lines.Count == 2 && d18.Source == "本地LRC文件";
    Console.WriteLine($"  行数: {d18?.Lines.Count}, 来源: {d18?.Source}");
    Console.WriteLine(ok18 ? "✅ 嵌套目录递归扫描通过" : "❌ 嵌套目录递归扫描失败");
}
finally { try { Directory.Delete(nestDir, true); } catch { } }

Console.WriteLine($"\n最终结果: {(ok1 && ok2 && ok3 && ok4 && ok5 && ok6 && ok7 && ok8 && ok9 && ok10 && ok11 && ok12 && ok13 && ok14 && ok15 && ok16 && ok17 && ok18 ? "全部通过 🎉" : "存在失败项")}");

// ==================== 测试文件构造 ====================

static void CreateTestMp3WithUslt(string path)
{
    // 构造一个 ID3v2.3 标签: "ID3" + 版本 + flags + 4字节syncsafe size
    var lyricText = "内嵌歌词测试第一行\n内嵌歌词测试第二行";
    var encoding = (byte)3; // UTF-8
    var language = new byte[] { (byte)'z', (byte)'h', (byte)'s' };
    var body = new List<byte>();
    body.Add(encoding);
    body.AddRange(language);
    body.Add(0); // 描述结尾
    body.AddRange(Encoding.UTF8.GetBytes(lyricText));

    var frame = new List<byte>();
    frame.AddRange(Encoding.ASCII.GetBytes("USLT"));
    frame.AddRange(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(body.Count))); // 4字节大端
    frame.Add(0); frame.Add(0); // flags
    frame.AddRange(body);

    var tagSize = frame.Count;
    var sizeBytes = new byte[4];
    sizeBytes[0] = (byte)((tagSize >> 21) & 0x7F);
    sizeBytes[1] = (byte)((tagSize >> 14) & 0x7F);
    sizeBytes[2] = (byte)((tagSize >> 7) & 0x7F);
    sizeBytes[3] = (byte)(tagSize & 0x7F);

    using var fs = new FileStream(path, FileMode.Create);
    fs.Write(Encoding.ASCII.GetBytes("ID3"));
    fs.WriteByte(3); // 主版本 v2.3
    fs.WriteByte(0); // 次版本
    fs.WriteByte(0); // flags
    fs.Write(sizeBytes, 0, 4);
    fs.Write(frame.ToArray(), 0, frame.Count);
    // 假音频数据
    var dummy = new byte[1024];
    new Random(42).NextBytes(dummy);
    fs.Write(dummy, 0, dummy.Length);
}

static void CreateTestFlacWithVorbisLyrics(string path)
{
    using var fs = new FileStream(path, FileMode.Create);
    fs.Write(Encoding.ASCII.GetBytes("fLaC"));
    // metadata block 0: STREAMINFO (type 0), length 34
    var streamInfo = new byte[34];
    streamInfo[10] = 0x02; // 一些最小通道数/采样率
    streamInfo[18] = 0x44; streamInfo[19] = 0xAC; streamInfo[20] = 0x00;
    fs.WriteByte(0x00); // last=0, type=0
    fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(34);
    fs.Write(streamInfo, 0, 34);

    // metadata block 4: VORBIS_COMMENT (type 4), last=1
    var comments = new List<byte>();
    var vendor = Encoding.UTF8.GetBytes("test");
    comments.AddRange(BitConverter.GetBytes(vendor.Length)); // LE
    comments.AddRange(vendor);
    var fields = new List<string> { "TITLE=测试歌曲", "LYRICS=测试歌词第一行\n测试歌词第二行" };
    comments.AddRange(BitConverter.GetBytes(fields.Count)); // LE
    foreach (var f in fields)
    {
        var fb = Encoding.UTF8.GetBytes(f);
        comments.AddRange(BitConverter.GetBytes(fb.Length)); // LE
        comments.AddRange(fb);
    }
    int len = comments.Count;
    fs.WriteByte(0x84); // last=1 (0x80), type=4
    fs.WriteByte((byte)((len >> 16) & 0xFF));
    fs.WriteByte((byte)((len >> 8) & 0xFF));
    fs.WriteByte((byte)(len & 0xFF));
    fs.Write(comments.ToArray(), 0, comments.Count);
    // 假音频帧数据
    var dummy = new byte[512];
    new Random(7).NextBytes(dummy);
    fs.Write(dummy, 0, dummy.Length);
}

static void CreateTestMp3WithDuration(string path, int bitrateKbps, int audioBytes)
{
    // ID3v2.3 空标签 (tag size=0) + MPEG1 Layer3 帧头 + 音频数据
    using var fs = new FileStream(path, FileMode.Create);
    fs.Write(Encoding.ASCII.GetBytes("ID3"));
    fs.WriteByte(3); fs.WriteByte(0); fs.WriteByte(0);
    fs.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);
    // 帧头: 0xFF 0xFB 0x90 = sync + MPEG1 + Layer3 + bitrate_idx=9(128kbps) + sampleRate_idx=0(44100Hz)
    var frameHeader = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
    var data = new byte[audioBytes];
    new Random(11).NextBytes(data);
    Array.Copy(frameHeader, 0, data, 0, 4);
    fs.Write(data, 0, data.Length);
}

static void CreateTestFlacWithDuration(string path, int sampleRate, long totalSamples)
{
    // 只写 STREAMINFO 块: 采样率(20bit) + 总采样数(36bit)
    using var fs = new FileStream(path, FileMode.Create);
    fs.Write(Encoding.ASCII.GetBytes("fLaC"));
    var body = new byte[34];
    body[10] = (byte)(sampleRate >> 12);
    body[11] = (byte)(sampleRate >> 4);
    body[12] = (byte)((sampleRate & 0x0F) << 4);
    body[13] = (byte)((totalSamples >> 32) & 0x0F);
    body[14] = (byte)((totalSamples >> 24) & 0xFF);
    body[15] = (byte)((totalSamples >> 16) & 0xFF);
    body[16] = (byte)((totalSamples >> 8) & 0xFF);
    body[17] = (byte)(totalSamples & 0xFF);
    fs.WriteByte(0x80); // last=1, type=0 (STREAMINFO)
    fs.WriteByte(0); fs.WriteByte(0); fs.WriteByte(34);
    fs.Write(body, 0, 34);
    var dummy = new byte[64];
    new Random(13).NextBytes(dummy);
    fs.Write(dummy, 0, dummy.Length);
}
