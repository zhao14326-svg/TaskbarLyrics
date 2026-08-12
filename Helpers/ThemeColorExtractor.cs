using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TaskbarLyrics.Helpers;

/// <summary>封面取色生成的整套主题色板（5 组颜色，全局复用）。</summary>
public sealed record ThemePalette(
    string Primary,      // 主色调:封面最主要色相
    string Accent,       // 强调色:主色微调饱和度(描边/氛围光)
    string SurfaceRgb,   // 面板基底色 "r,g,b"(供 rgba 使用,低饱和高透明)
    string TextPrimary,  // 一级文字色(歌词),自动对比度
    string TextSecondary // 二级文字色(歌名),一级文字降透明度版本
);

/// <summary>
/// Extracts a full theme palette from album cover image bytes.
/// 压缩采样 64×64 → 直方图取主导色相 → 饱和度钳制 → 明暗自动对比度文字。
/// 封面为纯白/纯黑/灰度时返回 null(回退系统主题)。
/// </summary>
public static class ThemeColorExtractor
{
    /// <summary>仅提取主色(兼容旧调用)。</summary>
    public static string? ExtractDominant(byte[]? imageBytes) => ExtractPalette(imageBytes)?.Primary;

    /// <summary>提取整套主题色板；无封面/灰度封面返回 null。</summary>
    public static ThemePalette? ExtractPalette(byte[]? imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0) return null;
        try
        {
            var source = ImageDecoder.DecodeToBitmapSource(imageBytes);
            if (source == null) return null;

            // 压缩采样到 64×64 再提取像素，避免全图逐像素解析卡顿
            int w = Math.Min(source.PixelWidth, 64);
            int h = Math.Min(source.PixelHeight, 64);
            var stride = w * 4;
            var pixels = new byte[h * stride];
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(pixels, stride, 0);

            // 直方图(8bin/通道,512桶),跳过极亮/极暗/纯灰噪点
            var histo = new Dictionary<int, (int count, float totalSat, float totalVal)>();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * stride + x * 4;
                    byte b = pixels[idx], g = pixels[idx + 1], r = pixels[idx + 2];
                    int lum = (r + g + b) / 3;
                    if (lum < 15 || lum > 240 || (r == b && b == g)) continue;

                    int qKey = (r >> 5) << 6 | (g >> 5) << 3 | (b >> 5);
                    float max = Math.Max(r, Math.Max(g, b));
                    float min = Math.Min(r, Math.Min(g, b));
                    float sat = max > 0 ? (max - min) / max : 0;
                    float val = max / 255f;
                    if (histo.TryGetValue(qKey, out var v))
                        histo[qKey] = (v.count + 1, v.totalSat + sat, v.totalVal + val);
                    else
                        histo[qKey] = (1, sat, val);
                }
            }
            if (histo.Count == 0) return null;

            // 按 数量 × 平均饱和度 × 平均亮度 选取主导色
            var best = histo.OrderByDescending(kv =>
            {
                var (cnt, satSum, valSum) = kv.Value;
                return cnt * (satSum / cnt) * (valSum / cnt);
            }).First();

            int q = best.Key;
            int cr = (q >> 6 & 7) * 36 + 16; // 3-bit 还原 8-bit
            int cg = (q >> 3 & 7) * 36 + 16;
            int cb = (q & 7) * 36 + 16;

            var (hue, hueSat, hueLight) = RgbToHsl(cr, cg, cb);

            // 黑白灰处理:主色饱和度极低 → 放弃取色,回退系统主题
            if (hueSat < 0.08) return null;

            // 饱和度钳制:强制上限 42%(规格要求 35%~45%)
            double clampedSat = Math.Min(hueSat, 0.42);

            // primary:钳制后的主色
            var (pr, pg, pb) = HslToRgb(hue, clampedSat, hueLight);
            string primary = ToHex(pr, pg, pb);

            // accent:主色略提饱和/亮度,用于微弱描边与氛围光
            var (ar, ag, ab) = HslToRgb(hue, Math.Min(1, clampedSat + 0.12), Math.Min(1, hueLight + 0.05));
            string accent = ToHex(ar, ag, ab);

            // surface-bg:主色大幅降饱和 + 压低亮度,供磨砂面板叠加层使用
            var (sr, sg, sb) = HslToRgb(hue, clampedSat * 0.30, Math.Min(0.9, Math.Max(0.08, hueLight * 0.8)));
            string surface = $"{sr},{sg},{sb}";

            // 文字自动对比度(WCAG):按主色亮度决定深/浅文字
            double textLum = (pr * 0.299 + pg * 0.587 + pb * 0.114) / 255.0;
            bool lightBg = textLum > 0.5;
            string textPrimary = lightBg ? "#18181C" : "#F5F5F7";
            string textSecondary = lightBg ? "#3A3A42" : "#C9C9D2";

            return new ThemePalette(primary, accent, surface, textPrimary, textSecondary);
        }
        catch
        {
            return null;
        }
    }

    // ==================== HSL 工具 ====================

    private static (double h, double s, double l) RgbToHsl(int r, int g, int b)
    {
        double rn = r / 255.0, gn = g / 255.0, bn = b / 255.0;
        double max = Math.Max(rn, Math.Max(gn, bn)), min = Math.Min(rn, Math.Min(gn, bn));
        double l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        double d = max - min;
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == rn) h = (gn - bn) / d + (gn < bn ? 6 : 0);
        else if (max == gn) h = (bn - rn) / d + 2;
        else h = (rn - gn) / d + 4;
        h /= 6;
        return (h, s, l);
    }

    private static (int r, int g, int b) HslToRgb(double h, double s, double l)
    {
        if (s == 0) { int v = (int)(l * 255); return (v, v, v); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double r = HueToRgb(p, q, h + 1.0 / 3);
        double g = HueToRgb(p, q, h);
        double b = HueToRgb(p, q, h - 1.0 / 3);
        return ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static string ToHex(int r, int g, int b)
        => $"#{Math.Clamp(r, 0, 255):X2}{Math.Clamp(g, 0, 255):X2}{Math.Clamp(b, 0, 255):X2}";
}

