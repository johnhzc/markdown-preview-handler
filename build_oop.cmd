@echo off
setlocal
REM ============================================================
REM  Build the OUT-OF-PROCESS preview host: MarkdownPreviewHost.exe
REM
REM  Unlike build.cmd (which builds the InprocServer32 DLL), this
REM  produces a standalone EXE registered as LocalServer32, so
REM  Explorer launches it directly via CoCreateInstance instead of
REM  loading it through mscoree + prevhost.
REM
REM  Output: bin\MarkdownPreviewHost.exe
REM ============================================================

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4 compiler not found.
    exit /b 1
)

if not exist bin mkdir bin

"%CSC%" -nologo -codepage:65001 -target:exe -platform:x64 -optimize+ ^
    -main:MarkdownPreview.Program ^
    -keyfile:src\handler.snk ^
    -out:bin\MarkdownPreviewHost.exe ^
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
    src\MarkdownConverter.cs ^
    src_host\Program.cs ^
    src_host\SelfTest.cs

if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo [OK] Built bin\MarkdownPreviewHost.exe
echo      Register with an elevated PowerShell:  install_oop.ps1
endlocal
