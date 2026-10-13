// MarkdownToHtml.cpp
//
// Self-contained GFM-ish Markdown -> HTML converter plus the stylesheet.
// Written for the native preview handler so the DLL needs no third-party code.
//
// Supported: ATX headings, fenced/indented code blocks, blockquotes, ordered
// and unordered lists (one nesting level), tables with alignment, horizontal
// rules, bold/italic/strike, inline code, links, images, autolinks, hard
// line breaks. Relative image paths are rewritten against the source folder.

#include "MarkdownPreviewHandler.h"
#include <sstream>
#include <algorithm>

namespace {

// --------------------------------------------------------------------------
// small helpers
// --------------------------------------------------------------------------

bool StartsWith(const std::wstring& s, const wchar_t* prefix)
{
    size_t n = wcslen(prefix);
    return s.size() >= n && s.compare(0, n, prefix) == 0;
}

std::wstring Trim(const std::wstring& s)
{
    size_t b = s.find_first_not_of(L" \t\r\n");
    if (b == std::wstring::npos) return L"";
    size_t e = s.find_last_not_of(L" \t\r\n");
    return s.substr(b, e - b + 1);
}

bool IsBlank(const std::wstring& s)
{
    return s.find_first_not_of(L" \t\r\n") == std::wstring::npos;
}

std::wstring EscapeHtml(const std::wstring& s)
{
    std::wstring out;
    out.reserve(s.size() + 16);
    for (wchar_t c : s)
    {
        switch (c)
        {
        case L'&':  out += L"&amp;";  break;
        case L'<':  out += L"&lt;";   break;
        case L'>':  out += L"&gt;";   break;
        case L'"':  out += L"&quot;"; break;
        default:    out += c;         break;
        }
    }
    return out;
}

std::wstring EscapeAttr(const std::wstring& s) { return EscapeHtml(s); }

std::wstring ToLowerAscii(const std::wstring& s)
{
    std::wstring r = s;
    for (wchar_t& c : r) if (c >= L'A' && c <= L'Z') c = (wchar_t)(c - L'A' + L'a');
    return r;
}

// percent-encode a file path for use in an href/src
std::wstring PathToFileUrl(const std::wstring& path)
{
    std::wstring out = L"file:///";
    for (wchar_t c : path)
    {
        if (c == L'\\') out += L'/';
        else if (c == L' ') out += L"%20";
        else if (c == L'#') out += L"%23";
        else if (c == L'%') out += L"%25";
        else out += c;
    }
    return out;
}

bool IsAbsolutePath(const std::wstring& p)
{
    if (p.size() >= 2 && p[1] == L':') return true;                 // C:\...
    if (p.size() >= 2 && p[0] == L'\\' && p[1] == L'\\') return true; // UNC
    if (!p.empty() && (p[0] == L'/' )) return true;
    return false;
}

// --------------------------------------------------------------------------
// inline formatting
// --------------------------------------------------------------------------

// Handles `code`, **bold**, *italic*, ~~strike~~, [text](url), ![alt](src),
// <http://autolink>. Deliberately simple: no nesting of emphasis inside links.
std::wstring RenderInline(const std::wstring& text, const std::wstring& sourceDir)
{
    std::wstring out;
    size_t i = 0;
    const size_t n = text.size();

    while (i < n)
    {
        wchar_t c = text[i];

        // inline code
        if (c == L'`')
        {
            size_t end = text.find(L'`', i + 1);
            if (end != std::wstring::npos)
            {
                out += L"<code>";
                out += EscapeHtml(text.substr(i + 1, end - i - 1));
                out += L"</code>";
                i = end + 1;
                continue;
            }
        }

        // image ![alt](src)
        if (c == L'!' && i + 1 < n && text[i + 1] == L'[')
        {
            size_t close = text.find(L']', i + 2);
            if (close != std::wstring::npos && close + 1 < n && text[close + 1] == L'(')
            {
                size_t paren = text.find(L')', close + 2);
                if (paren != std::wstring::npos)
                {
                    std::wstring alt = text.substr(i + 2, close - i - 2);
                    std::wstring src = Trim(text.substr(close + 2, paren - close - 2));
                    if (!IsAbsolutePath(src) && !StartsWith(src, L"http") && !StartsWith(src, L"data:"))
                    {
                        if (!sourceDir.empty()) src = sourceDir + L"\\" + src;
                    }
                    out += L"<img src=\"" + EscapeAttr(StartsWith(src, L"http") || StartsWith(src, L"data:")
                        ? src : PathToFileUrl(src)) + L"\" alt=\"" + EscapeAttr(alt) + L"\">";
                    i = paren + 1;
                    continue;
                }
            }
        }

        // link [text](url)
        if (c == L'[')
        {
            size_t close = text.find(L']', i + 1);
            if (close != std::wstring::npos && close + 1 < n && text[close + 1] == L'(')
            {
                size_t paren = text.find(L')', close + 2);
                if (paren != std::wstring::npos)
                {
                    std::wstring label = text.substr(i + 1, close - i - 1);
                    std::wstring url = Trim(text.substr(close + 2, paren - close - 2));
                    out += L"<a href=\"" + EscapeAttr(url) + L"\">" + RenderInline(label, sourceDir) + L"</a>";
                    i = paren + 1;
                    continue;
                }
            }
        }

        // autolink <http://...>
        if (c == L'<' && i + 7 < n &&
            (text.compare(i + 1, 7, L"http://") == 0 || text.compare(i + 1, 8, L"https://") == 0))
        {
            size_t end = text.find(L'>', i);
            if (end != std::wstring::npos)
            {
                std::wstring url = text.substr(i + 1, end - i - 1);
                out += L"<a href=\"" + EscapeAttr(url) + L"\">" + EscapeHtml(url) + L"</a>";
                i = end + 1;
                continue;
            }
        }

        // bold / italic / strike
        if (c == L'*' || c == L'_')
        {
            int run = 1;
            while (i + run < n && text[i + run] == c) run++;
            const wchar_t* tag = (run >= 2) ? ((c == L'*') ? L"strong" : L"strong") : ((c == L'*') ? L"em" : L"em");
            std::wstring delim(run >= 2 ? 2 : 1, c);
            size_t end = text.find(delim, i + delim.size());
            if (end != std::wstring::npos && end > i + delim.size())
            {
                std::wstring inner = text.substr(i + delim.size(), end - i - delim.size());
                out += L"<"; out += tag; out += L">";
                out += RenderInline(inner, sourceDir);
                out += L"</"; out += tag; out += L">";
                i = end + delim.size();
                continue;
            }
        }
        if (c == L'~' && i + 1 < n && text[i + 1] == L'~')
        {
            size_t end = text.find(L"~~", i + 2);
            if (end != std::wstring::npos)
            {
                out += L"<del>" + RenderInline(text.substr(i + 2, end - i - 2), sourceDir) + L"</del>";
                i = end + 2;
                continue;
            }
        }

        out += EscapeHtml(std::wstring(1, c));
        i++;
    }
    return out;
}

// --------------------------------------------------------------------------
// tables
// --------------------------------------------------------------------------

std::vector<std::wstring> SplitRow(const std::wstring& line)
{
    std::vector<std::wstring> cells;
    std::wstring cur;
    bool escaped = false;
    // trim leading/trailing pipe
    size_t b = line.find_first_not_of(L" \t");
    size_t e = line.find_last_not_of(L" \t");
    std::wstring body = (b == std::wstring::npos) ? L"" : line.substr(b, e - b + 1);
    if (!body.empty() && body[0] == L'|') body.erase(0, 1);
    if (!body.empty() && body[body.size() - 1] == L'|') body.erase(body.size() - 1);

    for (wchar_t c : body)
    {
        if (escaped) { cur += c; escaped = false; continue; }
        if (c == L'\\') { escaped = true; continue; }
        if (c == L'|') { cells.push_back(Trim(cur)); cur.clear(); continue; }
        cur += c;
    }
    cells.push_back(Trim(cur));
    return cells;
}

bool IsTableSeparator(const std::wstring& line)
{
    std::wstring t = Trim(line);
    if (t.empty()) return false;
    bool hasDash = false;
    for (wchar_t c : t)
    {
        if (c == L'-') { hasDash = true; continue; }
        if (c == L'|' || c == L':' || c == L' ' || c == L'\t') continue;
        return false;
    }
    return hasDash;
}

} // namespace

