@echo off
setlocal
REM ============================================================
REM  Render test: convert test\sample.md to HTML via the
REM  MarkdownConverter inside MarkdownPreviewHandler.dll.
REM  Output: test\preview_render_test.html
REM ============================================================

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4 compiler not found.
    exit /b 1
)

"%CSC%" -nologo -codepage:65001 ^
    -out:bin\RenderTest.exe ^
    -r:bin\MarkdownPreviewHandler.dll ^
    -r:System.dll -r:System.Core.dll ^
    test\RenderTest.cs

if errorlevel 1 (
    echo [ERROR] Test harness build failed.
    exit /b 1
)

bin\RenderTest.exe test\sample.md
endlocal
