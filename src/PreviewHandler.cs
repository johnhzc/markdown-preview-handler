using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace MarkdownPreview
{
    /// <summary>
    /// Windows Explorer 预览处理器 COM 组件。
    /// 由 prevhost.exe 代加载，在预览窗格中渲染 Markdown 文档与图片文件。
    /// CLSID：{3511EA64-B51A-4298-B6C3-6AB81F179E1C}
    ///
    /// 注意：COM 经 mscoree shim 加载托管类时对象可能运行在 MTA 线程，
    /// 而 WebBrowser（IE/ActiveX 内核）要求 STA。所有 UI 均委托给
    /// PreviewHost 内部的 STA 线程构建并运行消息泵。
    /// </summary>
    [ComVisible(true)]
    [Guid("3511EA64-B51A-4298-B6C3-6AB81F179E1C")]
    [ClassInterface(ClassInterfaceType.None)]
    public class PreviewHandler : IPreviewHandler, IInitializeWithFile, IInitializeWithStream, IObjectWithSite, IOleWindow
    {
        public const string ClsidString = "{3511EA64-B51A-4298-B6C3-6AB81F179E1C}";
        public const string HandlerName = "Markdown & Image Preview Handler";

        private string _filePath;      // IInitializeWithFile 路径
        private byte[] _streamData;    // IInitializeWithStream 数据
        private RECT _bounds;          // 预览区域矩形
        private PreviewHost _host;     // STA 线程上的 UI 宿主
        private object _site;

        /// <summary>
        /// 静态构造：只要 shell（或任何宿主）真的加载了本模块，就会留下这一行。
        /// 它是“处理器到底有没有被装载”的最终判据 —— 连它都没有，
        /// 说明问题百分之百在注册/激活侧，与渲染代码无关。
        /// </summary>
        static PreviewHandler()
        {
            PreviewLog.Write("=== MODULE LOADED (static ctor) ===");
            PreviewLog.Write("    log path = " + PreviewLog.LogPath);
        }

        public PreviewHandler()
        {
            LogInfo("PreviewHandler instance created");
        }

        // ---------------- IInitializeWithFile ----------------

        void IInitializeWithFile.Initialize(string pszFilePath, uint grfMode)
        {
            LogInfo("IInitializeWithFile.Initialize(" + pszFilePath + ", " + grfMode + ")");
            try
            {
                if (string.IsNullOrEmpty(pszFilePath))
                {
                    throw Marshal.GetExceptionForHR(unchecked((int)0x80070057)); // E_INVALIDARG
                }
                _filePath = pszFilePath;
                _streamData = null;
            }
            catch (Exception ex)
            {
                PreviewLog.WriteError("IInitializeWithFile.FAILED", ex);
                throw;
            }
        }

        // ---------------- IInitializeWithStream ----------------

        void IInitializeWithStream.Initialize(IStream pstm, uint grfMode)
        {
            LogInfo("IInitializeWithStream.Initialize(stream=" + (pstm == null ? "null" : "obj") + ", " + grfMode + ")");
            try
            {
                if (pstm == null)
                {
                    throw Marshal.GetExceptionForHR(unchecked((int)0x80070057));
                }
                _streamData = ReadStream(pstm);
                _filePath = null;
                LogInfo("IInitializeWithStream OK: " + (_streamData == null ? 0 : _streamData.Length) + " bytes");
            }
            catch (Exception ex)
            {
                PreviewLog.WriteError("IInitializeWithStream.FAILED", ex);
                throw;
            }
        }

        private static byte[] ReadStream(IStream stream)
        {
            System.Runtime.InteropServices.ComTypes.STATSTG stat;
            stream.Stat(out stat, 1); // STATFLAG_NONAME
            long size = stat.cbSize;
            int capacity = (size > 0 && size <= int.MaxValue) ? (int)size : 4096;
            using (MemoryStream ms = new MemoryStream(capacity))
            {
                byte[] buffer = new byte[8192];
                IntPtr readPtr = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    while (true)
                    {
                        stream.Read(buffer, buffer.Length, readPtr);
                        int read = Marshal.ReadInt32(readPtr);
                        if (read <= 0) break;
                        ms.Write(buffer, 0, read);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(readPtr);
                }
                return ms.ToArray();
            }
        }

        // ---------------- IPreviewHandler ----------------

        void IPreviewHandler.SetWindow(IntPtr hwnd, ref RECT rect)
        {
            LogInfo("IPreviewHandler.SetWindow hwnd=" + hwnd + " rect=" +
                    rect.Left + "," + rect.Top + " " + rect.Right + "x" + rect.Bottom);
            try
            {
                _bounds = rect;
                EnsureHost();
                _host.Attach(hwnd, rect);
                LogInfo("IPreviewHandler.SetWindow OK");
            }
            catch (Exception ex)
            {
                PreviewLog.WriteError("IPreviewHandler.SetWindow FAILED", ex);
                throw;
            }
        }

        void IPreviewHandler.SetRect(ref RECT rect)
        {
            LogInfo("IPreviewHandler.SetRect " + rect.Left + "," + rect.Top + " " + rect.Right + "x" + rect.Bottom);
            try
            {
                _bounds = rect;
                if (_host != null) { _host.SetBounds(rect); }
            }
            catch (Exception ex)
            {
                PreviewLog.WriteError("IPreviewHandler.SetRect FAILED", ex);
                throw;
            }
        }

        void IPreviewHandler.DoPreview()
        {
            LogInfo("IPreviewHandler.DoPreview file=" + (_filePath ?? "(stream)"));
            try
            {
                EnsureHost();
                _host.BuildChild();
                Exception buildError = _host.LastError;
                if (buildError != null)
                {
                    PreviewLog.WriteError("BuildChild reported error", buildError);
                }
                LogInfo("IPreviewHandler.DoPreview OK");
            }
            catch (Exception ex)
            {
                PreviewLog.WriteError("IPreviewHandler.DoPreview FAILED", ex);
                throw;
            }
        }

        void IPreviewHandler.Unload()
        {
            LogInfo("Unload");
            DestroyHost();
        }

        void IPreviewHandler.SetFocus()
        {
            if (_host != null) { _host.Focus(); }
        }

        void IPreviewHandler.QueryFocus(out IntPtr phwnd)
        {
            IntPtr h = _host != null ? _host.GetFocusHandle() : IntPtr.Zero;
            if (h == IntPtr.Zero)
            {
                phwnd = IntPtr.Zero;
                Marshal.ThrowExceptionForHR(unchecked((int)0x80004005)); // E_FAIL
                return;
            }
            phwnd = h;
        }

        [PreserveSig]
        int IPreviewHandler.TranslateAccelerator(ref MSG pmsg)
        {
            return 1; // S_FALSE：快捷键交还宿主处理
        }

        // ---------------- IOleWindow ----------------

        void IOleWindow.GetWindow(out IntPtr phwnd)
        {
            IntPtr h = _host != null ? _host.GetRootHandle() : IntPtr.Zero;
            phwnd = h;
            LogInfo("IOleWindow.GetWindow -> " + h);
        }

        void IOleWindow.ContextSensitiveHelp(bool fEnterMode)
        {
            // 预览场景不需要上下文相关帮助
        }

        // ---------------- IObjectWithSite ----------------

        void IObjectWithSite.SetSite(object pUnkSite)
        {
            _site = pUnkSite;
            LogInfo("SetSite: " + (pUnkSite == null ? "(null)" : "site object"));
        }

        void IObjectWithSite.GetSite(ref Guid riid, out object ppvSite)
        {
            if (_site == null)
            {
                ppvSite = null;
                Marshal.ThrowExceptionForHR(unchecked((int)0x80004005)); // E_FAIL
                return;
            }
            IntPtr pUnk = Marshal.GetIUnknownForObject(_site);
            try
            {
                IntPtr pOut;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(pUnk, ref riid, out pOut));
                ppvSite = Marshal.GetObjectForIUnknown(pOut);
                Marshal.Release(pOut);
            }
            finally
            {
                Marshal.Release(pUnk);
            }
        }

        // ---------------- 渲染逻辑 ----------------

        /// <summary>诊断日志：记录宿主线程的单元状态，便于定位加载问题。</summary>
        private static void LogInfo(string message)
        {
            PreviewLog.Write(message);
        }

        private void EnsureHost()
        {
            if (_host == null || !_host.IsAlive)
            {
                LogInfo("EnsureHost: creating STA host");
                try
                {
                    _host = new PreviewHost(CreatePreviewControlCore);
                    LogInfo("EnsureHost: host created, alive=" + _host.IsAlive);
                }
                catch (Exception ex)
                {
                    PreviewLog.WriteError("EnsureHost FAILED", ex);
                    throw;
                }
            }
        }

        private void DestroyHost()
        {
            if (_host != null)
            {
                try { _host.Dispose(); }
                catch { }
                _host = null;
            }
        }

        /// <summary>在 STA 线程上构造预览控件。</summary>
        private Control CreatePreviewControlCore()
        {
            if (_filePath == null && _streamData == null)
            {
                return CreateErrorControl("没有可预览的文件数据。");
            }

            string ext = _filePath != null
                ? Path.GetExtension(_filePath).ToLowerInvariant()
                : null;

            byte[] data = null;
            if (_filePath != null)
            {
                try
                {
                    data = File.ReadAllBytes(_filePath);
                }
                catch (Exception ex)
                {
                    LogInfo("READ FAILED: " + ex.GetType().Name + ": " + ex.Message);
                    return CreateErrorControl("无法读取文件：" + ex.Message);
                }
            }
            else
            {
                data = _streamData;
            }

            if (ext != null && FileTypes.IsImageExtension(ext))
            {
                return new ImagePreviewControl(data);
            }

            if (ext != null && FileTypes.IsMarkdownExtension(ext))
            {
                return new MarkdownPreviewControl(FileTypes.ReadTextSmart(data), _filePath, false);
            }

            if (_filePath == null)
            {
                // 宿主以流初始化时没有扩展名信息，按内容嗅探
                if (FileTypes.IsImageBytes(data))
                {
                    return new ImagePreviewControl(data);
                }
                string text = FileTypes.ReadTextSmart(data);
                bool looksMarkdown = FileTypes.LooksLikeMarkdown(text);
                return new MarkdownPreviewControl(text, null, !looksMarkdown);
            }

            return CreateErrorControl("不支持的文件类型：" + ext);
        }

        internal static Control CreateErrorControl(string message)
        {
            Label label = new Label();
            label.Text = message;
            label.Dock = DockStyle.Fill;
            label.BackColor = System.Drawing.Color.FromArgb(253, 246, 227);
            label.ForeColor = System.Drawing.Color.FromArgb(130, 80, 30);
            label.Padding = new Padding(16);
            label.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            return label;
        }
    }
}
