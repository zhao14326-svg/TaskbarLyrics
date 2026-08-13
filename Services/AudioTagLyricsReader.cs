using System.IO;
using System.Text;
using TaskbarLyrics.Models;

namespace TaskbarLyrics.Services;

/// <summary>
/// 从音频文件中读取内嵌歌词、封面和基本元数据（不依赖外部库）。
/// 支持：MP3 的 ID3v2.2/2.3/2.4 USLT 帧 + APIC/PIC 封面、FLAC 的 Vorbis 注释 LYRICS 标签 + METADATA_BLOCK_PICTURE。
/// </summary>
/// <summary>音频标签读取(内嵌歌词/封面/时长)。</summary>
public interface IAudioTagLyricsReader
{
    (string Artist, string Title) ReadMetaCached(string filePath);
    Task<double> ReadDurationAsync(string filePath);
    Task<LyricsData?> ReadFromFileAsync(string filePath);
    Task<byte[]?> ReadMp3CoverAsync(string path);
    Task<byte[]?> ReadFlacCoverAsync(string path);
}

public class AudioTagLyricsReader : IAudioTagLyricsReader
{
    /// <summary>Read basic Artist + Title from audio file metadata (with cache).</summary>
    public (string Artist, string Title) ReadMetaCached(string filePath)
    {
        var cache = Helpers.AudioMetaCache.TryGet(filePath);
        if (cache != null) return (cache.Artist, cache.Title);

        var (artist, title) = ReadMeta(filePath);
        Helpers.AudioMetaCache.Store(filePath, artist, title);
        return (artist, title);
    }

