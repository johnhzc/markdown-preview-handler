using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace MarkdownPreview
{
    /// <summary>
    /// 预览控件宿主。所有 WinForms 控件（尤其是依赖 ActiveX 的 WebBrowser）
    /// 必须在 STA 线程上创建并运行消息泵。
    ///
    /// COM 经 mscoree shim 加载托管类时，对象通常运行在 MTA 线程上，
    /// 直接在其中 new WebBrowser() 会抛：
    ///   "当前线程不在单线程单元中，因此无法实例化 ActiveX 控件"
    /// 因此这里建立一个独立的 STA 线程承载 UI，并通过 Invoke 与其交互。
    /// </summary>
    internal sealed class PreviewHost : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private readonly object _gate = new object();

        private Control _root;          // STA 线程上创建的顶层容器窗格
        private Control _child;         // 实际预览控件
        private Func<Control> _factory; // 在被宿主线程上构造预览控件

        private IntPtr _parentHwnd;
        private RECT _bounds;
        private bool _doPreviewRequested;
        private Exception _lastError;
        private volatile bool _disposed;

        public Exception LastError { get { return _lastError; } }
        public bool IsAlive { get { return _thread != null && _thread.IsAlive; } }

        public PreviewHost(Func<Control> childFactory)
        {
            _factory = childFactory;
            _thread = new Thread(ThreadMain);
            _thread.IsBackground = true;
            _thread.SetApartmentState(ApartmentState.STA);  // 关键：STA
            _thread.Name = "PreviewHandlerUI";
            _thread.Start();
            _ready.Wait(5000);
        }

        private void ThreadMain()
        {
            try
            {
                PreviewLog.Write("UI thread started (STA), managed tid=" +
                    System.Threading.Thread.CurrentThread.ManagedThreadId);
                _root = new Panel();
                _root.Dock = DockStyle.Fill;
                _root.BackColor = Color.White;
                // 强制创建句柄，让后续 SetParent 可用
                IntPtr h = _root.Handle;
                _ready.Set();

                // 运行消息泵，直到 Dispose 关闭窗体
                Application.Run(new MessagePumpForm(_root));
                PreviewLog.Write("UI thread message pump exited");
            }
            catch (Exception ex)
            {
                _lastError = ex;
                PreviewLog.WriteError("UI THREAD FAILED", ex);
                _ready.Set();
            }
        }

        /// <summary>纯消息泵宿主窗体，不显示，仅承载控件树。</summary>
        private sealed class MessagePumpForm : Form
        {
            public MessagePumpForm(Control content)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-4000, -4000);
                Size = new Size(1, 1);
                content.Parent = this;
                Controls.Add(content);
            }

            protected override void SetVisibleCore(bool value)
            {
                base.SetVisibleCore(false); // 永不显示，只跑消息循环
            }
        }

        /// <summary>把宿主预览窗格的句柄与矩形交给 STA 线程处理。</summary>
        public void Attach(IntPtr parentHwnd, RECT bounds)
        {
            lock (_gate)
            {
                _parentHwnd = parentHwnd;
                _bounds = bounds;
            }
            RunOnUI(delegate
            {
                if (_doPreviewRequested) { BuildChild(); }
                if (_parentHwnd != IntPtr.Zero) { Reparent(); }
            });
        }

        public void SetBounds(RECT bounds)
        {
            lock (_gate) { _bounds = bounds; }
            RunOnUI(delegate { Position(); });
        }

        /// <summary>构造预览控件（在 STA 线程上执行）。</summary>
        public void BuildChild()
        {
            RunOnUI(delegate
            {
                _doPreviewRequested = true;
                if (_child != null) { try { _child.Dispose(); } catch { } _child = null; }
                try
                {
                    _child = _factory();
                    _child.Dock = DockStyle.Fill;
                    _root.Controls.Add(_child);
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    _lastError = ex;
                    PreviewLog.WriteError("BUILD FAILED", ex);
                    _child = PreviewHandler.CreateErrorControl("无法预览此文件：" + ex.Message);
                    _child.Dock = DockStyle.Fill;
                    _root.Controls.Add(_child);
                }
                Position();
            });
        }

        private void Reparent()
        {
            if (_root == null || _parentHwnd == IntPtr.Zero) return;
            NativeMethods.SetParent(_root.Handle, _parentHwnd);
            Position();
            NativeMethods.ShowWindow(_root.Handle, 5); // SW_SHOW
        }

        private void Position()
        {
            if (_root == null || !_root.IsHandleCreated) return;
            int w = _bounds.Right - _bounds.Left;
            int h = _bounds.Bottom - _bounds.Top;
            if (w > 0 && h > 0)
            {
                NativeMethods.SetWindowPos(_root.Handle, IntPtr.Zero,
                    _bounds.Left, _bounds.Top, w, h, 0x0004); // SWP_NOZORDER
            }
        }

        public void Focus()
        {
            RunOnUI(delegate { if (_child != null) _child.Focus(); });
        }

        public IntPtr GetFocusHandle()
        {
            IntPtr result = IntPtr.Zero;
            RunOnUI(delegate
            {
                if (_child != null && _child.IsHandleCreated) result = _child.Handle;
            });
            return result;
        }

        /// <summary>承载预览的顶层子窗口句柄（IOleWindow::GetWindow 用）。</summary>
        public IntPtr GetRootHandle()
        {
            try
            {
                if (_root != null && _root.IsHandleCreated) return _root.Handle;
            }
            catch { }
            return IntPtr.Zero;
        }

        private void RunOnUI(Action action)
        {
            if (_disposed || _root == null) return;
            try
            {
                if (_root.InvokeRequired) { _root.Invoke(action); }
                else { action(); }
            }
            catch (Exception ex)
            {
                _lastError = ex;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                RunOnUI(delegate
                {
                    if (_child != null) { try { _child.Dispose(); } catch { } _child = null; }
                    Application.ExitThread(); // 结束消息泵，释放 STA 线程
                });
            }
            catch { }
            _ready.Dispose();
        }

        private static string Flatten(Exception ex)
        {
            string s = ex.GetType().FullName + ": " + ex.Message;
            Exception e = ex.InnerException;
            int depth = 0;
            while (e != null && depth < 6)
            {
                s += " -> " + e.GetType().FullName + ": " + e.Message;
                e = e.InnerException;
                depth++;
            }
            return s;
        }
    }
}