// --------------------------------------------------------------------------
// document conversion
// --------------------------------------------------------------------------

std::wstring MarkdownToHtmlDocument(const std::wstring& md, const std::wstring& sourceDir)
{
    std::wstring body;
    body.reserve(md.size() * 2);

    // normalise newlines
    std::wstring text;
    text.reserve(md.size());
    for (size_t i = 0; i < md.size(); i++)
    {
        if (md[i] == L'\r')
        {
            if (i + 1 < md.size() && md[i + 1] == L'\n') continue;
            text += L'\n';
        }
        else text += md[i];
    }

    // split into lines
    std::vector<std::wstring> lines;
    {
        std::wstring cur;
        for (wchar_t c : text)
        {
            if (c == L'\n') { lines.push_back(cur); cur.clear(); }
            else cur += c;
        }
        lines.push_back(cur);
    }

    size_t i = 0;
    while (i < lines.size())
    {
        std::wstring line = lines[i];

        // blank
        if (IsBlank(line)) { i++; continue; }

        // fenced code block
        if (StartsWith(Trim(line), L"```"))
        {
            std::wstring lang = Trim(Trim(line).substr(3));
            i++;
            std::wstring code;
            while (i < lines.size() && !StartsWith(Trim(lines[i]), L"```"))
            {
                code += lines[i];
                code += L'\n';
                i++;
            }
            if (i < lines.size()) i++; // skip closing fence
            body += L"<pre><code";
            if (!lang.empty()) body += L" class=\"language-" + EscapeAttr(lang) + L"\"";
            body += L">" + EscapeHtml(code) + L"</code></pre>\n";
            continue;
        }

        // horizontal rule
        {
            std::wstring t = Trim(line);
            bool hr = t.size() >= 3;
            if (hr)
            {
                wchar_t c0 = t[0];
                if (c0 == L'-' || c0 == L'*' || c0 == L'_')
                {
                    for (wchar_t c : t) if (c != c0 && c != L' ') { hr = false; break; }
                }
                else hr = false;
                if (hr) { body += L"<hr>\n"; i++; continue; }
            }
        }

        // ATX heading
        if (line[0] == L'#')
        {
            int level = 0;
            size_t p = 0;
            while (p < line.size() && line[p] == L'#' && level < 6) { level++; p++; }
            if (level > 0 && (p >= line.size() || line[p] == L' ' || line[p] == L'\t'))
            {
                std::wstring title = Trim(line.substr(p));
                // strip trailing #
                while (!title.empty() && title[title.size() - 1] == L'#') title.erase(title.size() - 1);
                title = Trim(title);
                wchar_t tag[8];
                swprintf_s(tag, L"h%d", level);
                body += L"<"; body += tag; body += L">";
                body += RenderInline(title, sourceDir);
                body += L"</"; body += tag; body += L">\n";
                i++;
                continue;
            }
        }

        // table
        if (line.find(L'|') != std::wstring::npos && i + 1 < lines.size() && IsTableSeparator(lines[i + 1]))
        {
            std::vector<std::wstring> header = SplitRow(line);
            std::vector<std::wstring> sep = SplitRow(lines[i + 1]);
            std::vector<std::wstring> align(header.size());
            for (size_t c = 0; c < header.size() && c < sep.size(); c++)
            {
                std::wstring s = sep[c];
                bool left = !s.empty() && s[0] == L':';
                bool right = !s.empty() && s[s.size() - 1] == L':';
                align[c] = (left && right) ? L"center" : (right ? L"right" : (left ? L"left" : L""));
            }
            body += L"<table>\n<thead><tr>";
            for (size_t c = 0; c < header.size(); c++)
            {
                body += L"<th";
                if (!align[c].empty()) body += L" style=\"text-align:" + align[c] + L"\"";
                body += L">" + RenderInline(header[c], sourceDir) + L"</th>";
            }
            body += L"</tr></thead>\n<tbody>\n";
            i += 2;
            while (i < lines.size() && lines[i].find(L'|') != std::wstring::npos && !IsBlank(lines[i]))
            {
                std::vector<std::wstring> row = SplitRow(lines[i]);
                body += L"<tr>";
                for (size_t c = 0; c < header.size(); c++)
                {
                    body += L"<td";
                    if (c < align.size() && !align[c].empty()) body += L" style=\"text-align:" + align[c] + L"\"";
                    body += L">";
                    if (c < row.size()) body += RenderInline(row[c], sourceDir);
                    body += L"</td>";
                }
                body += L"</tr>\n";
                i++;
            }
            body += L"</tbody></table>\n";
            continue;
        }

        // blockquote (collect consecutive > lines)
        if (line[0] == L'>')
        {
            std::wstring quoted;
            while (i < lines.size() && !lines[i].empty() && lines[i][0] == L'>')
            {
                std::wstring l = lines[i].substr(1);
                if (!l.empty() && l[0] == L' ') l.erase(0, 1);
                quoted += l;
                quoted += L'\n';
                i++;
            }
            // recursive: render inner block content
            std::wstring inner = MarkdownToHtmlDocument(quoted, sourceDir);
            // strip the <style>/<body> wrapper: take everything inside <body>
            size_t b = inner.find(L"<body>");
            size_t e = inner.rfind(L"</body>");
            if (b != std::wstring::npos && e != std::wstring::npos && e > b)
                inner = inner.substr(b + 6, e - b - 6);
            body += L"<blockquote>\n" + inner + L"</blockquote>\n";
            continue;
        }

        // lists
        {
            std::wstring t = Trim(line);
            bool ul = (StartsWith(t, L"- ") || StartsWith(t, L"* ") || StartsWith(t, L"+ "));
            bool ol = false;
            size_t digits = 0;
            while (digits < t.size() && iswdigit(t[digits])) digits++;
            if (digits > 0 && digits + 1 < t.size() &&
                (t[digits] == L'.' || t[digits] == L')') && t[digits + 1] == L' ')
                ol = true;

            if (ul || ol)
            {
                std::wstring listTag = ol ? L"ol" : L"ul";
                body += L"<" + listTag + L">\n";
                while (i < lines.size())
                {
                    std::wstring item = Trim(lines[i]);
                    bool isItem = false;
                    std::wstring content;
                    if (StartsWith(item, L"- ") || StartsWith(item, L"* ") || StartsWith(item, L"+ "))
                    {
                        if (ol) break;
                        isItem = true;
                        content = item.substr(2);
                    }
                    else
                    {
                        size_t d = 0;
                        while (d < item.size() && iswdigit(item[d])) d++;
                        if (d > 0 && d + 1 < item.size() && (item[d] == L'.' || item[d] == L')') && item[d + 1] == L' ')
                        {
                            if (!ol) break;
                            isItem = true;
                            content = item.substr(d + 2);
                        }
                    }
                    if (!isItem) break;

                    // task list?
                    std::wstring cb;
                    std::wstring c = Trim(content);
                    if (StartsWith(c, L"[ ] "))      { cb = L"<input type=\"checkbox\" disabled> "; content = c.substr(4); }
                    else if (StartsWith(ToLowerAscii(c), L"[x] ")) { cb = L"<input type=\"checkbox\" checked disabled> "; content = c.substr(4); }

                    body += L"<li>" + cb + RenderInline(content, sourceDir) + L"</li>\n";
                    i++;
                }
                body += L"</" + listTag + L">\n";
                continue;
            }
        }

        // paragraph (gather until blank / block start)
        {
            std::wstring para;
            while (i < lines.size() && !IsBlank(lines[i]))
            {
                std::wstring t = Trim(lines[i]);
                if (StartsWith(t, L"```") || t[0] == L'#' || t[0] == L'>') break;
                if (StartsWith(t, L"- ") || StartsWith(t, L"* ") || StartsWith(t, L"+ ")) break;
                if (para.size() >= 2 && para.compare(para.size() - 2, 2, L"  ") == 0)
                {
                    para.erase(para.size() - 2);
                    para += L"<br>";
                }
                else if (!para.empty()) para += L' ';
                para += t;
                i++;
                // table lookahead
                if (i < lines.size() && lines[i].find(L'|') != std::wstring::npos && i + 1 <= lines.size() && IsTableSeparator(lines[i + 1 < lines.size() ? i + 1 : i]))
                    break;
            }
            if (!para.empty()) body += L"<p>" + RenderInline(para, sourceDir) + L"</p>\n";
        }
    }

    // ---------------------------------------------------------------- shell
    std::wstring html;
    html += L"<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n";
    html += L"<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">\n";
    html += L"<style>\n";
    html +=
        L":root{color-scheme:light dark}\n"
        L"body{margin:0;padding:28px 34px;font:15px/1.75 \"Segoe UI\",\"Microsoft YaHei UI\",\"Microsoft YaHei\",sans-serif;"
        L"color:#1f2328;background:#ffffff;word-wrap:break-word}\n"
        L"h1,h2,h3,h4,h5,h6{margin:28px 0 14px;font-weight:600;line-height:1.3}\n"
        L"h1{font-size:2em;border-bottom:1px solid #e3e6ea;padding-bottom:.3em}\n"
        L"h2{font-size:1.55em;border-bottom:1px solid #eceff2;padding-bottom:.25em}\n"
        L"h3{font-size:1.28em}h4{font-size:1.1em}h5{font-size:1em}h6{font-size:.92em;color:#656d76}\n"
        L"p{margin:0 0 16px}\n"
        L"a{color:#0b64c8;text-decoration:none}a:hover{text-decoration:underline}\n"
        L"code{font-family:Consolas,\"Cascadia Mono\",\"Courier New\",monospace;font-size:.92em;"
        L"background:#f2f4f7;border:1px solid #e4e8ec;border-radius:5px;padding:.12em .38em}\n"
        L"pre{background:#f6f8fa;border:1px solid #e4e8ec;border-radius:8px;padding:14px 16px;overflow:auto}\n"
        L"pre code{background:none;border:0;padding:0;font-size:.9em;line-height:1.55}\n"
        L"blockquote{margin:0 0 16px;padding:2px 18px;border-left:4px solid #d6dbe1;color:#57606a;background:#fafbfc}\n"
        L"ul,ol{margin:0 0 16px;padding-left:2em}li{margin:.28em 0}\n"
        L"table{border-collapse:collapse;margin:0 0 18px;display:block;overflow:auto;max-width:100%}\n"
        L"th,td{border:1px solid #dfe3e8;padding:7px 13px}th{background:#f6f8fa;font-weight:600}\n"
        L"hr{height:1px;border:0;background:#e3e6ea;margin:26px 0}\n"
        L"img{max-width:100%;height:auto}\n"
        L"input[type=checkbox]{margin-right:.4em}\n"
        L"@media (prefers-color-scheme:dark){"
        L"body{background:#1e1f22;color:#e6e6e6}"
        L"h1,h2{border-color:#3a3d42}code{background:#2b2d31;border-color:#3a3d42}"
        L"pre{background:#26282c;border-color:#3a3d42}blockquote{background:#26282c;border-color:#4a4d52;color:#b8bcc2}"
        L"th{background:#26282c}th,td{border-color:#3a3d42}hr{background:#3a3d42}a{color:#6cb6ff}"
        L"}\n";
    html += L"</style>\n</head>\n<body>\n";
    html += body;
    html += L"\n</body>\n</html>\n";
    return html;
}
