using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

/// <summary>
/// Loads the handler assembly directly from bin\ (bypassing the stale GAC
/// copy) and drives the preview lifecycle from an MTA thread - reproducing
/// the thread model that made the old WebBrowser-based control fail.
/// </summary>
static class LocalHarness
{
    [STAThread]
    static void Main(string[] args)
    {
        string dll = args.Length > 0 ? args[0] : @"D:\BuddyData\Tasks\markdown-preview-handler\bin\MarkdownPreviewHandler.dll";
        string file = args.Length > 1 ? args[1] : @"D:\BuddyData\Tasks\markdown-preview-handler\test\sample.md";
        string outPng = args.Length > 2 ? args[2] : @"D:\BuddyData\Tasks\markdown-preview-handler\test\local_harness.png";

        Console.WriteLine("=== LocalHarness ==");
        Console.WriteLine("dll : " + dll);
        Console.WriteLine("file: " + file);

        Assembly asm = Assembly.LoadFrom(dll);
        Console.WriteLine("loaded: " + asm.Location);

        Type handlerType = asm.GetType("MarkdownPreview.PreviewHandler", true);
        object handler = Activator.CreateInstance(handlerType);
        Console.WriteLine("[1] instance OK");

        // Initialize
        Type iif = handlerType.GetInterface("MarkdownPreview.IInitializeWithFile");
        iif.InvokeMember("Initialize", BindingFlags.InvokeMethod, null, handler, new object[] { file, 0u });
        Console.WriteLine("[2] Initialize OK");

        // Dedicated STA host (mirrors what the handler does internally)
        Form host = new Form();
        host.Width = 620; host.Height = 460;
        host.Text = "LocalHarness (STA)";
        IntPtr h = host.Handle;

        Type rectType = asm.GetType("MarkdownPreview.RECT");
        object rect = Activator.CreateInstance(rectType);
        rectType.GetField("Left").SetValue(rect, 0);
        rectType.GetField("Top").SetValue(rect, 0);
        rectType.GetField("Right").SetValue(rect, 620);
        rectType.GetField("Bottom").SetValue(rect, 460);

        Type iph = handlerType.GetInterface("MarkdownPreview.IPreviewHandler");
        iph.InvokeMember("SetWindow", BindingFlags.InvokeMethod, null, handler, new object[] { h, rect });
        Console.WriteLine("[3] SetWindow OK");
        iph.InvokeMember("DoPreview", BindingFlags.InvokeMethod, null, handler, null);
        Console.WriteLine("[4] DoPreview OK");

        // Pump messages so the STA thread builds and renders the control
        DateTime deadline = DateTime.Now.AddSeconds(6);
        while (DateTime.Now < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }

        Console.WriteLine("[5] host children: " + CountChildren(h));
        try
        {
            using (Bitmap bmp = new Bitmap(host.Width, host.Height))
            {
                host.DrawToBitmap(bmp, new Rectangle(0, 0, host.Width, host.Height));
                bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("[6] rendered -> " + outPng);
            }
        }
        catch (Exception ex) { Console.WriteLine("[6] render failed: " + ex.Message); }

        iph.InvokeMember("Unload", BindingFlags.InvokeMethod, null, handler, null);
        Console.WriteLine("=== done ===");
    }

    static int CountChildren(IntPtr parent)
    {
        int n = 0;
        EnumChildWindows(parent, delegate(IntPtr a, IntPtr b) { n++; return true; }, IntPtr.Zero);
        return n;
    }
    delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
}
