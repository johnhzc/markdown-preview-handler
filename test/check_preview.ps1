# Preview handler self-check. Read-only: it inspects state and prints a verdict.
#
#     powershell -ExecutionPolicy Bypass -File .\test\check_preview.ps1
#
# Checks:
#   1. What the shell resolves for .md via the same API Explorer uses
#      (AssocQueryString + IPreviewHandler shellex GUID)
#   2. CLSID / AppID / surrogate-host architecture
#   3. PreviewHandlers list, Approved whitelist, GAC, extension associations
#   4. Whether handler.log shows the handler was ever invoked

param(
    [string]$Probe = 'D:\BuddyData\Tasks\markdown-preview-handler\test\sample.md'
)

$Clsid   = '{3511EA64-B51A-4298-B6C3-6AB81F179E1C}'
$Shellex = '{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}'
$AppId64 = '{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}'
$AppId32 = '{534A1E02-D58F-44f0-B58B-36CBED287C7C}'
$Token   = 'aad039b6d8722aba'
$LogPath = Join-Path $env:LOCALAPPDATA 'MarkdownPreviewHandler\handler.log'

$problems = New-Object System.Collections.Generic.List[string]

function Say($text, $level) {
    switch ($level) {
        'ok'   { Write-Host "  [OK] $text" -ForegroundColor Green }
        'warn' { Write-Host "  [!!] $text" -ForegroundColor Yellow }
        default { Write-Host "  [--] $text" }
    }
}

function Get-RegValue($path, $name) {
    try { (Get-ItemProperty -Path $path -Name $name -ErrorAction Stop).$name }
    catch { $null }
}

Write-Host '=== Markdown preview handler self-check ===' -ForegroundColor Cyan
$bits = if ([Environment]::Is64BitProcess) { '64' } else { '32' }
Write-Host "PowerShell process bits: $bits"
Write-Host ''

# ---------------------------------------------------------------- 1
Write-Host '1. Shell preview-handler resolution (AssocQueryString, same API Explorer uses)' -ForegroundColor Cyan
$sig = @'
using System;using System.Text;using System.Runtime.InteropServices;
public class AssocProbe {
 [DllImport("shlwapi.dll",CharSet=CharSet.Unicode,PreserveSig=true)]
 public static extern int AssocQueryStringW(int flags,int str,string pszAssoc,string pszExtra,StringBuilder buf,ref int pcch);
 public static string Q(string ext,string extra){
   var sb=new StringBuilder(1024); int n=1024;
   int hr=AssocQueryStringW(0,16,ext,extra,sb,ref n);
   return hr==0? sb.ToString() : "(hr=0x"+hr.ToString("X8")+")";
 }
}
'@
Add-Type -TypeDefinition $sig -Language CSharp | Out-Null

foreach ($ext in '.md', '.markdown', '.png', '.jpg') {
    $r = [AssocProbe]::Q($ext, $Shellex)
    if ($r -eq $Clsid) {
        Say "$ext -> our handler" 'ok'
    } else {
        Say "$ext -> $r" 'warn'
        if ($ext -eq '.md' -or $ext -eq '.markdown') {
            $problems.Add("$ext does not resolve to our handler (shell will not invoke us)")
        }
    }
}
Write-Host ''

# ---------------------------------------------------------------- 2
Write-Host '2. COM registration and surrogate host' -ForegroundColor Cyan
$ips = Get-RegValue "HKLM:\SOFTWARE\Classes\CLSID\$Clsid\InprocServer32" '(default)'
if ($ips -eq 'mscoree.dll') {
    Say 'InprocServer32 = mscoree.dll' 'ok'
} else {
    Say "InprocServer32 = $ips (should be mscoree.dll)" 'warn'
    $problems.Add('InprocServer32 is not mscoree.dll')
}

$asm = Get-RegValue "HKLM:\SOFTWARE\Classes\CLSID\$Clsid\InprocServer32" 'Assembly'
if ($asm) { Say "Assembly = $asm" 'ok' }
else { Say 'Assembly value missing' 'warn'; $problems.Add('InprocServer32\Assembly is missing') }

