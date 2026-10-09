using System;
using System.IO;
using System.Windows.Forms;

/// <summary>
/// Standalone STA/MTA reporter: writes an environment report so we can see
/// what threading model the host actually hands us.
/// </summary>
static class ThreadReport
{
    static void Main()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "thread_report.txt");
        string info =
            "ApartmentState of current thread: " + System.Threading.Thread.CurrentThread.GetApartmentState() + "\r\n" +
            "IsBackground: " + System.Threading.Thread.CurrentThread.IsBackground + "\r\n" +
            "Process: " + Environment.Is64BitProcess + "\r\n" +
            "AppBase: " + AppDomain.CurrentDomain.BaseDirectory + "\r\n" +
            "Now: " + DateTime.Now + "\r\n";
        File.AppendAllText(path, info + "---\r\n");
        Console.WriteLine(info);
    }
}
