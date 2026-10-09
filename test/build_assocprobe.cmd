@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" -nologo -codepage:65001 -platform:x64 -out:bin\AssocProbe.exe test\AssocProbe.cs
if errorlevel 1 ( echo [ERROR] build failed & exit /b 1 )
echo [OK] AssocProbe built.
endlocal
