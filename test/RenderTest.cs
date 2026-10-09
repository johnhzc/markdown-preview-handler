using System;
using System.IO;
using MarkdownPreview;

/// <summary>
/// 渲染测试 harness：调用 MarkdownConverter 将 sample.md 转成 HTML，
/// 用于在浏览器中直观验证转换器输出。
/// </summary>
class RenderTest
{
    static int Main(string[] args)
    {
        string src = args.Length > 0 ? args[0] : "sample.md";
        string full = Path.GetFullPath(src);
        if (!File.Exists(full))
        {
            Console.Error.WriteLine("Input not found: " + full);
            return 1;
        }

        string html = MarkdownConverter.ToHtml(File.ReadAllText(full), full, false);
        string outPath = Path.Combine(Path.GetDirectoryName(full), "preview_render_test.html");
        File.WriteAllText(outPath, html, new System.Text.UTF8Encoding(true));
        Console.WriteLine("[OK] " + outPath);
        return 0;
    }
}
