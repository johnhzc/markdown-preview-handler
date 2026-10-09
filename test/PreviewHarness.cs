using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

/// <summary>
/// End-to-end harness: loads the handler assembly, instantiates the COM
/// class, and drives the real preview lifecycle (Initialize -> SetWindow ->
/// DoPreview) against a real file, capturing any exception and rendering the
/// resulting control to a PNG so failures are visible.
/// </summary>
static class PreviewHarness
{
    static int Main(string[] args)
    {
        string file = args.Length > 0 ? args[0] : @"D:\BuddyData\Tasks\markdown-preview-handler\test\sample.md";
        string outPng = args.Length > 1 ? args[1] : @"D:\BuddyData\Tasks\markdown-preview-handler\test\harness_output.png";

        Console.WriteLine("=== Preview harness ===");
        Console.WriteLine("file: " + file);
        Console.WriteLine("exists: " + File.Exists(file));
        Console.WriteLine("");

        Application.EnableVisualStyles();

        // Load via the CLSID path exactly like the shell does
        Guid clsid = new Guid("3511EA64-B51A-4298-B6C3-6AB81F179E1C");
        Type t = Type.GetTypeFromCLSID(clsid);
        object handler = null;
        try
        {
            handler = Activator.CreateInstance(t);
            Console.WriteLine("[1] CoCreateInstance: OK -> " + handler.GetType().FullName);
            Console.WriteLine("    assembly: " + handler.GetType().Assembly.Location);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[1] CoCreateInstance FAILED: " + Flatten(ex));
            return 1;
        }

        // IInitializeWithFile
        try
        {
            object initFile = handler;
            // late-bound invoke (avoids referencing the interface here)
            Type iif = handler.GetType().GetInterface("MarkdownPreview.IInitializeWithFile");
            if (iif == null)
            {
                Console.WriteLine("[2] IInitializeWithFile interface NOT FOUND on type");
            }
            else
            {
                iif.InvokeMember("Initialize", BindingFlags.InvokeMethod, null, initFile,
                    new object[] { file, 0u });
                Console.WriteLine("[2] IInitializeWithFile.Initialize: OK");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[2] Initialize FAILED: " + Flatten(ex));
        }

        // IPreviewHandler.SetWindow with a real host form
        Form host = new Form();
        host.Width = 600; host.Height = 450;
        host.Text = "Preview Harness Host";
        IntPtr hostHandle = host.Handle;
        Console.WriteLine("[3] host form handle: " + hostHandle);

        try
        {
            Type iph = handler.GetType().GetInterface("MarkdownPreview.IPreviewHandler");
            if (iph == null)
            {
                Console.WriteLine("[3] IPreviewHandler NOT FOUND");
            }
            else
            {
                object rect = Activator.CreateInstance(handler.GetType().Assembly.GetType("MarkdownPreview.RECT"));
                rect.GetType().GetField("Left").SetValue(rect, 0);
                rect.GetType().GetField("Top").SetValue(rect, 0);
                rect.GetType().GetField("Right").SetValue(rect, 600);
                rect.GetType().GetField("Bottom").SetValue(rect, 450);
                object[] margs = new object[] { hostHandle, rect };
                iph.InvokeMember("SetWindow", BindingFlags.InvokeMethod, null, handler, margs);
                Console.WriteLine("[3] SetWindow: OK");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[3] SetWindow FAILED: " + Flatten(ex));
        }

        // DoPreview
        try
        {
            Type iph = handler.GetType().GetInterface("MarkdownPreview.IPreviewHandler");
            iph.InvokeMember("DoPreview", BindingFlags.InvokeMethod, null, handler, null);
            Console.WriteLine("[4] DoPreview: OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[4] DoPreview FAILED: " + Flatten(ex));
        }

        // Let the control create handles and lay out
        for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(80); }

        // Dump the host's children and render
        Console.WriteLine("[5] host child count: " + CountChildren(hostHandle));
        try
        {
            host.Refresh();
            using (Bitmap bmp = new Bitmap(host.Width, host.Height))
            {
                host.DrawToBitmap(bmp, new Rectangle(0, 0, host.Width, host.Height));
                bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("[6] rendered -> " + outPng);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[6] render FAILED: " + Flatten(ex));
        }

        Console.WriteLine("=== done ===");
        return 0;
    }

    static int CountChildren(IntPtr parent)
    {
        int count = 0;
        EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) { count++; return true; }, IntPtr.Zero);
        return count;
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc lpEnumFunc, IntPtr lParam);

    static string Flatten(Exception ex)
    {
        string s = ex.GetType().FullName + ": " + ex.Message;
        Exception e = ex.InnerException;
        int depth = 0;
        while (e != null && depth < 6)
        {
            s += "\n      -> " + e.GetType().FullName + ": " + e.Message;
            e = e.InnerException;
            depth++;
        }
        return s;
    }
}