$appid = Get-RegValue "HKLM:\SOFTWARE\Classes\CLSID\$Clsid" 'AppID'
if ($appid -eq $AppId64) {
    Say "AppID = $AppId64 (64-bit prevhost) - correct" 'ok'
} elseif ($appid -eq $AppId32) {
    Say "AppID = $appid  <-- 32-BIT surrogate (SysWOW64\prevhost.exe)" 'warn'
    Say 'This assembly is AnyCPU and only loads into the 64-bit CLR.' 
    Say 'Sent to the 32-bit surrogate, activation fails: Explorer shows'
    Say '"no preview available" and handler.log stays empty.'
    $problems.Add('AppID points at the 32-bit surrogate; must be 64-bit prevhost')
} else {
    Say "AppID = $appid" 'warn'
    $problems.Add('AppID is not the 64-bit prevhost surrogate')
}
$sur = Get-RegValue "HKLM:\SOFTWARE\Classes\AppID\$AppId64" 'DllSurrogate'
Say "Surrogate DllSurrogate = $sur"
Write-Host ''

# ---------------------------------------------------------------- 3
Write-Host '3. PreviewHandlers list / Approved whitelist / GAC' -ForegroundColor Cyan
$ph = Get-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers' $Clsid
if ($ph) { Say "listed in PreviewHandlers: $ph" 'ok' }
else {
    Say 'CLSID missing from PreviewHandlers (Explorer shows "cannot preview this file")' 'warn'
    $problems.Add('CLSID missing from the PreviewHandlers list')
}

$ap = Get-RegValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved' $Clsid
if ($ap) { Say "listed in Approved whitelist: $ap" 'ok' }
else {
    Say 'CLSID missing from Shell Extensions\Approved (ignored on Win11 24H2+)' 'warn'
    $problems.Add('CLSID missing from the Approved whitelist')
}

$gac = "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\MarkdownPreviewHandler\v4.0_1.0.0.0__$Token\MarkdownPreviewHandler.dll"
if (Test-Path $gac) {
    $gi = Get-Item $gac
    Say ("GAC copy present ({0:n0} bytes, {1})" -f $gi.Length, $gi.LastWriteTime) 'ok'
} else {
    Say "no GAC copy: $gac" 'warn'
    $problems.Add('Assembly not in the GAC (the mscoree shim only resolves from GAC / host dir)')
}

foreach ($ext in '.md', '.markdown', '.png', '.jpg') {
    $v = Get-RegValue "HKLM:\SOFTWARE\Classes\$ext\shellex\$Shellex" '(default)'
    if ($v -eq $Clsid) { Say "$ext shellex -> our handler" 'ok' }
    else { Say "$ext shellex -> $v" 'warn' }
}
Write-Host ''

# ---------------------------------------------------------------- 4
Write-Host '4. Was the handler ever invoked?' -ForegroundColor Cyan
if (Test-Path $LogPath) {
    $lines = @(Get-Content $LogPath)
    Say "log: $LogPath ($($lines.Count) lines)"
    Say "last line: $($lines[-1])"
    $invoked = @($lines | Where-Object { $_ -match 'DoPreview: file=' })
    if ($invoked.Count -gt 0) {
        Say 'Handler was activated and DoPreview ran: the registration chain works.' 'ok'
    } else {
        Say 'No "DoPreview: file=" entry in the log.' 'warn'
    }
} else {
    Say "no log yet: $LogPath" 'warn'
    Say 'Explorer has never managed to activate the handler (registration side, not rendering).'
}
Write-Host ''

# ---------------------------------------------------------------- verdict
Write-Host '=== Verdict ===' -ForegroundColor Cyan
if ($problems.Count -eq 0) {
    Write-Host 'No registration problem found. If the pane is still blank, look at the rendering side and make sure explorer.exe was restarted.' -ForegroundColor Green
} else {
    Write-Host "Found $($problems.Count) problem(s):" -ForegroundColor Yellow
    $problems | ForEach-Object { Write-Host "  * $_" -ForegroundColor Yellow }
    Write-Host ''
    Write-Host 'Fix: run as administrator   .\fix-preview.ps1' -ForegroundColor Cyan
}
Write-Host ''
Write-Host "Test file: $Probe  (select it in Explorer and turn on the preview pane with Alt+P)"
