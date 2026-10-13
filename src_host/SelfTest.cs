using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MarkdownPreview
{
    /// <summary>
    /// 进程外自测：以“COM 客户端”的身份激活本处理器
    /// （CoCreateInstance + CLSCTX_LOCAL_SERVER，与资源管理器完全相同的路径），
    /// 再走一遍 Initialize → SetWindow → DoPreview，并把结果截图。
    ///
    /// 这样可以在不依赖人眼、不依赖资源管理器的情况下验证：
    ///   1. LocalServer32 注册能否被激活
    ///   2. 托管对象能否跨进程编组
    ///   3. 渲染是否真的画出来了
    /// </summary>
    internal static class SelfTest
    {
        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pv, uint flags);

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(
            ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        [DllImport("ole32.dll")]
        private static extern int CoGetClassObject(
            ref Guid rclsid, uint dwClsContext, IntPtr pServerInfo, ref Guid riid, out IntPtr ppv);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr param);

        private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

        // 注意：不能用 readonly static 字段直接作 ref/out 实参
        private static Guid CLSID =
            new Guid("3511EA64-B51A-4298-B6C3-6AB81F179E1C");
        private static Guid IID_IUnknown =
            new Guid("00000000-0000-0000-C000-000000000046");
        private static Guid IID_IClassFactory =
            new Guid("00000001-0000-0000-C000-000000000046");

        internal static int Run(string file)
        {
            Console.WriteLine("=== 进程外自测 ===");
            Console.WriteLine("file: " + file);
            Console.WriteLine("exists: " + File.Exists(file));

            int hr = CoInitializeEx(IntPtr.Zero, 0x2);
            Console.WriteLine("CoInitializeEx hr=0x" + hr.ToString("X8"));

            // 1. 类对象（先探活，不启动实例）
            IntPtr cf;
            hr = CoGetClassObject(ref CLSID, 0x4, IntPtr.Zero, ref IID_IClassFactory, out cf);
            Console.WriteLine("CoGetClassObject(CLSCTX_LOCAL_SERVER, IClassFactory) hr=0x" + hr.ToString("X8"));
            if (hr == 0) Marshal.Release(cf);
            if (hr < 0) { Console.WriteLine("=> 无法取得类对象，LocalServer32 注册或服务器有问题"); }

            // 2. 激活对象（和 shell 一样）
            Guid iidUnk = IID_IUnknown;
            IntPtr unk;
            hr = CoCreateInstance(ref CLSID, IntPtr.Zero, 0x4, ref iidUnk, out unk);
            Console.WriteLine("CoCreateInstance(CLSCTX_LOCAL_SERVER) hr=0x" + hr.ToString("X8"));
            if (hr < 0) { Console.WriteLine("=> 进程外激活失败"); return 1; }

            // 3. 直接强转到 [ComImport] 接口 —— 这才是跨进程编组的真实路径。
            //    注意：不能用反射 GetInterface("名字")，透明代理上看不到托管接口类型，
            //    那样连 QI 都不会发出去（会误判成“接口不存在”）。
            object activated = Marshal.GetObjectForIUnknown(unk);
            Console.WriteLine("activated object: " + activated.GetType().FullName);

            IInitializeWithFile initFile = null;
            IInitializeWithStream initStream = null;
            IPreviewHandler handler = null;
            try { initFile = (IInitializeWithFile)activated; Console.WriteLine("QI IInitializeWithFile : OK"); }
            catch (Exception ex) { Console.WriteLine("QI IInitializeWithFile : FAILED " + Root(ex)); }
            try { initStream = (IInitializeWithStream)activated; Console.WriteLine("QI IInitializeWithStream : OK"); }
            catch (Exception ex) { Console.WriteLine("QI IInitializeWithStream : FAILED " + Root(ex)); }
            try { handler = (IPreviewHandler)activated; Console.WriteLine("QI IPreviewHandler : OK"); }
            catch (Exception ex) { Console.WriteLine("QI IPreviewHandler : FAILED " + Root(ex)); }
            try { IOleWindow ow = (IOleWindow)activated; Console.WriteLine("QI IOleWindow : OK"); }
            catch (Exception ex) { Console.WriteLine("QI IOleWindow : FAILED " + Root(ex)); }
            try { IObjectWithSite ows = (IObjectWithSite)activated; Console.WriteLine("QI IObjectWithSite : OK"); }
            catch (Exception ex) { Console.WriteLine("QI IObjectWithSite : FAILED " + Root(ex)); }

            // 直接 QueryInterface 探活（不经过 RCW 的强转逻辑），区分
            // “服务器拒绝” 与 “客户端没有编组信息” 这两种完全不同的原因
            ProbeQi(unk, "IInitializeWithFile   ", "{B7D14566-0509-4CCE-A71F-0A554233BD9B}");
            ProbeQi(unk, "IInitializeWithStream ", "{B824B49D-22AC-4161-AC8A-9916E8FA3F7F}");
            ProbeQi(unk, "IPreviewHandler       ", "{8895B1C6-B7F3-4824-B022-4FDD1C0FE5E2}");
            ProbeQi(unk, "IObjectWithSite       ", "{FC4801A3-2BA9-11CF-A229-00AA003D7352}");
            ProbeQi(unk, "IOleWindow            ", "{00000114-0000-0000-C000-000000000046}");

            Marshal.Release(unk);

            // 4. 宿主窗口 + 完整预览流程
            Application.EnableVisualStyles();
            Form host = new Form();
            host.Width = 660; host.Height = 500;
            host.Text = "OPP SelfTest Host";
            host.StartPosition = FormStartPosition.Manual;
            host.Location = new Point(60, 60);
            host.Show();
            IntPtr hwnd = host.Handle;
            Console.WriteLine("host hwnd: " + hwnd);

            if (initFile != null)
            {
                try { initFile.Initialize(file, 0); Console.WriteLine("IInitializeWithFile.Initialize OK"); }
                catch (Exception ex) { Console.WriteLine("IInitializeWithFile.Initialize FAILED: " + Root(ex)); }
            }

            if (handler != null)
            {
                try
                {
                    RECT r = MakeRect(host.ClientSize.Width, host.ClientSize.Height);
                    handler.SetWindow(hwnd, ref r);
                    Console.WriteLine("IPreviewHandler.SetWindow OK");
                }
                catch (Exception ex) { Console.WriteLine("SetWindow FAILED: " + Root(ex)); }

                try { handler.DoPreview(); Console.WriteLine("IPreviewHandler.DoPreview OK"); }
                catch (Exception ex) { Console.WriteLine("DoPreview FAILED: " + Root(ex)); }
            }

            // 5. 泵消息让它渲染
            Console.WriteLine("pumping messages 6s ...");
            DateTime deadline = DateTime.Now.AddSeconds(6);
            while (DateTime.Now < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(50);
            }

            int children = CountChildren(hwnd);
            Console.WriteLine("host child windows: " + children);

            // 6. 截图
            try
            {
                using (Bitmap bmp = new Bitmap(host.Width, host.Height))
                {
                    host.Refresh();
                    host.DrawToBitmap(bmp, new Rectangle(0, 0, host.Width, host.Height));
                    string outPath = Path.Combine(
                        Path.GetDirectoryName(file) ?? ".", "opp_selftest.png");
                    bmp.Save(outPath, ImageFormat.Png);
                    Console.WriteLine("screenshot -> " + outPath);
                }
            }
            catch (Exception ex) { Console.WriteLine("screenshot failed: " + ex.Message); }

            Console.WriteLine("=== 自测结束（窗口保持 8 秒便于观察）===");
            DateTime hold = DateTime.Now.AddSeconds(8);
            while (DateTime.Now < hold) { Application.DoEvents(); Thread.Sleep(50); }
            return 0;
        }

        private static RECT MakeRect(int w, int h)
        {
            RECT r = new RECT();
            r.Left = 0; r.Top = 0; r.Right = w; r.Bottom = h;
            return r;
        }

        private static void ProbeQi(IntPtr pUnk, string label, string iidStr)
        {
            try
            {
                Guid iid = new Guid(iidStr);
                IntPtr p;
                int hr = Marshal.QueryInterface(pUnk, ref iid, out p);
                if (hr == 0)
                {
                    Console.WriteLine("  raw QI " + label + " OK");
                    Marshal.Release(p);
                }
                else
                {
                    Console.WriteLine("  raw QI " + label + " FAILED 0x" + hr.ToString("X8"));
                }
            }
            catch (Exception ex) { Console.WriteLine("  raw QI " + label + " threw " + ex.Message); }
        }

        private static string Root(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static int CountChildren(IntPtr parent)
        {
            int n = 0;
            EnumChildWindows(parent, delegate(IntPtr h, IntPtr p) { n++; return true; }, IntPtr.Zero);
            return n;
        }
    }
}
