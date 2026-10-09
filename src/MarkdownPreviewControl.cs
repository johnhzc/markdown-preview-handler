using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;

namespace MarkdownPreview
{
    /// <summary>
    /// Markdown 预览控件：将 Markdown 转换为 HTML 后由系统内置 IE 引擎（Trident）渲染。
    /// 链接点击被拦截并转交系统默认程序打开，避免在预览窗格内跳转。
    /// </summary>
    public class MarkdownPreviewControl : WebBrowser
    {
        private string _pendingHtml;

        public MarkdownPreviewControl(string markdownText, string sourceFilePath, bool plainTextMode)
        {
            ScriptErrorsSuppressed = true;
            IsWebBrowserContextMenuEnabled = false;
            WebBrowserShortcutsEnabled = true;
            AllowWebBrowserDrop = false;
            Navigating += OnNavigating;

            _pendingHtml = MarkdownConverter.ToHtml(markdownText, sourceFilePath, plainTextMode);
            if (IsHandleCreated)
            {
                DocumentText = _pendingHtml;
                _pendingHtml = null;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_pendingHtml != null)
            {
                DocumentText = _pendingHtml;
                _pendingHtml = null;
            }
        }

        private void OnNavigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            // about:blank 是 DocumentText 的载体页面，放行（含页内锚点）
            if (e.Url != null && e.Url.Scheme == "about")
            {
                return;
            }
            // 禁止在预览窗格内导航，转交系统默认程序打开
            e.Cancel = true;
            try
            {
                if (e.Url != null)
                {
                    Process.Start(e.Url.ToString());
                }
            }
            catch
            {
                // 沙箱环境可能限制进程启动，静默忽略
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Navigating -= OnNavigating;
            }
            base.Dispose(disposing);
        }
    }
}
