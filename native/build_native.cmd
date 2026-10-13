@echo off
setlocal EnableDelayedExpansion
REM ============================================================
REM  Build the native (in-process) Markdown preview handler.
REM
REM  Needs the MSVC toolchain: "Desktop development with C++" from
REM  Visual Studio Build Tools, or any Visual Studio with C++.
REM
REM  WebView2 (optional but recommended):
REM    set WEBVIEW2_DIR=C:\path\to\Microsoft.Web.WebView2.1.0.xxxx
REM  Expected layout under that dir:
REM    include\WebView2.h
REM    lib\x64\WebView2Loader.lib
REM    x64\WebView2Loader.dll
REM  Without it the DLL still builds and falls back to plain text drawing.
REM
REM  Output: bin\MarkdownPreviewNative.dll
REM ============================================================

REM ---- locate msbuild ------------------------------------------------------
set "MSBUILD="
for %%P in (
    "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
) do (
    if not defined MSBUILD if exist %%P set "MSBUILD=%%~P"
)

if not defined MSBUILD (
    where msbuild >nul 2>nul && set "MSBUILD=msbuild"
)

if not defined MSBUILD (
    echo [ERROR] MSBuild not found.
    echo         Install "Visual Studio Build Tools" with the
    echo         "Desktop development with C++" workload, then re-run this script
    echo         from a "x64 Native Tools Command Prompt for VS".
    exit /b 1
)

echo [--] MSBuild: %MSBUILD%

set "WVARG="
if defined WEBVIEW2_DIR (
    echo [--] WebView2: %WEBVIEW2_DIR%
    set "WVARG=/p:WebView2Dir=%WEBVIEW2_DIR%"
) else (
    echo [!!] WEBVIEW2_DIR not set - building with the plain-text fallback renderer.
    echo      Set it to get real Markdown rendering.
)

pushd "%~dp0"
"%MSBUILD%" MarkdownPreviewHandler.vcxproj /nologo /v:minimal ^
    /p:Configuration=Release /p:Platform=x64 %WVARG%
set "RC=%ERRORLEVEL%"
popd

if not "%RC%"=="0" (
    echo [ERROR] Build failed with %RC%.
    exit /b %RC%
)

echo.
echo [OK] Built native\..\bin\MarkdownPreviewNative.dll
echo      Install with an elevated PowerShell:  native\install_native.ps1
endlocal
