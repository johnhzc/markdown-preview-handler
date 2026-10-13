# Register the NATIVE preview handler with Explorer.
#
#   Run from an ADMIN PowerShell:
#       powershell -ExecutionPolicy Bypass -File .\native\install_native.ps1
#       powershell -ExecutionPolicy Bypass -File .\native\install_native.ps1 -Uninstall
#
# What it does
#   1. copies MarkdownPreviewNative.dll + WebView2Loader.dll to
#      %ProgramFiles%\MarkdownPreviewHandler
#   2. clears every trace of the managed (.NET) handler: its CLSID/AppID keys
#      and the extension->CLSID associations that pointed at it
#   3. regsvr32 the native DLL, which writes its own keys
#      (InprocServer32 = the DLL, ThreadingModel = Apartment, no AppID)
#   4. verifies the result and restarts Explorer
#
# The extension associations written by the native DLL use the SHELL's
# preview-handler GUID {8895b1c6-b41f-4c1c-a562-0d564250836f} - not the
# IPreviewHandler IID {8895b1c6-b7f3-...} that the managed build used by
# mistake.

[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\MarkdownPreviewHandler",
    [switch]$Uninstall,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'

$Clsid       = '{3511EA64-B51A-4298-B6C3-6AB81F179E1C}'
$ShellGuid   = '{8895b1c6-b41f-4c1c-a562-0d564250836f}'
$StaleGuid   = '{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}'   # old, wrong GUID
$Extensions  = @('.md', '.markdown', '.mdown', '.mkd',
                 '.jpg', '.jpeg', '.png', '.gif', '.bmp', '.tif', '.tiff', '.ico')
$Root        = Split-Path -Parent $PSScriptRoot
$SrcDll      = Join-Path $Root 'bin\MarkdownPreviewNative.dll'
$SrcLoader   = Join-Path $Root 'bin\WebView2Loader.dll'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host 'Run this from an ADMIN PowerShell.' -ForegroundColor Yellow
    Write-Host ("  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"") -ForegroundColor Cyan
    return
}

if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }

function Restart-Explorer {
    if ($NoRestart) { return }
    Write-Host '[..] restarting explorer.exe'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1500
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
    Write-Host '[OK] explorer restarted'
}

function Remove-ExtensionKeys {
    param([string]$Guid)
    foreach ($ext in $Extensions) {
        foreach ($base in 'HKLM:\SOFTWARE\Classes', 'HKCU:\Software\Classes') {
            foreach ($sub in "$ext\shellex\$Guid",
                             "SystemFileAssociations\$ext\shellex\$Guid") {
                $k = Join-Path $base $sub
                if (Test-Path $k) {
                    Remove-Item -Path $k -Recurse -Force -ErrorAction SilentlyContinue
                    Write-Host "  removed $k"
                }
            }
        }
    }
}

# ------------------------------------------------------------------ uninstall
if ($Uninstall) {
    $dll = Join-Path $InstallDir 'MarkdownPreviewNative.dll'
    if (Test-Path $dll) {
        $p = Start-Process regsvr32.exe -ArgumentList '/s', '/u', "`"$dll`"" -Wait -PassThru
        Write-Host ("[OK] regsvr32 /u exit=" + $p.ExitCode)
    }
    Remove-ExtensionKeys -Guid $ShellGuid
    # also clear the newer-style shell GUID that the native DLL registers
    Remove-ExtensionKeys -Guid $ShellGuid
    Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Restart-Explorer
    return
}

# -------------------------------------------------------------------- install
if (-not (Test-Path $SrcDll)) {
    throw "Not built: $SrcDll  (run native\build_native.ps1 first)"
}

Write-Host '=== 1. clean out the managed handler ==='
Get-Process prevhost -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# its COM registration (both registry views) and surrogate AppID
foreach ($hive in 'HKLM:\SOFTWARE\Classes\CLSID', 'HKLM:\SOFTWARE\Classes\Wow6432Node\CLSID') {
    $k = Join-Path $hive $Clsid
    if (Test-Path $k) { Remove-Item -Path $k -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "  removed $k" }
}
Remove-Item -Path 'HKLM:\SOFTWARE\Classes\MarkdownPreview.PreviewHandler' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path 'HKCU:\Software\Classes\MarkdownPreview.MdFile' -Recurse -Force -ErrorAction SilentlyContinue

# the managed build also registered associations under the WRONG guid
Remove-ExtensionKeys -Guid $StaleGuid

# ------------------------------------------------------------------ 2. deploy
Write-Host '=== 2. deploy the native DLL ==='
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $SrcDll $InstallDir -Force
if (Test-Path $SrcLoader) { Copy-Item $SrcLoader $InstallDir -Force }
$dll = Join-Path $InstallDir 'MarkdownPreviewNative.dll'
Write-Host "[OK] $dll"
if (-not (Test-Path (Join-Path $InstallDir 'WebView2Loader.dll'))) {
    Write-Host '[!!] WebView2Loader.dll is missing next to the handler - preview will fall back to plain text.' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 3. register
Write-Host '=== 3. regsvr32 ==='
$p = Start-Process regsvr32.exe -ArgumentList '/s', "`"$dll`"" -Wait -PassThru
Write-Host ("[OK] regsvr32 exit=" + $p.ExitCode)

# ------------------------------------------------------------------ 4. verify
Write-Host '=== 4. verification ==='
$ips = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$Clsid\InprocServer32" -ErrorAction SilentlyContinue).'(default)'
$appid = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$Clsid" -ErrorAction SilentlyContinue).AppID
$tm = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$Clsid\InprocServer32" -ErrorAction SilentlyContinue).ThreadingModel
$ph = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers' -ErrorAction SilentlyContinue).$Clsid

Write-Host ("  InprocServer32 : " + $ips)
Write-Host ("  ThreadingModel : " + $tm)
Write-Host ("  AppID          : [" + $appid + "]   (must be empty)")
Write-Host ("  PreviewHandlers: " + $ph)

$bad = 0
if ($ips -ne $dll) { Write-Host '  [!!] InprocServer32 does not point at the deployed DLL' -ForegroundColor Yellow; $bad++ }
if ($appid)       { Write-Host '  [!!] an AppID is present; activation would go to a surrogate' -ForegroundColor Yellow; $bad++ }
if (-not $ph)     { Write-Host '  [!!] CLSID missing from the PreviewHandlers list' -ForegroundColor Yellow; $bad++ }

foreach ($ext in @('.md', '.markdown', '.png')) {
    $v = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\$ext\shellex\$ShellGuid" -ErrorAction SilentlyContinue).'(default)'
    $ok = ($v -eq $Clsid)
    if (-not $ok) { $bad++ }
    Write-Host ("  {0,-11} -> {1}  {2}" -f $ext, $v, $(if ($ok) { '' } else { '  <-- expected the native CLSID' }))
}

if ($bad -eq 0) { Write-Host '  [OK] registration looks complete' -ForegroundColor Green }

Restart-Explorer
Write-Host ''
Write-Host 'Now: open a folder with a .md file, press Alt+P, click the file.' -ForegroundColor Cyan
Write-Host 'Handler log: %LOCALAPPDATA%\MarkdownPreviewHandler\handler.log'
