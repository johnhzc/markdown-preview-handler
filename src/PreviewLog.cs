using System;
using System.IO;
using System.Text;
using System.Threading;

namespace MarkdownPreview
{
    /// <summary>
    /// 处理器诊断日志。
    ///
    /// 写入 <c>%LOCALAPPDATA%\MarkdownPreviewHandler\handler.log</c>（不可写时
    /// 依次退回 %TEMP%、C:\ProgramData、程序集所在目录）。
    ///
    /// 排错判据：
    ///   * 出现 <c>ctor</c> / <c>SetWindow</c> / <c>DoPreview</c> —— shell 已激活处理器；
    ///   * 完全没有文件 —— shell 没有加载我们，或加载时在类型初始化阶段就失败了。
    /// </summary>
    internal static class PreviewLog
    {
        private const long MaxBytes = 512 * 1024;
        private const string DirName = "MarkdownPreviewHandler";

        private static readonly object Gate = new object();
        private static string _path;
        private static bool _resolved;

        // ---------------------------------------------------------------
        // 路径解析：任何一个环节抛异常都不能让日志整体失效
        // ---------------------------------------------------------------

        private static string ResolvePath()
        {
            string[] dirs = new string[4];
            try
            {
                dirs[0] = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DirName);
            }
            catch { }
            try { dirs[1] = Path.Combine(Path.GetTempPath(), DirName); } catch { }
            dirs[2] = @"C:\ProgramData\" + DirName;
            try
            {
                string asmDir = Path.GetDirectoryName(typeof(PreviewLog).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir)) dirs[3] = Path.Combine(asmDir, DirName);
            }
            catch { }

            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    if (!Path.IsPathRooted(dir)) continue;
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "handler.log");
                }
                catch { }
            }
            return null;
        }

        internal static string LogPath
        {
            get
            {
                if (!_resolved)
                {
                    lock (Gate)
                    {
                        if (!_resolved)
                        {
                            try { _path = ResolvePath(); } catch { _path = null; }
                            _resolved = true;
                        }
                    }
                }
                return _path;
            }
        }

        // ---------------------------------------------------------------
        // 写日志
        // ---------------------------------------------------------------

        internal static void Write(string message)
        {
            string line;
            try
            {
                Thread t = Thread.CurrentThread;
                int pid = -1;
                try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
                string ap = "?";
                try { ap = t.GetApartmentState().ToString(); } catch { }

                line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                       " | tid=" + t.ManagedThreadId +
                       " | " + ap +
                       " | pid=" + pid +
                       " | " + message + Environment.NewLine;
            }
            catch
            {
                line = "? | raw | " + message + Environment.NewLine;
            }

            try
            {
                string path = LogPath;
                if (string.IsNullOrEmpty(path)) return;   // 彻底无法写日志
                lock (Gate)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxBytes) File.Delete(path);
                    }
                    catch { }
                    File.AppendAllText(path, line);
                }
            }
            catch
            {
                // 日志失败绝不影响预览
            }
        }

        internal static void WriteError(string context, Exception ex)
        {
            Write(context + " | EXCEPTION " + Flatten(ex));
            try
            {
                if (ex != null && ex.StackTrace != null)
                {
                    Write(context + " | STACK " + ex.StackTrace.Replace(Environment.NewLine, " / "));
                }
            }
            catch { }
        }

        internal static string Flatten(Exception ex)
        {
            if (ex == null) return "(null)";
            StringBuilder sb = new StringBuilder();
            Exception e = ex;
            int depth = 0;
            while (e != null && depth < 10)
            {
                if (depth > 0) sb.Append(" -> ");
                sb.Append(e.GetType().FullName).Append(": ").Append(e.Message);
                e = e.InnerException;
                depth++;
            }
            return sb.ToString();
        }
    }
}
