using System;
using System.Reflection;
using System.Runtime.InteropServices;

/// <summary>
/// COM loading probe: distinguishes assembly-level failures from
/// COM/shim-level failures for the preview handler class.
/// Modes: /asm (managed load), /com (CoCreateInstance inproc),
/// /surrogate (CLSCTX_LOCAL_SERVER - what Explorer/prevhost really does).
/// </summary>
static class ComProbe
{
    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(ref Guid clsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    static Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");
    const uint CLSCTX_INPROC_SERVER = 0x1;
    const uint CLSCTX_LOCAL_SERVER = 0x4;

    static int Main(string[] args)
    {
        // Accept "com"/"asm"/"surrogate" (no slashes, Git Bash friendly) or "/com" etc.
        string mode = args.Length > 0 ? args[0].TrimStart('/').ToLowerInvariant() : "all";
        Console.WriteLine("=== ComProbe (64-bit: " + (Environment.Is64BitProcess ? "yes" : "no") + ", CLR: " + Environment.Version + ", mode: " + mode + ") ===");
        Console.WriteLine("AppBase: " + AppDomain.CurrentDomain.BaseDirectory);
        string dllPath = @"C:\Program Files\MarkdownPreviewHandler\MarkdownPreviewHandler.dll";

        // --- Test 1: direct managed load (bypasses COM entirely) ---
        if (mode == "asm" || mode == "all")
        {
            Console.WriteLine("[Test 1] Assembly.LoadFrom(\"" + dllPath + "\")");
            try
            {
                Assembly asm = Assembly.LoadFrom(dllPath);
                Console.WriteLine("  OK: " + asm.FullName);
                Type t = asm.GetType("MarkdownPreview.PreviewHandler", true);
                Console.WriteLine("  Type OK: " + t.FullName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  FAILED: 0x" + ex.HResult.ToString("X8") + " " + ex.GetType().FullName);
                Exception e = ex;
                while (e != null) { Console.WriteLine("    " + e.Message); e = e.InnerException; }
            }
        }

        Guid clsid = new Guid("3511EA64-B51A-4298-B6C3-6AB81F179E1C");

        // --- Test 2: full COM activation path, in-process ---
        if (mode == "com" || mode == "all")
        {
            Console.WriteLine("[Test 2] CoCreateInstance via CLSID (managed, inproc)");
            Type comType = Type.GetTypeFromCLSID(clsid);
            Console.WriteLine("  Type from CLSID: " + (comType == null ? "(null)" : comType.FullName));
            try
            {
                object obj = Activator.CreateInstance(comType);
                Console.WriteLine("  OK: " + obj.GetType().FullName);
                Console.WriteLine("  Assembly: " + obj.GetType().Assembly.Location);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  FAILED: 0x" + ex.HResult.ToString("X8") + " " + ex.GetType().FullName);
                Exception e = ex;
                while (e != null) { Console.WriteLine("    " + e.Message); e = e.InnerException; }
            }
        }

        // --- Test 3: raw CoCreateInstance with explicit context ---
        // surrogate forces CLSCTX_LOCAL_SERVER = exactly what the shell
        // preview host does, routing into prevhost.exe.
        if (mode == "surrogate" || mode == "com" || mode == "all")
        {
            uint ctx = mode == "surrogate" ? CLSCTX_LOCAL_SERVER : CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER;
            Console.WriteLine("[Test 3] Raw CoCreateInstance, CLSCTX=0x" + ctx.ToString("X"));
            IntPtr punk;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, ctx, ref IID_IUnknown, out punk);
            if (hr == 0)
            {
                Console.WriteLine("  OK: S_OK, instance created");
                Marshal.Release(punk);
            }
            else
            {
                Console.WriteLine("  FAILED: hr=0x" + hr.ToString("X8") + " (" + new System.ComponentModel.Win32Exception(hr).Message + ")");
            }
        }

        Console.WriteLine("=== done ===");
        return 0;
    }
}
