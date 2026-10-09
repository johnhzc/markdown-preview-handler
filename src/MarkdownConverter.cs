using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownPreview
{
    /// <summary>
    /// 轻量 Markdown → HTML 转换器（GFM 风格子集），零第三方依赖。
    /// 支持：ATX/Setext 标题、围栏代码块、行内代码、有序/无序列表（任意层级嵌套）、
    /// 任务列表、引用块（可嵌套）、表格、水平线、粗体/斜体/删除线、链接/图片/自动链接。
    /// </summary>
    public static class MarkdownConverter
    {
        private static readonly Regex FenceRegex = new Regex(@"^ {0,3}(`{3,}|~{3,})\s*([^`]*)\s*$");
        private static readonly Regex HeadingRegex = new Regex(@"^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
        private static readonly Regex HrRegex = new Regex(@"^ {0,3}(\*[ \t]*){3,}$|^ {0,3}(-[ \t]*){3,}$|^ {0,3}(_[ \t]*){3,}$");
        private static readonly Regex SetextH1Regex = new Regex(@"^ {0,3}=+\s*$");
        private static readonly Regex SetextH2Regex = new Regex(@"^ {0,3}-+\s*$");
        private static readonly Regex QuoteRegex = new Regex(@"^ {0,3}>");
        private static readonly Regex ListRegex = new Regex(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.+)$");
        private static readonly Regex TaskRegex = new Regex(@"^\[([xX]| )\]\s+(.*)$");
        private static readonly Regex TableSepRegex = new Regex(@"^ {0,3}\|?(\s*:?-{3,}:?\s*\|)+\s*:?-{3,}:?\s*\|?\s*$|^\s*\|?\s*:?-{3,}:?\s*\|?\s*$");
        private static readonly Regex InlineCodeRegex = new Regex(@"(`+)([^`]+?)\1");
        private static readonly Regex ImageRegex = new Regex(@"!\[([^\]]*)\]\(([^)\s]+)(\s+""[^""]*"")?\)");
        private static readonly Regex LinkRegex = new Regex(@"(?<!!)\[([^\]]*)\]\(([^)\s]+)(\s+""[^""]*"")?\)");
        private static readonly Regex AutolinkRegex = new Regex(@"(?<![(""'>=])(\bhttps?://[^\s<>()\[\]]+[^\s<>()\[\]""'.,:;!?])");
        private static readonly Regex BoldStarRegex = new Regex(@"\*\*(.+?)\*\*");
        private static readonly Regex BoldUnderscoreRegex = new Regex(@"(?<![A-Za-z0-9_])__(.+?)__(?![A-Za-z0-9_])");
        private static readonly Regex ItalicStarRegex = new Regex(@"(?<![A-Za-z0-9*])\*([^*\n]+?)\*(?![A-Za-z0-9*])");
        private static readonly Regex ItalicUnderscoreRegex = new Regex(@"(?<![A-Za-z0-9_])_([^_\n]+?)_(?![A-Za-z0-9_])");
        private static readonly Regex StrikeRegex = new Regex(@"~~([^~\n]+?)~~");
        private static readonly Regex PlaceholderRegex = new Regex("\u0001(\\d+)\u0002");

        private class ListFrame
        {
            public int Indent;
            public bool Ordered;
        }

        // ---------------- 对外入口 ----------------

        /// <summary>
        /// 将 Markdown 文本转换为完整 HTML 文档。
        /// </summary>
        /// <param name="markdown">Markdown 源文本</param>
        /// <param name="sourceFilePath">源文件路径（用于解析相对路径的图片/链接，可为 null）</param>
        /// <param name="plainTextMode">true 时按纯文本渲染（不做 Markdown 解析）</param>
        public static string ToHtml(string markdown, string sourceFilePath, bool plainTextMode)
        {
            if (markdown == null) markdown = "";

            string baseDir = null;
            if (!string.IsNullOrEmpty(sourceFilePath))
            {
                try { baseDir = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath)); }
                catch { baseDir = null; }
            }

            string body;
            if (plainTextMode)
            {
                body = "<pre class=\"plain\">" + EscapeHtml(markdown) + "</pre>";
            }
            else
            {
                string normalized = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
                body = RenderBlocks(normalized.Split('\n'), baseDir);
            }

            StringBuilder sb = new StringBuilder(4096);
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\" />");
            sb.Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\" />");
            sb.Append("<title>Preview</title><style>");
            sb.Append(Css);
            sb.Append("</style></head><body>");
            sb.Append(body);
            sb.Append("</body></html>");
            return sb.ToString();
        }

        // ---------------- 块级渲染 ----------------

        private static string RenderBlocks(string[] lines, string baseDir)
        {
            StringBuilder outSb = new StringBuilder();
            int i = 0;
            int n = lines.Length;

            while (i < n)
            {
                string line = lines[i];

                if (line.Trim().Length == 0) { i++; continue; }

                // 围栏代码块
                Match fence = FenceRegex.Match(line);
                if (fence.Success)
                {
                    outSb.Append(RenderFencedBlock(lines, ref i, fence, baseDir));
                    continue;
                }

                // ATX 标题
                Match heading = HeadingRegex.Match(line);
                if (heading.Success)
                {
                    int level = heading.Groups[1].Value.Length;
                    outSb.Append("<h").Append(level).Append(">")
                         .Append(InlineRender(heading.Groups[2].Value, baseDir))
                         .Append("</h").Append(level).Append(">");
                    i++;
                    continue;
                }

                // 水平线
                if (HrRegex.IsMatch(line))
                {
                    outSb.Append("<hr />");
                    i++;
                    continue;
                }

                // 引用块（剥离 "> " 前缀后递归渲染，天然支持嵌套）
                if (QuoteRegex.IsMatch(line))
                {
                    List<string> inner = new List<string>();
                    while (i < n && QuoteRegex.IsMatch(lines[i]))
                    {
                        inner.Add(Regex.Replace(lines[i], "^ {0,3}> ?", ""));
                        i++;
                    }
                    outSb.Append("<blockquote>")
                         .Append(RenderBlocks(inner.ToArray(), baseDir))
                         .Append("</blockquote>");
                    continue;
                }

                // 表格（GFM 管道表格：当前行含 | 且下一行是分隔行）
                if (i + 1 < n && line.Contains("|") && TableSepRegex.IsMatch(lines[i + 1]))
                {
                    outSb.Append(RenderTable(lines, ref i, baseDir));
                    continue;
                }

                // 有序/无序列表（缩进栈算法，支持嵌套与任务列表）
                if (ListRegex.IsMatch(line))
                {
                    RenderList(lines, ref i, baseDir, outSb);
                    continue;
                }

                // 段落（含 Setext 标题检测）
                outSb.Append(RenderParagraph(lines, ref i, baseDir));
            }

            return outSb.ToString();
        }

        private static string RenderFencedBlock(string[] lines, ref int i, Match fence, string baseDir)
        {
            string fenceMark = fence.Groups[1].Value;
            string lang = fence.Groups[2].Value.Trim();
            char fenceChar = fenceMark[0];
            Regex closeRegex = new Regex("^ {0,3}" + Regex.Escape(new string(fenceChar, 1))
                + "{" + fenceMark.Length + ",}\\s*$");

            StringBuilder code = new StringBuilder();
            i++;
            while (i < lines.Length)
            {
                if (closeRegex.IsMatch(lines[i])) { i++; break; }
                code.Append(lines[i]).Append('\n');
                i++;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("<pre><code");
            if (lang.Length > 0)
            {
                sb.Append(" class=\"language-").Append(EscapeHtml(lang)).Append("\"");
            }
            sb.Append(">").Append(EscapeHtml(code.ToString())).Append("</code></pre>");
            return sb.ToString();
        }

        private static string RenderParagraph(string[] lines, ref int i, string baseDir)
        {
            int n = lines.Length;
            List<string> para = new List<string>();
            string setextLevel = null;

            while (i < n)
            {
                string cur = lines[i];
                if (cur.Trim().Length == 0) { i++; break; }

                if (para.Count > 0)
                {
                    if (SetextH1Regex.IsMatch(cur)) { setextLevel = "1"; i++; break; }
                    if (SetextH2Regex.IsMatch(cur)) { setextLevel = "2"; i++; break; }
                    if (IsBlockStart(cur)) break;
                    if (cur.Contains("|") && i + 1 < n && TableSepRegex.IsMatch(lines[i + 1])) break;
                }

                para.Add(cur);
                i++;
            }

            string joined = string.Join("\n", para.ToArray());
            if (setextLevel != null)
            {
                return "<h" + setextLevel + ">" + InlineRender(joined, baseDir) + "</h" + setextLevel + ">";
            }
            return "<p>" + InlineRender(joined, baseDir) + "</p>";
        }

        private static void RenderList(string[] lines, ref int i, string baseDir, StringBuilder outSb)
        {
            int n = lines.Length;
            List<ListFrame> stack = new List<ListFrame>();
            StringBuilder listSb = new StringBuilder();

            while (i < n)
            {
                Match m = ListRegex.Match(lines[i]);
                if (m.Success)
                {
                    int indent = m.Groups[1].Value.Length;
                    char marker = m.Groups[2].Value[0];
                    bool ordered = marker != '-' && marker != '*' && marker != '+';
                    string content = m.Groups[3].Value.Trim();

                    // 先弹出比当前缩进更深的层级
                    while (stack.Count > 0 && indent < stack[stack.Count - 1].Indent)
                    {
                        listSb.Append("</li></").Append(stack[stack.Count - 1].Ordered ? "ol" : "ul").Append(">");
                        stack.RemoveAt(stack.Count - 1);
                    }

                    if (stack.Count == 0 || indent > stack[stack.Count - 1].Indent)
                    {
                        // 新开列表（顶层或嵌套；嵌套时父 li 保持开放）
                        listSb.Append(ordered ? "<ol>" : "<ul>");
                        stack.Add(new ListFrame { Indent = indent, Ordered = ordered });
                    }
                    else if (ordered != stack[stack.Count - 1].Ordered)
                    {
                        // 同缩进但列表类型变化：关闭重开
                        listSb.Append("</li></").Append(stack[stack.Count - 1].Ordered ? "ol" : "ul").Append(">");
                        stack.RemoveAt(stack.Count - 1);
                        listSb.Append(ordered ? "<ol>" : "<ul>");
                        stack.Add(new ListFrame { Indent = indent, Ordered = ordered });
                    }
                    else
                    {
                        listSb.Append("</li>"); // 同级下一项
                    }

                    // 任务列表 [ ] / [x]
                    Match task = TaskRegex.Match(content);
                    if (task.Success)
                    {
                        string mark = task.Groups[1].Value;
                        bool isChecked = mark == "x" || mark == "X";
                        listSb.Append("<li>");
                        listSb.Append(isChecked
                            ? "<input type=\"checkbox\" checked=\"checked\" disabled=\"disabled\" /> "
                            : "<input type=\"checkbox\" disabled=\"disabled\" /> ");
                        listSb.Append(InlineRender(task.Groups[2].Value, baseDir));
                    }
                    else
                    {
                        listSb.Append("<li>").Append(InlineRender(content, baseDir));
                    }
                    i++;
                }
                else
                {
                    string t = lines[i];
                    if (t.Trim().Length == 0) break;   // 空行结束列表
                    if (IsBlockStart(t)) break;        // 新块开始，结束列表
                    // 列表项的续行
                    listSb.Append("<br />").Append(InlineRender(t.Trim(), baseDir));
                    i++;
                }
            }

            while (stack.Count > 0)
            {
                listSb.Append("</li></").Append(stack[stack.Count - 1].Ordered ? "ol" : "ul").Append(">");
                stack.RemoveAt(stack.Count - 1);
            }

            outSb.Append(listSb.ToString());
        }

        private static string RenderTable(string[] lines, ref int i, string baseDir)
        {
            List<string> headers = SplitTableRow(lines[i]);
            List<string> sepCells = SplitTableRow(lines[i + 1]);
            i += 2;

            List<int> aligns = ParseAligns(sepCells, headers.Count);

            StringBuilder rows = new StringBuilder();
            while (i < lines.Length)
            {
                string row = lines[i];
                if (row.Trim().Length == 0 || !row.Contains("|")) break;
                List<string> cells = SplitTableRow(row);
                rows.Append("<tr>");
                for (int c = 0; c < headers.Count; c++)
                {
                    string cellText = c < cells.Count ? cells[c] : "";
                    rows.Append("<td").Append(AlignStyle(aligns, c)).Append(">")
                        .Append(InlineRender(cellText, baseDir))
                        .Append("</td>");
                }
                rows.Append("</tr>");
                i++;
            }

            StringBuilder thead = new StringBuilder();
            thead.Append("<thead><tr>");
            for (int c = 0; c < headers.Count; c++)
            {
                thead.Append("<th").Append(AlignStyle(aligns, c)).Append(">")
                     .Append(InlineRender(headers[c], baseDir))
                     .Append("</th>");
            }
            thead.Append("</tr></thead>");

            return "<table>" + thead.ToString() + "<tbody>" + rows.ToString() + "</tbody></table>";
        }

        private static List<string> SplitTableRow(string row)
        {
            string t = row.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            List<string> cells = new List<string>(t.Split('|'));
            for (int k = 0; k < cells.Count; k++) cells[k] = cells[k].Trim();
            return cells;
        }

        private static List<int> ParseAligns(List<string> sepCells, int colCount)
        {
            List<int> aligns = new List<int>();
            for (int c = 0; c < colCount; c++)
            {
                if (c >= sepCells.Count) { aligns.Add(0); continue; }
                string s = sepCells[c];
                bool left = s.StartsWith(":");
                bool right = s.EndsWith(":");
                aligns.Add(left && right ? 2 : (right ? 1 : 0));
            }
            return aligns;
        }

        private static string AlignStyle(List<int> aligns, int col)
        {
            if (col >= aligns.Count || aligns[col] == 0) return "";
            return aligns[col] == 1 ? " style=\"text-align:right\"" : " style=\"text-align:center\"";
        }

        private static bool IsBlockStart(string line)
        {
            return FenceRegex.IsMatch(line)
                || HeadingRegex.IsMatch(line)
                || QuoteRegex.IsMatch(line)
                || HrRegex.IsMatch(line)
                || ListRegex.IsMatch(line);
        }

        // ---------------- 行内渲染 ----------------

        private static string InlineRender(string text, string baseDir)
        {
            if (text == null) return "";
            string s = EscapeHtml(text);
            List<string> placeholders = new List<string>();

            // 1. 行内代码（最先处理，占位保护）
            s = InlineCodeRegex.Replace(s, delegate(Match m)
            {
                placeholders.Add("<code>" + m.Groups[2].Value.Trim() + "</code>");
                return "\u0001" + (placeholders.Count - 1) + "\u0002";
            });

            // 2. 图片（相对路径基于源文件目录解析为 file:/// 绝对地址）
            s = ImageRegex.Replace(s, delegate(Match m)
            {
                string url = ResolveUrl(m.Groups[2].Value, baseDir);
                placeholders.Add("<img src=\"" + url + "\" alt=\"" + m.Groups[1].Value + "\" />");
                return "\u0001" + (placeholders.Count - 1) + "\u0002";
            });

            // 3. 链接
            s = LinkRegex.Replace(s, delegate(Match m)
            {
                string url = ResolveUrl(m.Groups[2].Value, baseDir);
                placeholders.Add("<a href=\"" + url + "\">" + m.Groups[1].Value + "</a>");
                return "\u0001" + (placeholders.Count - 1) + "\u0002";
            });

            // 4. 自动链接
            s = AutolinkRegex.Replace(s, delegate(Match m)
            {
                string url = m.Groups[1].Value;
                placeholders.Add("<a href=\"" + url + "\">" + url + "</a>");
                return "\u0001" + (placeholders.Count - 1) + "\u0002";
            });

            // 5. 强调与删除线
            s = BoldStarRegex.Replace(s, "<strong>$1</strong>");
            s = BoldUnderscoreRegex.Replace(s, "<strong>$1</strong>");
            s = ItalicStarRegex.Replace(s, "<em>$1</em>");
            s = ItalicUnderscoreRegex.Replace(s, "<em>$1</em>");
            s = StrikeRegex.Replace(s, "<del>$1</del>");

            // 6. 换行（预览场景按 GFM breaks 风格处理）
            s = s.Replace("\n", "<br />");

            // 7. 还原占位符
            s = PlaceholderRegex.Replace(s, delegate(Match m)
            {
                int idx = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                return idx < placeholders.Count ? placeholders[idx] : "";
            });

            return s;
        }

        /// <summary>相对路径 → file:/// 绝对地址（基于 Markdown 源文件所在目录）。</summary>
        private static string ResolveUrl(string url, string baseDir)
        {
            if (string.IsNullOrEmpty(url)) return url;
            string lower = url.ToLowerInvariant();
            if (lower.StartsWith("http://") || lower.StartsWith("https://") ||
                lower.StartsWith("file://") || lower.StartsWith("data:") ||
                url.StartsWith("#") || url.StartsWith("/"))
            {
                return url;
            }
            if (baseDir != null)
            {
                try
                {
                    return new Uri(Path.GetFullPath(Path.Combine(baseDir, url))).AbsoluteUri;
                }
                catch { }
            }
            return url;
        }

        private static string EscapeHtml(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;");
        }

        // ---------------- 样式 ----------------

        private const string Css =
            "body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;font-size:14px;line-height:1.65;" +
            "color:#1f2328;background:#fff;margin:0;padding:16px 20px;word-wrap:break-word}" +
            "h1{font-size:1.65em;margin:16px 0 10px;padding-bottom:6px;border-bottom:1px solid #d8dee4}" +
            "h2{font-size:1.35em;margin:14px 0 8px;padding-bottom:5px;border-bottom:1px solid #e7ebef}" +
            "h3{font-size:1.18em;margin:12px 0 6px}" +
            "h4{font-size:1.05em;margin:10px 0 5px}" +
            "h5{font-size:0.98em;margin:10px 0 5px}" +
            "h6{font-size:0.92em;color:#59636e;margin:10px 0 5px}" +
            "p{margin:7px 0}" +
            "a{color:#0969da;text-decoration:none}" +
            "a:hover{text-decoration:underline}" +
            "code{font-family:Consolas,'Courier New',monospace;font-size:0.92em;background:#f0f3f6;" +
            "border:1px solid #e3e8ee;padding:1px 4px;border-radius:3px}" +
            "pre{background:#f6f8fa;border:1px solid #e3e8ee;padding:10px 12px;margin:10px 0;overflow:auto}" +
            "pre code{background:none;border:none;padding:0;display:block;white-space:pre}" +
            "pre.plain{background:#fbfbfc}" +
            "blockquote{margin:10px 0;padding:4px 14px;border-left:3px solid #d0d7de;background:#f9fafb;color:#59636e}" +
            "blockquote p{margin:4px 0}" +
            "ul,ol{margin:7px 0;padding-left:26px}" +
            "li{margin:3px 0}" +
            "table{border-collapse:collapse;margin:10px 0}" +
            "th,td{border:1px solid #d0d7de;padding:5px 10px}" +
            "th{background:#f6f8fa}" +
            "img{max-width:100%}" +
            "hr{border:none;border-top:1px solid #d8dee4;margin:14px 0}" +
            "del{color:#8c959f}" +
            "input[type=checkbox]{margin-right:5px;vertical-align:middle}";
    }
}