    private (string Artist, string Title) ReadMeta(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".mp3" => ReadMp3Meta(filePath),
                ".flac" => ReadFlacMeta(filePath),
                _ => ("", "")
            };
        }
        catch { return ("", ""); }
    }

    /// <summary>从音频文件读取时长(秒)：MP3 按帧头码率估算，FLAC 用 STREAMINFO 总采样数。失败/不支持返回 0。</summary>
    public async Task<double> ReadDurationAsync(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".mp3" => await ReadMp3DurationAsync(filePath),
                ".flac" => await ReadFlacDurationAsync(filePath),
                _ => 0
            };
        }
        catch { return 0; }
    }

    private (string, string) ReadMp3Meta(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[10];
        if (fs.Length < 10 || !ReadExactAsync(fs, header, 10).GetAwaiter().GetResult()) return ("", "");
        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return ("", "");

        byte major = header[3];
        byte flags = header[5];
        int tagSize = ReadSyncSafeInt32(header, 6);
        if (tagSize <= 0 || tagSize > 1024 * 1024) return ("", "");
        var data = new byte[tagSize];
        int rd = 0;
        while (rd < tagSize) { int n = fs.Read(data, rd, tagSize - rd); if (n <= 0) break; rd += n; }

        int pos = 0;
        if ((flags & 0x40) != 0) pos = 4 + (major >= 4 ? ReadSyncSafeInt32(data, 0) : ReadInt32BE(data, 0));

        string artist = "", title = "";
        // Scan frames for TPE1/TIT2 (v2.3/2.4) or TP1/TT2 (v2.2)
        while (pos + 6 <= data.Length)
        {
            string id;
            int size;
            if (major == 2)
            {
                if (pos + 3 > data.Length) break;
                id = Encoding.ASCII.GetString(data, pos, 3);
                if (id[0] == 0) break;
                size = (data[pos + 3] << 16) | (data[pos + 4] << 8) | data[pos + 5];
                pos += 6;
            }
            else
            {
                if (pos + 4 > data.Length) break;
                id = Encoding.ASCII.GetString(data, pos, 4);
                if (id[0] == 0) break;
                size = major == 4 ? ReadSyncSafeInt32(data, pos + 4) : ReadInt32BE(data, pos + 4);
                pos += 10;
            }
            if (pos + size > data.Length) break;

            if (id == "TPE1" || id == "TP1" || id == "TP2") artist = DecodeTextFrame(data, pos, size);
            else if (id == "TIT2" || id == "TT2") title = DecodeTextFrame(data, pos, size);

            if (artist.Length > 0 && title.Length > 0) break;
            pos += size;
        }
        return (artist, title);
    }

    /// <summary>MP3 时长估算：跳过 ID3 标签后扫描第一个 MPEG 帧头，按码率估算（CBR/VBR 近似，误差可接受）。</summary>
    private async Task<double> ReadMp3DurationAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long dataStart = 0;
        if (fs.Length >= 10)
        {
            var head = new byte[10];
            if (ReadExactAsync(fs, head, 10).GetAwaiter().GetResult() && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            {
                dataStart = 10 + ((head[6] & 0x7F) << 21) | ((head[7] & 0x7F) << 14) | ((head[8] & 0x7F) << 7) | (head[9] & 0x7F);
                fs.Position = dataStart;
            }
            else { fs.Position = 0; }
        }

        var buf = new byte[8192];
        while (fs.Position < fs.Length)
        {
            int n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            for (int i = 0; i + 3 < n; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;
                int version = (buf[i + 1] >> 3) & 0x03;   // 3=MPEG1, 2=MPEG2, 0=MPEG2.5
                int layer = (buf[i + 1] >> 1) & 0x03;     // 1=Layer3, 2=Layer2, 3=Layer1
                int brIdx = (buf[i + 2] >> 4) & 0x0F;
                if (version == 1 || layer == 0 || brIdx == 0 || brIdx == 15) continue;
                int bitrateKbps = GetMp3BitrateKbps(version, layer, brIdx);
                if (bitrateKbps <= 0) continue;
                double audioBytes = fs.Length - dataStart;
                if (audioBytes <= 0) return 0;
                return audioBytes * 8.0 / (bitrateKbps * 1000.0);
            }
            if (fs.Position >= 2) fs.Position -= 2; // 跨块边界重叠扫描
            else break;
        }
        return 0;
    }

    private readonly int[] Mpeg1Layer1 = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0 };
    private readonly int[] Mpeg1Layer2 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 };
    private readonly int[] Mpeg1Layer3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
    private readonly int[] Mpeg2Layer1 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0 };
    private readonly int[] Mpeg2Layer23 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };

    private int GetMp3BitrateKbps(int version, int layer, int idx)
    {
        if (version == 3) return layer switch
        {
            1 => Mpeg1Layer3[idx],
            2 => Mpeg1Layer2[idx],
            _ => Mpeg1Layer1[idx],
        };
        return layer == 1 ? Mpeg2Layer1[idx] : Mpeg2Layer23[idx];
    }

    private string DecodeTextFrame(byte[] data, int start, int size)
    {
        if (size < 1) return "";
        int encoding = data[start];
        int textStart = start + 1;
        int textLen = start + size - textStart;
        if (textLen <= 0) return "";
        var slice = new byte[textLen];
        Array.Copy(data, textStart, slice, 0, textLen);
        return encoding switch
        {
            0 => Encoding.Latin1.GetString(slice).TrimEnd('\0'),
            1 => DecodeUtf16(slice, true).TrimEnd('\0'),
            2 => DecodeUtf16(slice, false).TrimEnd('\0'),
            3 => Encoding.UTF8.GetString(slice).TrimEnd('\0'),
            _ => Encoding.UTF8.GetString(slice).TrimEnd('\0')
        };
    }

    private (string, string) ReadFlacMeta(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var magic = new byte[4];
        if (fs.Length < 4 || !ReadExactAsync(fs, magic, 4).GetAwaiter().GetResult()) return ("", "");
        if (magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C') return ("", "");

        string artist = "", title = "";
        while (fs.Position < fs.Length)
        {
            var bh = new byte[4];
            if (!ReadExactAsync(fs, bh, 4).GetAwaiter().GetResult()) break;
            bool last = (bh[0] & 0x80) != 0;
            int type = bh[0] & 0x7F;
            int length = (bh[1] << 16) | (bh[2] << 8) | bh[3];
            if (type == 4)
            {
                var body = new byte[length];
                int rd = 0;
                while (rd < length) { int r = fs.Read(body, rd, length - rd); if (r <= 0) break; rd += r; }
                // Parse Vorbis comments for ARTIST and TITLE
                int pos = 0;
                int vendorLen = ReadInt32LE(body, pos); pos += 4 + vendorLen;
                if (pos + 4 > body.Length) break;
                int count = ReadInt32LE(body, pos); pos += 4;
                for (int i = 0; i < count && pos + 4 <= body.Length; i++)
                {
                    int len = ReadInt32LE(body, pos); pos += 4;
                    if (pos + len > body.Length) break;
                    var comment = Encoding.UTF8.GetString(body, pos, len); pos += len;
                    var eq = comment.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = comment[..eq].Trim().ToUpperInvariant();
                    var val = comment[(eq + 1)..].Trim();
                    if (key == "ARTIST" && artist.Length == 0) artist = val;
                    else if (key == "TITLE" && title.Length == 0) title = val;
                }
            }
            fs.Seek(length, SeekOrigin.Current);
            if (last) break;
        }
        return (artist, title);
    }

    /// <summary>FLAC 时长：读 STREAMINFO 块的总采样数与采样率。</summary>
    private async Task<double> ReadFlacDurationAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < 8) return 0;
        var magic = new byte[4];
        if (!ReadExactAsync(fs, magic, 4).GetAwaiter().GetResult() || magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C') return 0;

        while (true)
        {
            var bh = new byte[4];
            if (!ReadExactAsync(fs, bh, 4).GetAwaiter().GetResult()) return 0;
            bool last = (bh[0] & 0x80) != 0;
            int type = bh[0] & 0x7F;
            int length = (bh[1] << 16) | (bh[2] << 8) | bh[3];
            if (type == 0) // STREAMINFO
            {
                var body = new byte[length];
                int rd = 0;
                while (rd < length) { int r = fs.Read(body, rd, length - rd); if (r <= 0) break; rd += r; }
                if (length < 18) return 0;
                // 布局: [0..9]=块/帧尺寸, [10..12]=采样率(20bit), [13..17]=总采样数(36bit, 高4位在body[13]低4位)
                int sampleRate = (body[10] << 12) | (body[11] << 4) | (body[12] >> 4);
                long totalSamples = ((long)(body[13] & 0x0F) << 32) | ((long)body[14] << 24) | ((long)body[15] << 16) | ((long)body[16] << 8) | body[17];
                if (sampleRate <= 0 || totalSamples <= 0) return 0;
                return totalSamples / (double)sampleRate;
            }
            if (length > 0) fs.Seek(length, SeekOrigin.Current);
            if (last) return 0;
        }
    }

    // Reuse existing static helpers (ReadInt32BE, ReadInt32LE, ReadSyncSafeInt32, ReadExact, DecodeUtf16 are already in this file)
    /// <summary>从音频文件读取内嵌歌词；无内嵌歌词时返回 null</summary>
    public async Task<LyricsData?> ReadFromFileAsync(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            string? lyrics = null;
            string source = "";

            switch (ext)
            {
                case ".mp3":
                    lyrics = await ReadMp3Id3v2Async(filePath);
                    source = "MP3内嵌歌词(ID3v2 USLT)";
                    break;
                case ".flac":
                    lyrics = await ReadFlacVorbisAsync(filePath);
                    source = "FLAC内嵌歌词(Vorbis LYRICS)";
                    break;
                case ".ogg":
                    lyrics = await ReadOggVorbisAsync(filePath);
                    source = "OGG内嵌歌词(Vorbis LYRICS)";
                    break;
            }

            if (string.IsNullOrWhiteSpace(lyrics))
                return null;

            // 优先尝试作为 LRC 解析（带时间戳）
            if (LrcParser.LooksLikeLrc(lyrics))
            {
                var data = LrcParser.Parse(lyrics);
                data.Source = source;
                return data;
            }

            // 纯文本歌词：不分时间戳，一行一句，时间从 0 递增 4 秒
            var plain = new LyricsData { Source = source };
            int i = 0;
            foreach (var l in lyrics.Split('\n'))
            {
                var t = l.TrimEnd('\r').Trim();
                if (t.Length > 0)
                    plain.Lines.Add(new LyricLine(TimeSpan.FromSeconds(i++ * 4), t));
            }
            return plain;
        }
        catch
        {
            return null;
        }
    }

    // ==================== Cover Art Extraction ====================

    /// <summary>Extract embedded cover art from MP3 ID3v2 APIC frame.</summary>
    public async Task<byte[]?> ReadMp3CoverAsync(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[10];
            if (fs.Length < 10) return null;
            if (!await ReadExactAsync(fs, header, 10)) return null;
            if (header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3') return null;

            byte major = header[3];
            byte flags = header[5];
            int tagSize = ReadSyncSafeInt32(header, 6);
            if (tagSize <= 0 || tagSize > 20 * 1024 * 1024) return null;

            var tagData = new byte[tagSize];
            int read = 0;
            while (read < tagSize)
            {
                int n = await fs.ReadAsync(tagData, read, tagSize - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < tagSize) Array.Resize(ref tagData, read);

            int pos = 0;
            bool hasExtendedHeader = (flags & 0x40) != 0;
            if (hasExtendedHeader)
            {
                if (tagData.Length < 4) return null;
                int extSize = major >= 4 ? ReadSyncSafeInt32(tagData, 0) : ReadInt32BE(tagData, 0);
                pos = 4 + extSize;
            }

            return major switch
            {
                2 => ScanCoverV22(tagData, ref pos),
                3 => ScanCoverV23(tagData, ref pos),
                4 => ScanCoverV24(tagData, ref pos),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extract embedded cover art from FLAC METADATA_BLOCK_PICTURE.</summary>
    public async Task<byte[]?> ReadFlacCoverAsync(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var magic = new byte[4];
            if (fs.Length < 4) return null;
            if (!await ReadExactAsync(fs, magic, 4)) return null;
            if (magic[0] != (byte)'f' || magic[1] != (byte)'L' || magic[2] != (byte)'a' || magic[3] != (byte)'C') return null;

            while (fs.Position < fs.Length)
            {
                var blockHeader = new byte[4];
                if (!await ReadExactAsync(fs, blockHeader, 4)) break;
                bool last = (blockHeader[0] & 0x80) != 0;
                int type = blockHeader[0] & 0x7F;
                int length = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];

                if (type == 4) // VORBIS_COMMENT
                {
                    var body = new byte[length];
                    int rd = 0;
                    while (rd < length)
                    {
                        int r = await fs.ReadAsync(body, rd, length - rd);
                        if (r <= 0) break;
                        rd += r;
                    }
                    return ParseVorbisCover(body);
                }

                fs.Seek(length, SeekOrigin.Current);
                if (last) break;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ==================== MP3 ID3v2 (lyrics) ====================

    private async Task<string?> ReadMp3Id3v2Async(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[10];
        if (fs.Length < 10) return null;
        if (!await ReadExactAsync(fs, header, 10)) return null;
        if (header[0] != (byte)'I' || header[1] != (byte)'D' || header[2] != (byte)'3') return null;

        byte major = header[3];
        byte flags = header[5];
        int tagSize = ReadSyncSafeInt32(header, 6);
        if (tagSize <= 0 || tagSize > 20 * 1024 * 1024) return null;

        var tagData = new byte[tagSize];
        int read = 0;
        while (read < tagSize)
        {
            int n = await fs.ReadAsync(tagData, read, tagSize - read);
            if (n <= 0) break;
            read += n;
        }
        if (read < tagSize) Array.Resize(ref tagData, read);

        int pos = 0;
        bool hasExtendedHeader = (flags & 0x40) != 0;
        if (hasExtendedHeader)
        {
            if (tagData.Length < 4) return null;
            int extSize = major >= 4 ? ReadSyncSafeInt32(tagData, 0) : ReadInt32BE(tagData, 0);
            pos = 4 + extSize;
        }

        return major switch
        {
            2 => ScanLyricsV22(tagData, ref pos),
            3 => ScanLyricsV23(tagData, ref pos),
            4 => ScanLyricsV24(tagData, ref pos),
            _ => null
        };
    }

    private string? ScanLyricsV22(byte[] data, ref int pos)
    {
        while (pos + 6 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, pos, 3);
            if (id == "\0\0\0" || id == "\0\0\0\0") break;
            int size = (data[pos + 3] << 16) | (data[pos + 4] << 8) | data[pos + 5];
            int bodyStart = pos + 6;
            if (bodyStart + size > data.Length) break;
            if (id == "ULT")
            {
                var text = DecodeLyricsBody(data, bodyStart, size);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            pos = bodyStart + size;
        }
        return null;
    }

    private string? ScanLyricsV23(byte[] data, ref int pos)
    {
        while (pos + 10 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, pos, 4);
            if (id.StartsWith('\0') || id == "\0\0\0\0") break;
            int size = ReadInt32BE(data, pos + 4);
            int bodyStart = pos + 10;
            if (bodyStart + size > data.Length) break;
            if (id == "USLT")
            {
                var text = DecodeLyricsBody(data, bodyStart, size);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            pos = bodyStart + size;
        }
        return null;
    }

    private string? ScanLyricsV24(byte[] data, ref int pos)
    {
        while (pos + 10 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, pos, 4);
            if (id.StartsWith('\0') || id == "\0\0\0\0") break;
            int size = ReadSyncSafeInt32(data, pos + 4);
            int bodyStart = pos + 10;
            if (bodyStart + size > data.Length) break;
            if (id == "USLT")
            {
                var text = DecodeLyricsBody(data, bodyStart, size);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            pos = bodyStart + size;
        }
        return null;
    }

    // ==================== MP3 ID3v2 Cover Scanners ====================

    private byte[]? ScanCoverV22(byte[] data, ref int pos)
    {
        while (pos + 6 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, pos, 3);
            if (id == "\0\0\0" || id == "\0\0\0\0") break;
            int size = (data[pos + 3] << 16) | (data[pos + 4] << 8) | data[pos + 5];
            int bodyStart = pos + 6;
            if (bodyStart + size > data.Length) break;
            if (id == "PIC") // v2.2 cover
            {
                var cover = DecodeCoverBody(data, bodyStart, size);
                if (cover is { Length: > 0 }) return cover;
            }
            pos = bodyStart + size;
        }
        return null;
    }

    private byte[]? ScanCoverV23(byte[] data, ref int pos)
    {
        while (pos + 10 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, pos, 4);
            if (id.StartsWith('\0') || id == "\0\0\0\0") break;
            int size = ReadInt32BE(data, pos + 4);
            int bodyStart = pos + 10;
            if (bodyStart + size > data.Length) break;
            if (id == "APIC")
            {
                var cover = DecodeCoverBody(data, bodyStart, size);
                if (cover is { Length: > 0 }) return cover;
            }
            pos = bodyStart + size;
        }
        return null;
    }

    private byte[]? ScanCoverV24(byte[] data, ref int pos)
    {
        while (pos + 10 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, pos, 4);
            if (id.StartsWith('\0') || id == "\0\0\0\0") break;
            int size = ReadSyncSafeInt32(data, pos + 4);
            int bodyStart = pos + 10;
            if (bodyStart + size > data.Length) break;
            if (id == "APIC")
            {
                var cover = DecodeCoverBody(data, bodyStart, size);
                if (cover is { Length: > 0 }) return cover;
            }
            pos = bodyStart + size;
        }
        return null;
    }

    /// <summary>
    /// Decode APIC/PIC frame body.
    /// Layout: encoding(1) + mime_type(0-terminated) + picture_type(1) + description(0-terminated) + image_data
    /// For v2.2 PIC: encoding(1) + format("JPG"/"PNG" 3 bytes) + picture_type(1) + description(0-terminated) + image_data
    /// </summary>
    private byte[]? DecodeCoverBody(byte[] data, int start, int size)
    {
        if (size < 10) return null;
        int pos = start;

        // encoding byte
        int encoding = data[pos]; pos++;

        // MIME type or format string (null-terminated)
        int mimeStart = pos;
        while (pos < start + size && data[pos] != 0) pos++;
        int mimeLen = pos - mimeStart;
        pos++; // skip null terminator

        // picture type byte
        if (pos >= start + size) return null;
        pos++;

        // description (null-terminated, encoding-dependent)
        if (encoding == 1 || encoding == 2)
        {
            // UTF-16: skip until 0x00 0x00
            while (pos + 1 < start + size)
            {
                if (data[pos] == 0 && data[pos + 1] == 0) { pos += 2; break; }
                pos += 2;
            }
        }
        else
        {
            while (pos < start + size && data[pos] != 0) pos++;
            if (pos < start + size) pos++;
        }

        // Remaining bytes are image data
        int imageLen = start + size - pos;
        if (imageLen <= 0) return null;

        var image = new byte[imageLen];
        Array.Copy(data, pos, image, 0, imageLen);
        return image;
    }

    // ==================== USLT Decoding ====================

    private string DecodeLyricsBody(byte[] data, int start, int size)
    {
        if (size < 5) return "";
        int encoding = data[start];
        int contentStart = start + 4;

        int textStart = contentStart;
        if (encoding == 1 || encoding == 2)
        {
            while (textStart + 1 < start + size)
            {
                if (data[textStart] == 0 && data[textStart + 1] == 0) { textStart += 2; break; }
                textStart += 2;
            }
        }
        else
        {
            while (textStart < start + size && data[textStart] != 0) textStart++;
            if (textStart < start + size) textStart++;
        }

        int textLen = start + size - textStart;
        if (textLen <= 0) return "";

        var slice = new byte[textLen];
        Array.Copy(data, textStart, slice, 0, textLen);

        return encoding switch
        {
            0 => Encoding.Latin1.GetString(slice),
            1 => DecodeUtf16(slice, true),
            2 => DecodeUtf16(slice, false),
            3 => new UTF8Encoding(false, true).GetString(slice),
            _ => Encoding.UTF8.GetString(slice)
        };
    }

    private string DecodeUtf16(byte[] bytes, bool skipBom)
    {
        int offset = 0;
        if (skipBom && bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            offset = 2;
        else if (skipBom && bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            offset = 2;
        try { return Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset); }
        catch { return ""; }
    }

    // ==================== FLAC / OGG ====================

    private async Task<string?> ReadFlacVorbisAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var magic = new byte[4];
        if (fs.Length < 4) return null;
        if (!await ReadExactAsync(fs, magic, 4)) return null;
        if (magic[0] != (byte)'f' || magic[1] != (byte)'L' || magic[2] != (byte)'a' || magic[3] != (byte)'C') return null;

        while (fs.Position < fs.Length)
        {
            var blockHeader = new byte[4];
            if (!await ReadExactAsync(fs, blockHeader, 4)) break;
            bool last = (blockHeader[0] & 0x80) != 0;
            int type = blockHeader[0] & 0x7F;
            int length = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];

            if (type == 4)
            {
                var body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int r = await fs.ReadAsync(body, read, length - read);
                    if (r <= 0) break;
                    read += r;
                }
                return ParseVorbisLyrics(body);
            }

            fs.Seek(length, SeekOrigin.Current);
            if (last) break;
        }
        return null;
    }

    private string? ParseVorbisLyrics(byte[] body)
    {
        if (body.Length < 8) return null;
        int pos = 0;
        int vendorLen = ReadInt32LE(body, pos); pos += 4;
        if (pos + vendorLen > body.Length) return null;
        pos += vendorLen;
        if (pos + 4 > body.Length) return null;
        int commentCount = ReadInt32LE(body, pos); pos += 4;

        for (int i = 0; i < commentCount && pos + 4 <= body.Length; i++)
        {
            int len = ReadInt32LE(body, pos); pos += 4;
            if (pos + len > body.Length) break;
            var comment = Encoding.UTF8.GetString(body, pos, len);
            pos += len;

            var eq = comment.IndexOf('=');
            if (eq <= 0) continue;
            var key = comment[..eq].Trim().ToUpperInvariant();
            if (key is "LYRICS" or "UNSYNCEDLYRICS")
            {
                var value = comment[(eq + 1)..].Trim();
                if (value.Length > 0) return value;
            }
        }
        return null;
    }

    /// <summary>Parse METADATA_BLOCK_PICTURE from Vorbis comments and extract image data.</summary>
    private byte[]? ParseVorbisCover(byte[] body)
    {
        if (body.Length < 8) return null;
        int pos = 0;
        int vendorLen = ReadInt32LE(body, pos); pos += 4;
        if (pos + vendorLen > body.Length) return null;
        pos += vendorLen;
        if (pos + 4 > body.Length) return null;
        int commentCount = ReadInt32LE(body, pos); pos += 4;

        for (int i = 0; i < commentCount && pos + 4 <= body.Length; i++)
        {
            int len = ReadInt32LE(body, pos); pos += 4;
            if (pos + len > body.Length) break;
            var comment = Encoding.UTF8.GetString(body, pos, len);
            pos += len;

            var eq = comment.IndexOf('=');
            if (eq <= 0) continue;
            var key = comment[..eq].Trim().ToUpperInvariant();
            if (key == "METADATA_BLOCK_PICTURE")
            {
                var b64 = comment[(eq + 1)..].Trim();
                try
                {
                    var picBytes = Convert.FromBase64String(b64);
                    return ParseFlacPictureBlock(picBytes);
                }
                catch { return null; }
            }
        }
        return null;
    }

    /// <summary>Parse FLAC picture block to extract image bytes.</summary>
    private byte[]? ParseFlacPictureBlock(byte[] data)
    {
        if (data.Length < 8) return null;
        int pos = 0;

        // picture type (int32)
        pos += 4;

        // mime type length (int32 BE)
        if (pos + 4 > data.Length) return null;
        int mimeLen = ReadInt32BE(data, pos); pos += 4;
        if (pos + mimeLen > data.Length) return null;
        pos += mimeLen;

        // description length (int32 BE)
        if (pos + 4 > data.Length) return null;
        int descLen = ReadInt32BE(data, pos); pos += 4;
        if (pos + descLen > data.Length) return null;
        pos += descLen;

        // picture metadata (width, height, depth, colors — all int32 = 16 bytes)
        pos += 16;

        // image data length (int32 BE)
        if (pos + 4 > data.Length) return null;
        int dataLen = ReadInt32BE(data, pos); pos += 4;
        if (pos + dataLen > data.Length) return null;

        var image = new byte[dataLen];
        Array.Copy(data, pos, image, 0, dataLen);
        return image;
    }

    // ==================== OGG Vorbis ====================

    private async Task<string?> ReadOggVorbisAsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[64 * 1024];
        var collected = new List<byte>();
        while (collected.Count < 8 * 1024 * 1024)
        {
            int n = await fs.ReadAsync(buffer, 0, buffer.Length);
            if (n <= 0) break;
            collected.AddRange(buffer.AsSpan(0, n).ToArray());
        }
        var all = collected.ToArray();
        var text = Encoding.UTF8.GetString(all);

        foreach (var key in new[] { "LYRICS=", "UNSYNCEDLYRICS=" })
        {
            int idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            int start = idx + key.Length;
            int end = text.IndexOf("\0", start);
            if (end < 0) end = text.Length;
            var value = text[start..end].Trim();
            if (value.Length > 0) return value;
        }
        return null;
    }

    // ==================== Utility ====================

    private int ReadInt32BE(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }

    private int ReadInt32LE(byte[] data, int offset)
    {
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
    }

    private int ReadSyncSafeInt32(byte[] data, int offset)
    {
        return (data[offset] & 0x7F) << 21
             | (data[offset + 1] & 0x7F) << 14
             | (data[offset + 2] & 0x7F) << 7
             | (data[offset + 3] & 0x7F);
    }

    private async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
