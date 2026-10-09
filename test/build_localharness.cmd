@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" -nologo -codepage:65001 -platform:x64 -out:bin\LocalHarness.exe ^
    -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
    test\LocalHarness.cs
if errorlevel 1 ( echo [ERROR] build failed & exit /b 1 )
echo [OK] LocalHarness built.
endlocal
