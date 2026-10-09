@echo off
setlocal
REM ============================================================
REM  Build script for MarkdownPreviewHandler.dll
REM  Zero-dependency build using the .NET Framework 4 compiler.
REM  Output: bin\MarkdownPreviewHandler.dll
REM ============================================================

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4 compiler not found. Install .NET Framework 4.x.
    exit /b 1
)

if not exist bin mkdir bin

"%CSC%" -nologo -codepage:65001 -target:library -platform:anycpu -optimize+ ^
    -keyfile:src\handler.snk ^
    -out:bin\MarkdownPreviewHandler.dll ^
    -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
    src\AssemblyInfo.cs ^
    src\ComInterfaces.cs ^
    src\FileTypes.cs ^
    src\Registration.cs ^
    src\PreviewLog.cs ^
    src\PreviewHandler.cs ^
    src\PreviewHost.cs ^
    src\ImagePreviewControl.cs ^
    src\MarkdownPreviewControl.cs ^
    src\MarkdownConverter.cs

if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo [OK] Built bin\MarkdownPreviewHandler.dll
echo      Install with an elevated PowerShell:  install.ps1
endlocal
