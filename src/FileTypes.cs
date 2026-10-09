using System;
using System.Text;

namespace MarkdownPreview
{
    /// <summary>文件类型判断与文本解码工具。</summary>
    public static class FileTypes
    {
        public static readonly string[] ImageExtensions =
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico"
        };

        public static readonly string[] MarkdownExtensions =
        {
            ".md", ".markdown", ".mdown", ".mkd"
        };

        public static bool IsImageExtension(string ext)
        {
            return Array.IndexOf(ImageExtensions, ext) >= 0;
        }

        public static bool IsMarkdownExtension(string ext)
        {
            return Array.IndexOf(MarkdownExtensions, ext) >= 0;
        }

        /// <summary>按魔数（magic bytes）判断二进制数据是否为常见图片格式。</summary>
        public static bool IsImageBytes(byte[] d)
        {
            if (d == null || d.Length < 12) return false;
            if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return true;                     // JPEG
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true;     // PNG
            if (d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return true;                     // GIF
            if (d[0] == 0x42 && d[1] == 0x4D) return true;                                     // BMP
            if (d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A && d[3] == 0x00) return true;     // TIFF II
            if (d[0] == 0x4D && d[1] == 0x4D && d[2] == 0x00 && d[3] == 0x2A) return true;     // TIFF MM
            if (d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46 &&
                d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return true;   // WEBP
            return false;
        }

        /// <summary>对纯文本内容做启发式 Markdown 特征检测（用于流初始化时无扩展名的场景）。</summary>
        public static bool LooksLikeMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string[] lines = text.Split('\n');
            int max = Math.Min(lines.Length, 120);
            for (int i = 0; i < max; i++)
            {
                string line = lines[i];
                if (line == null) continue;
                string t = line.TrimStart();
                if (t.StartsWith("# ", StringComparison.Ordinal)) return true;
                if (t.StartsWith("## ", StringComparison.Ordinal)) return true;
                if (t.StartsWith("```", StringComparison.Ordinal)) return true;
                if (t.StartsWith("> ", StringComparison.Ordinal)) return true;
                if (t.StartsWith("- ", StringComparison.Ordinal)) return true;
                if (t.StartsWith("* ", StringComparison.Ordinal)) return true;
                if (line.Contains("](")) return true;
                if (line.Contains("**")) return true;
            }
            return false;
        }

        /// <summary>
        /// 智能文本解码：识别 BOM；无 BOM 时先按严格 UTF-8 尝试，
        /// 失败则回退系统默认 ANSI（中文系统为 GBK）。
        /// </summary>
        public static string ReadTextSmart(byte[] data)
        {
            if (data == null || data.Length == 0) return "";

            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                return new UTF8Encoding(false).GetString(data, 3, data.Length - 3);
            }
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            }
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            }

            try
            {
                return new UTF8Encoding(false, true).GetString(data);
            }
            catch (System.Text.DecoderFallbackException)
            {
                return Encoding.Default.GetString(data);
            }
        }
    }
}
