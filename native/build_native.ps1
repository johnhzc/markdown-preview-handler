# Native Markdown preview handler - build, install and test
#
#   powershell -ExecutionPolicy Bypass -File .\native\build_native.ps1
#   powershell -ExecutionPolicy Bypass -File .\native\build_native.ps1 -Install
#   powershell -ExecutionPolicy Bypass -File .\native\build_native.ps1 -Install -Test
#   powershell -ExecutionPolicy Bypass -File .\native\build_native.ps1 -Uninstall
#
# The C++ sources live in native\src. This script drives MSBuild, then
# registers the DLL with regsvr32 (the DLL writes its own registry keys, which
# is the shape the working in-proc handlers on this machine use).

[CmdletBinding()]
param(
    [string]$WebView2Dir = $env:WEBVIEW2_DIR,
    [string]$Configuration = 'Release',
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$Test,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
$Here = $PSScriptRoot
$Root = Split-Path -Parent $Here
$OutDll = Join-Path $Root 'bin\MarkdownPreviewNative.dll'
$Clsid = '{3511EA64-B51A-4298-B6C3-6AB81F179E1C}'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Find-MSBuild {
    $candidates = @(
        "$env:ProgramFiles\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Restart-Explorer {
    if ($NoRestart) { return }
    Write-Host '[..] restarting explorer.exe'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1500
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
    Write-Host '[OK] explorer restarted'
}

# ---------------------------------------------------------------- uninstall
if ($Uninstall) {
    if (-not (Test-Admin)) {
        Write-Host 'Run this from an ADMIN PowerShell to unregister.' -ForegroundColor Yellow
        return
    }
    if (Test-Path $OutDll) {
        $p = Start-Process regsvr32.exe -ArgumentList '/s', '/u', "`"$OutDll`"" -Wait -PassThru
        Write-Host ("[OK] regsvr32 /u exit=" + $p.ExitCode)
    }
    Restart-Explorer
    return
}

# ---------------------------------------------------------------- build
$msbuild = Find-MSBuild
if (-not $msbuild) {
    Write-Host '=== MSBuild not found ===' -ForegroundColor Yellow
    Write-Host 'Install the C++ toolchain first (any one of these):'
    Write-Host '  winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"'
    Write-Host '  or install Visual Studio (Community) with "Desktop development with C++".'
    Write-Host ''
    Write-Host 'Then re-run:  powershell -ExecutionPolicy Bypass -File .\native\build_native.ps1'
    exit 2
}

Write-Host ("[--] MSBuild: " + $msbuild)
$msbuildArgs = @(
    (Join-Path $Here 'MarkdownPreviewHandler.vcxproj'),
    '/nologo', '/v:minimal', '/p:Configuration=' + $Configuration, '/p:Platform=x64'
)
if ($WebView2Dir) {
    Write-Host ("[--] WebView2: " + $WebView2Dir)
    $msbuildArgs += '/p:WebView2Dir=' + $WebView2Dir
} else {
    Write-Host '[!!] No WebView2 dir: the DLL will use the plain-text fallback renderer.' -ForegroundColor Yellow
    Write-Host '     Set -WebView2Dir (or $env:WEBVIEW2_DIR) for real Markdown rendering.'
}

& $msbuild @msbuildArgs
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with $LASTEXITCODE" }

if (-not (Test-Path $OutDll)) { throw "Build reported success but $OutDll is missing" }
$dll = Get-Item $OutDll
Write-Host ("[OK] built {0} ({1:n0} bytes)" -f $dll.Name, $dll.Length) -ForegroundColor Green

# ---------------------------------------------------------------- install
if ($Install) {
    if (-not (Test-Admin)) {
        Write-Host 'Run this from an ADMIN PowerShell to register.' -ForegroundColor Yellow
        return
    }
    Get-Process prevhost -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    $p = Start-Process regsvr32.exe -ArgumentList '/s', "`"$OutDll`"" -Wait -PassThru
    Write-Host ("[OK] regsvr32 exit=" + $p.ExitCode)

    Write-Host '[--] verification:'
    $ips = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$Clsid\InprocServer32" -ErrorAction SilentlyContinue).'(default)'
    Write-Host ("     InprocServer32      : " + $ips)
    Write-Host ("     AppID (must be gone): [" + (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$Clsid" -ErrorAction SilentlyContinue).AppID + "]")
    Write-Host ("     PreviewHandlers     : " + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers' -ErrorAction SilentlyContinue).$Clsid)
    Restart-Explorer
}

# ---------------------------------------------------------------- test
if ($Test) {
    Write-Host ''
    Write-Host 'Now: open a folder with a .md file, press Alt+P, click the file.' -ForegroundColor Cyan
    Write-Host 'The handler writes a log to %LOCALAPPDATA%\MarkdownPreviewHandler\handler.log'
    $log = Join-Path $env:LOCALAPPDATA 'MarkdownPreviewHandler\handler.log'
    if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }
    Write-Host 'Waiting 60s for the log to appear ...'
    for ($i = 0; $i -lt 120; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $log) {
            Write-Host '*** handler log ***' -ForegroundColor Green
            Get-Content $log | ForEach-Object { Write-Host ('    ' + $_) }
            break
        }
    }
    if (-not (Test-Path $log)) { Write-Host '(no log - the DLL was not loaded)' -ForegroundColor Yellow }
}
