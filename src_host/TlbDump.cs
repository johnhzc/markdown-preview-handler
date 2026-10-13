using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

// Dumps the interfaces/methods a registered type library exposes, so we can see
// exactly which COM interfaces a cross-process client can marshal.
internal static class TlbDump
{
    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int LoadTypeLibEx(string szFile, int regkind, out ITypeLib pptlib);

    internal static int Run(string path)
    {
        ITypeLib lib;
        int hr = LoadTypeLibEx(path, 0, out lib);
        Console.WriteLine("LoadTypeLibEx hr=0x" + hr.ToString("X8"));
        if (hr != 0) return 1;

        int count = lib.GetTypeInfoCount();
        Console.WriteLine("typeinfo count: " + count);
        for (int i = 0; i < count; i++)
        {
            ITypeInfo ti;
            lib.GetTypeInfo(i, out ti);
            string name = null, doc = null, help = null;
            int ctx;
            ti.GetDocumentation(-1, out name, out doc, out ctx, out help);
            TYPEATTR attr;
            ti.GetTypeAttr(out attr);
            Console.WriteLine("  [" + i + "] " + name +
                "  kind=" + attr.typekind +
                "  guid=" + attr.guid.ToString("B") +
                "  funcs=" + attr.cFuncs +
                "  vars=" + attr.cVars);

            for (int f = 0; f < attr.cFuncs; f++)
            {
                FUNCDESC fd;
                ti.GetFuncDesc(f, out fd);
                string fname = null;
                try { ti.GetDocumentation(fd.memid, out fname, out doc, out ctx, out help); }
                catch { }
                Console.WriteLine("        fn[" + f + "] " + fname +
                    " invkind=" + fd.invkind + " cParams=" + fd.cParams);
                ti.ReleaseFuncDesc(ref fd);
            }
            ti.ReleaseTypeAttr(ref attr);
            Marshal.ReleaseComObject(ti);
        }
        Marshal.ReleaseComObject(lib);
        return 0;
    }
}
