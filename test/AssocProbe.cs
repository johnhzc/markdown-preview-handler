using System;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Replicates exactly how the shell resolves a preview handler for an
/// extension: AssocQueryString with ASSOCSTR_SHELLEXTENSION, walking the
/// same class-merge order Explorer uses.
/// </summary>
static class AssocProbe
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int AssocQueryString(int flags, int str, string pszAssoc, string pszExtra, StringBuilder pszOut, ref int pcchOut);

    const int ASSOCF_NONE = 0;
    const int ASSOCSTR_SHELLEXTENSION = 16;   // preview handler CLSID
    const int ASSOCSTR_FRIENDLYAPPNAME = 4;
    const int ASSOCSTR_COMMAND = 1;

    static string Query(int str, string assoc, string extra)
    {
        int len = 1024;
        StringBuilder sb = new StringBuilder(len);
        int hr = AssocQueryString(ASSOCF_NONE, str, assoc, extra, sb, ref len);
        if (hr != 0) return "(hr=0x" + hr.ToString("X8") + ")";
        return sb.ToString();
    }

    static void Main()
    {
        Console.WriteLine("=== Shell-level association resolution (64-bit: " + Environment.Is64BitProcess + ") ===");
        string[] exts = { ".md", ".markdown", ".jpg", ".png" };
        foreach (string ext in exts)
        {
            Console.WriteLine("");
            Console.WriteLine("--- " + ext + " ---");
            Console.WriteLine("  ProgID     : " + Query(ASSOCSTR_FRIENDLYAPPNAME, ext, null));
            Console.WriteLine("  PreviewCLSID: " + Query(ASSOCSTR_SHELLEXTENSION, ext, "{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}"));
            Console.WriteLine("  OpenCommand: " + Query(ASSOCSTR_COMMAND, ext, null));
        }
        Console.WriteLine("");
        Console.WriteLine("=== done ===");
    }
}
