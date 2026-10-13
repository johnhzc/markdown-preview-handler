using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MarkdownPreview
{
    /// <summary>
    /// 进程外 COM 服务器入口。
    ///
    /// 为什么需要它：
    ///   把处理器注册成 InprocServer32 = mscoree.dll 交给 prevhost.dll 代进程时，
    ///   在本机 Windows 11 25H2 上 CLR 会在代进程内部初始化失败（clr.dll 已加载但
    ///   从未执行托管代码），预览窗格报“处理器中出现错误”。
    ///
    ///   改成 LocalServer32 指向本 EXE 后，资源管理器用
    ///   CoCreateInstance(CLSCTX_LOCAL_SERVER) 直接启动本进程，
    ///   CLR 在普通中等完整性进程里初始化，不再经过代进程。
    ///
    /// 用法：
    ///   MarkdownPreviewHost.exe -Embedding        （COM 激活时由系统传入，可忽略）
    ///   MarkdownPreviewHost.exe -SelfTest <file>  （本地自测：激活 + 走完整预览流程 + 截图）
    /// </summary>
    internal static class Program
    {
        // ---------------- Win32 ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll")]
        private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        // ---------------- 常量 ----------------

        private const uint COINIT_APARTMENTTHREADED = 0x2;
        private const uint CLSCTX_LOCAL_SERVER = 0x4;
        private const uint REGCLS_MULTIPLEUSE = 1;
        private static readonly Guid CLSID_PreviewHandler =
            new Guid("3511EA64-B51A-4298-B6C3-6AB81F179E1C");
        private static readonly Guid IID_IUnknown =
            new Guid("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IClassFactory =
            new Guid("00000001-0000-0000-C000-000000000046");

        private static int _lockCount;

        // ---------------- 入口 ----------------

        [STAThread]
        private static int Main(string[] args)
        {
            PreviewLog.Write("=== MarkdownPreviewHost.exe started: " +
                string.Join(" ", args) + " ===");

            if (args.Length >= 2 && args[0] == "-SelfTest")
            {
                return SelfTest.Run(args[1]);
            }

            return RunComServer();
        }

        private static int RunComServer()
        {
            int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            PreviewLog.Write("CoInitializeEx(STA) hr=0x" + hr.ToString("X8"));
            if (hr < 0 && hr != unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE
            {
                PreviewLog.Write("CoInitializeEx failed, exiting");
                return 1;
            }

            IntPtr factory = ClassFactory.Create();
            if (factory == IntPtr.Zero)
            {
                PreviewLog.Write("class factory creation failed");
                CoUninitialize();
                return 1;
            }

            IntPtr cookie;
            hr = CoRegisterClassObject(
                ref _clsidField, factory,
                CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out cookie);

            PreviewLog.Write("CoRegisterClassObject hr=0x" + hr.ToString("X8"));
            if (hr < 0)
            {
                CoUninitialize();
                return 1;
            }

            // 消息循环：COM 的 RPC 调用需要线程在泵消息
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            CoRevokeClassObject(cookie);
            CoUninitialize();
            PreviewLog.Write("com server exiting");
            return 0;
        }

        // ref Guid 需要可写的托管内存；用静态字段保证地址稳定
        private static Guid _clsidField = CLSID_PreviewHandler;

        [DllImport("ole32.dll")]
        private static extern int CoRegisterClassObject(
            ref Guid rclsid, IntPtr pUnk, uint dwClsContext, uint flags, out IntPtr lpdwRegister);

        [DllImport("ole32.dll")]
        private static extern int CoRevokeClassObject(IntPtr dwRegister);

        internal static int AddRefLock()
        {
            return Interlocked.Increment(ref _lockCount);
        }

        internal static int ReleaseLock()
        {
            return Interlocked.Decrement(ref _lockCount);
        }

        // ==============================================================
        // IClassFactory：手工构造 vtable
        //
        // 不用 [ComImport] 接口让 CLR 代劳，是因为要精确控制 vtable 布局与
        // 调用约定，避免 CLR 的接口编组差异导致 shell 侧激活失败。
        //   IUnknown      : QueryInterface(0), AddRef(1), Release(2)
        //   IClassFactory : CreateInstance(3), LockServer(4)
        // ==============================================================

        internal static class ClassFactory
        {
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int QueryInterfaceDelegate(IntPtr thisPtr, ref Guid riid, out IntPtr ppv);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int AddRefDelegate(IntPtr thisPtr);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int ReleaseDelegate(IntPtr thisPtr);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int CreateInstanceDelegate(
                IntPtr thisPtr, IntPtr pUnkOuter, ref Guid riid, out IntPtr ppv);

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int LockServerDelegate(IntPtr thisPtr, int fLock);

            // 必须保持委托存活，否则函数指针会失效
            private static QueryInterfaceDelegate _qi;
            private static AddRefDelegate _addRef;
            private static ReleaseDelegate _release;
            private static CreateInstanceDelegate _create;
            private static LockServerDelegate _lockServer;
            private static IntPtr _vtable;
            private static IntPtr _factory;
            private static int _refCount = 1;

            internal static IntPtr Create()
            {
                _qi = new QueryInterfaceDelegate(QueryInterface);
                _addRef = new AddRefDelegate(AddRef);
                _release = new ReleaseDelegate(Release);
                _create = new CreateInstanceDelegate(CreateInstance);
                _lockServer = new LockServerDelegate(LockServer);

                _vtable = Marshal.AllocHGlobal(IntPtr.Size * 5);
                Marshal.WriteIntPtr(_vtable, 0, Marshal.GetFunctionPointerForDelegate(_qi));
                Marshal.WriteIntPtr(_vtable, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_addRef));
                Marshal.WriteIntPtr(_vtable, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(_release));
                Marshal.WriteIntPtr(_vtable, IntPtr.Size * 3, Marshal.GetFunctionPointerForDelegate(_create));
                Marshal.WriteIntPtr(_vtable, IntPtr.Size * 4, Marshal.GetFunctionPointerForDelegate(_lockServer));

                _factory = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_factory, 0, _vtable);
                return _factory;
            }

            private static int QueryInterface(IntPtr thisPtr, ref Guid riid, out IntPtr ppv)
            {
                ppv = IntPtr.Zero;
                if (riid == IID_IUnknown || riid == IID_IClassFactory)
                {
                    ppv = thisPtr;
                    AddRef(thisPtr);
                    return 0;
                }
                return unchecked((int)0x80004002); // E_NOINTERFACE
            }

            private static int AddRef(IntPtr thisPtr)
            {
                return Interlocked.Increment(ref _refCount);
            }

            private static int Release(IntPtr thisPtr)
            {
                return Interlocked.Decrement(ref _refCount);
            }

            /// <summary>
            /// 创建处理器实例。CLR 会把托管对象包装成支持跨进程编组的 CCW，
            /// 因此返回的指针可以被 shell 直接使用。
            /// </summary>
            private static int CreateInstance(IntPtr thisPtr, IntPtr pUnkOuter, ref Guid riid, out IntPtr ppv)
            {
                ppv = IntPtr.Zero;
                PreviewLog.Write("IClassFactory.CreateInstance riid=" + riid.ToString("B"));

                if (pUnkOuter != IntPtr.Zero)
                {
                    return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
                }

                try
                {
                    // 注册表里 RuntimeVersion/Assembly 已由 mscoree 不再参与，
                    // 这里直接在当前 AppDomain 里构造类型。
                    Type t = Type.GetType("MarkdownPreview.PreviewHandler, MarkdownPreviewHandler", true);
                    object instance = Activator.CreateInstance(t);
                    IntPtr pUnk = Marshal.GetIUnknownForObject(instance);
                    try
                    {
                        int hr = Marshal.QueryInterface(pUnk, ref riid, out ppv);
                        PreviewLog.Write("  QueryInterface(" + riid.ToString("B") + ") hr=0x" + hr.ToString("X8"));
                        if (hr < 0)
                        {
                            return hr;
                        }
                    }
                    finally
                    {
                        Marshal.Release(pUnk);
                    }
                    AddRefLock();
                    return 0;
                }
                catch (Exception ex)
                {
                    PreviewLog.WriteError("CreateInstance FAILED", ex);
                    return unchecked((int)0x80004005); // E_FAIL
                }
            }

            private static int LockServer(IntPtr thisPtr, int fLock)
            {
                if (fLock != 0) AddRefLock(); else ReleaseLock();
                return 0;
            }
        }
    }
}
