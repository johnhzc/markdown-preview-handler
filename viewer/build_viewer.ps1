# Build the standalone Markdown viewer (MarkdownViewer.exe).
#
#   powershell -ExecutionPolicy Bypass -File .\viewer\build_viewer.ps1
#
# Needs the MSVC toolchain (Visual Studio Build Tools with "Desktop development
# with C++"). Output: bin\MarkdownViewer.exe
#
# The viewer needs WebView2Loader.dll next to the exe; this script copies it
# from native\webview2 if it is not already in bin\.

[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$ObjDir = Join-Path $Root "obj\viewer"

function Find-VcVars {
    $candidates = @(
        "$env:ProgramFiles\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }

    # fall back: any BuildTools install found via vswhere (handles custom paths,
    # e.g. an installation on D:)
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $p = & $vswhere -products '*' -all -prerelease -property installationPath 2>$null | Select-Object -First 1
        if ($p) {
            $bat = Join-Path $p 'VC\Auxiliary\Build\vcvars64.bat'
            if (Test-Path $bat) { return $bat }
        }
    }
    return $null
}

$vcvars = Find-VcVars
if (-not $vcvars) {
    Write-Host 'MSVC toolchain not found.' -ForegroundColor Yellow
    Write-Host 'Install "Visual Studio Build Tools" with the "Desktop development with C++" workload.'
    exit 2
}
Write-Host "[--] using $vcvars"

New-Item -ItemType Directory -Force -Path $ObjDir | Out-Null

$script = @"
@echo off
call "$vcvars" >nul 2>&1
cd /d "$Root"
cl /nologo /utf-8 /EHsc /std:c++17 /W3 /O2 /DUNICODE /D_UNICODE ^
  /Fe:bin\MarkdownViewer.exe /Fo:"$ObjDir\\" ^
  viewer\StandaloneViewer.cpp ^
  src_common\WebView2Interfaces.cpp ^
  native\src\MarkdownToHtml.cpp ^
  /I src_common /I native\src ^
  /link /SUBSYSTEM:WINDOWS user32.lib gdi32.lib ole32.lib comdlg32.lib shlwapi.lib shell32.lib
exit /b %ERRORLEVEL%
"@

$tmp = Join-Path $env:TEMP 'build_viewer_generated.cmd'
Set-Content -Path $tmp -Value $script -Encoding ASCII
& cmd.exe /c $tmp
$rc = $LASTEXITCODE
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

if ($rc -ne 0) { throw "Build failed with exit code $rc" }

$exe = Join-Path $Root 'bin\MarkdownViewer.exe'
if (-not (Test-Path $exe)) { throw "Build reported success but $exe is missing" }
$f = Get-Item $exe
Write-Host ("[OK] built {0} ({1:n0} bytes)" -f $f.Name, $f.Length) -ForegroundColor Green

# WebView2Loader.dll must sit next to the exe
$loader = Join-Path $Root 'bin\WebView2Loader.dll'
if (-not (Test-Path $loader)) {
    $src = Join-Path $Root 'native\webview2\build\native\x64\WebView2Loader.dll'
    if (Test-Path $src) {
        Copy-Item $src $loader -Force
        Write-Host "[OK] copied WebView2Loader.dll into bin\"
    } else {
        Write-Host '[!!] WebView2Loader.dll is missing; the viewer needs it next to the exe.' -ForegroundColor Yellow
        Write-Host '     Get it from the Microsoft.Web.WebView2 NuGet package (build\native\x64\).'
    }
}

Write-Host ''
Write-Host 'Next:  powershell -ExecutionPolicy Bypass -File .\viewer\install_viewer.ps1' -ForegroundColor Cyan
