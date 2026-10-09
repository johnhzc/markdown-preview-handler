# One-command deploy + live test for the Markdown preview handler.
#
#   Run from an ADMIN PowerShell (elevation is needed to refresh the GAC):
#
#     powershell -ExecutionPolicy Bypass -File .\test\deploy_and_test.ps1
#
# 1. refreshes the GAC copy from bin\ (kills prevhost first, because it keeps
#    the old assembly locked - that lock silently broke earlier deploys)
# 2. verifies the registration
# 3. restarts Explorer
# 4. clears the log, then watches while you test the preview pane

[CmdletBinding()]
param(
    [int]$Seconds = 60
)

$ErrorActionPreference = 'Stop'

$Root    = Split-Path -Parent $PSScriptRoot
$SrcDll  = Join-Path $Root 'bin\MarkdownPreviewHandler.dll'
$GacDll  = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\MarkdownPreviewHandler\v4.0_1.0.0.0__aad039b6d8722aba\MarkdownPreviewHandler.dll'
$InstallDll = Join-Path $env:ProgramFiles 'MarkdownPreviewHandler\MarkdownPreviewHandler.dll'
$Clsid   = '{3511EA64-B51A-4298-B6C3-6AB81F179E1C}'
$LogDir  = Join-Path $env:LOCALAPPDATA 'MarkdownPreviewHandler'
$Log     = Join-Path $LogDir 'handler.log'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host 'This script must run from an ADMIN PowerShell.' -ForegroundColor Yellow
    Write-Host 'Open Terminal as administrator, then run:' -ForegroundColor Yellow
    Write-Host ("  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"") -ForegroundColor Cyan
    return
}

if (-not (Test-Path $SrcDll)) { throw "Not built: $SrcDll (run build.cmd)" }
$srcLen = (Get-Item $SrcDll).Length
Write-Host "[--] build: $srcLen bytes"

# ---------- deploy (already elevated) ----------
Get-Process prevhost -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 900
Copy-Item $SrcDll $GacDll -Force
Copy-Item $SrcDll $InstallDll -Force
Write-Host "[OK] deployed to GAC and Program Files"

$gacLen = (Get-Item $GacDll).Length
if ($gacLen -ne $srcLen) {
    throw "GAC copy is $gacLen bytes but bin is $srcLen - deployment failed (prevhost still holding the file?)"
}
Write-Host "[OK] GAC and Program Files are up to date ($gacLen bytes)"

# ---------- verify registration ----------
$checks = @(
    @{ n = 'InprocServer32'; v = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$Clsid\InprocServer32" -ErrorAction SilentlyContinue).'(default)'; want = 'mscoree.dll' }
    @{ n = 'AppID';          v = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$Clsid" -ErrorAction SilentlyContinue).AppID;  want = '{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}' }
    @{ n = 'PreviewHandlers';v = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers' -ErrorAction SilentlyContinue).$Clsid; want = $null }
)
foreach ($c in $checks) {
    $ok = if ($c.want) { $c.v -eq $c.want } else { [bool]$c.v }
    Write-Host ("  [{0}] {1} = {2}" -f $(if ($ok) { 'OK' } else { '!!' }), $c.n, $c.v)
}

# ---------- restart Explorer ----------
Write-Host '[..] restarting explorer.exe'
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1500
if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
Start-Sleep -Seconds 3

# ---------- live test ----------
& (Join-Path $PSScriptRoot 'live_test.ps1') -Seconds $Seconds
