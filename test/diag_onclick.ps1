# One-shot diagnostic for the Markdown preview handler.
#
#   Run in a NORMAL PowerShell (no admin needed):
#       powershell -ExecutionPolicy Bypass -File .\test\diag_onclick.ps1
#
# It will:
#   1. print the current registration state (the diagnostics that matter)
#   2. clear %LOCALAPPDATA%\MarkdownPreviewHandler\handler.log
#   3. open the test folder in Explorer
#   4. wait while YOU open the preview pane (Alt+P) and click sample.md
#   5. report the pane state and the handler log, then dump prevhost's modules
#
# The verdict table is printed at the end.

param(
    [string]$Folder = 'D:\BuddyData\Tasks\markdown-preview-handler\test',
    [string]$File = 'sample.md',
    [int]$Seconds = 90
)

$Clsid = '{3511EA64-B51A-4298-B6C3-6AB81F179E1C}'
$Sh = '{8895b1c6-b41f-4c1c-a562-0d564250836f}'
$LogDir = Join-Path $env:LOCALAPPDATA 'MarkdownPreviewHandler'
$Log = Join-Path $LogDir 'handler.log'

# ---- registration summary -------------------------------------------------
Write-Host '=== registration ===' -ForegroundColor Cyan
$ips = Join-Path "HKLM:\SOFTWARE\Classes\CLSID\$Clsid" 'InprocServer32'
Write-Host ("  InprocServer32 : " + (Get-ItemProperty $ips -ErrorAction SilentlyContinue).'(default)')
Write-Host ("  Assembly       : " + (Get-ItemProperty $ips -ErrorAction SilentlyContinue).Assembly)
Write-Host ("  AppID          : " + (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$Clsid" -ErrorAction SilentlyContinue).AppID)
Write-Host ("  .md shellex    : " + (Get-ItemProperty "HKLM:\SOFTWARE\Classes\.md\shellex\$Sh" -ErrorAction SilentlyContinue).'(default)')
Write-Host ("  PreviewHandlers: " + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers' -ErrorAction SilentlyContinue).$Clsid)
$gac = "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\MarkdownPreviewHandler\v4.0_1.0.0.0__aad039b6d8722aba\MarkdownPreviewHandler.dll"
if (Test-Path $gac) {
    $gi = Get-Item $gac
    Write-Host ("  GAC copy       : {0} bytes ({1})" -f $gi.Length, $gi.LastWriteTime)
} else {
    Write-Host '  GAC copy       : MISSING' -ForegroundColor Yellow
}

# ---- helper: pane state ---------------------------------------------------
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.Text;
public class PaneWatch {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder b, int n);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder b, int n);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Rt,B; }
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  static string C(IntPtr h){var b=new StringBuilder(256);GetClassName(h,b,256);return b.ToString();}
  static string T(IntPtr h){var b=new StringBuilder(512);GetWindowText(h,b,512);return b.ToString();}
  public static string State(string leaf){
    string res="(window not found)";
    EnumWindows(delegate(IntPtr h, IntPtr p){
      if(C(h)!="CabinetWClass") return true;
      if(T(h).IndexOf(leaf,StringComparison.OrdinalIgnoreCase)<0) return true;
      string s="(pane not created)";
      EnumChildWindows(h, delegate(IntPtr c, IntPtr q){
        if(C(c)=="Shell Preview Extension Host"){ R r; GetWindowRect(c,out r);
          s=(IsWindowVisible(c)?"OPEN ":"closed ")+(r.Rt-r.L)+"x"+(r.B-r.T); }
        return true;
      }, IntPtr.Zero);
      res=s; return false;
    }, IntPtr.Zero);
    return res;
  }
}
'@ -Language CSharp

# ---- reset log and open the folder ---------------------------------------
if (Test-Path $LogDir) { Remove-Item $LogDir -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ''
Write-Host '=== now do this ===' -ForegroundColor Yellow
Write-Host "  1. in the Explorer window that just opened, go to $Folder"
Write-Host "  2. press Alt+P and CONFIRM the preview panel appears on the right"
Write-Host "  3. click $File once and wait 3 seconds"
Write-Host ''
Start-Process explorer.exe $Folder
Start-Sleep -Seconds 3

$leaf = Split-Path $Folder -Leaf
Write-Host "watching for $Seconds s ..." -ForegroundColor Cyan
$found = $false
$t0 = Get-Date
for ($i = 0; $i -lt ($Seconds * 2); $i++) {
    Start-Sleep -Milliseconds 500
    if ($i % 20 -eq 0) {
        Write-Host ("  [t+{0,3}s] pane: {1}" -f [int]((Get-Date) - $t0).TotalSeconds, [PaneWatch]::State($leaf))
    }
    if (Test-Path $Log) {
        Start-Sleep -Seconds 1
        Write-Host ''
        Write-Host '*** handler.log appeared ***' -ForegroundColor Green
        Get-Content $Log | ForEach-Object { Write-Host ('    ' + $_) }
        $found = $true
        break
    }
}

Write-Host ''
Write-Host '=== handler log ===' -ForegroundColor Cyan
if (Test-Path $Log) { Get-Content $Log } else { Write-Host '(none)' -ForegroundColor Yellow }

Write-Host ''
Write-Host '=== prevhost ===' -ForegroundColor Cyan
Get-Process prevhost -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("  pid={0}  started {1}" -f $_.Id, $_.StartTime)
    try {
        $m = (($_.Modules | Where-Object { $_.ModuleName -match 'mscoree|clr\.dll|mscorlib|clrjit|MarkdownPreviewHandler' }).ModuleName) -join ', '
        Write-Host ("    managed modules: " + $(if ($m) { $m } else { '(none)' }))
    } catch { Write-Host '    (cannot read modules)' }
}

Write-Host ''
Write-Host '=== how to read the result ===' -ForegroundColor Cyan
Write-Host '  log has "MODULE LOADED" and "DoPreview"  -> handler ran; rendering issue remains'
Write-Host '  log has "MODULE LOADED" but no DoPreview  -> handler ran then failed mid-way'
Write-Host '  no log at all                            -> shell never instantiated us'
Write-Host '  pane "closed" the whole time             -> the pane was never opened (no test happened)'
