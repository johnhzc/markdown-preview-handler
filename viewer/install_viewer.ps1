# Register the standalone Markdown viewer as a right-click verb.
#
#   Normal PowerShell (per-user, no admin needed):
#       powershell -ExecutionPolicy Bypass -File .\viewer\install_viewer.ps1
#
#   Elevated PowerShell (per-machine, visible to all users):
#       powershell -ExecutionPolicy Bypass -File .\viewer\install_viewer.ps1 -Machine
#
#   Remove again:
#       powershell -ExecutionPolicy Bypass -File .\viewer\install_viewer.ps1 -Uninstall
#
# What it adds
#   .md/.markdown/.mdown/.mkd  ->  "用 Markdown 查看器打开"  (right-click)
#   a directory background verb -> "新建/查看 Markdown"?   (not added; use SendTo)
#   SendTo shortcut             -> "%APPDATA%\Microsoft\Windows\SendTo" entry, so
#                                  you can also use Send To -> Markdown 查看器
#
# The viewer itself ignores Explorer's preview machinery completely: it is a
# normal GUI application that renders the file with WebView2.

[CmdletBinding()]
param(
    [switch]$Machine,
    [switch]$Uninstall,
    [switch]$NoSendTo
)

$ErrorActionPreference = 'Stop'

$Root      = Split-Path -Parent $PSScriptRoot
$ExeName   = 'MarkdownViewer.exe'
$BuildExe  = Join-Path $Root "bin\$ExeName"
$InstallDir = if ($Machine) { "$env:ProgramFiles\MarkdownViewer" } else { Join-Path $env:LOCALAPPDATA 'MarkdownViewer' }
$ExePath   = Join-Path $InstallDir $ExeName
$VerbName  = 'MarkdownViewer'
$VerbLabel = '用 Markdown 查看器打开'
$Extensions = @('.md', '.markdown', '.mdown', '.mkd')

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($Machine -and -not (Test-Admin)) {
    Write-Host '-Machine needs an elevated PowerShell.' -ForegroundColor Yellow
    return
}

# ------------------------------------------------------------------ uninstall
if ($Uninstall) {
    Write-Host '=== removing the viewer verb ===' -ForegroundColor Cyan

    foreach ($ext in $Extensions) {
        foreach ($hive in @('HKCU:\Software\Classes', 'HKLM:\SOFTWARE\Classes')) {
            $key = Join-Path $hive "$ext\shell\$VerbName"
            if (Test-Path $key) {
                Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue
                Write-Host "  removed $key"
            }
        }
    }

    $sendTo = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\SendTo\Markdown 查看器.lnk'
    if (Test-Path $sendTo) { Remove-Item $sendTo -Force; Write-Host "  removed $sendTo" }

    foreach ($dir in @((Join-Path $env:LOCALAPPDATA 'MarkdownViewer'), "$env:ProgramFiles\MarkdownViewer")) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "  removed $dir" }
    }
    return
}

# -------------------------------------------------------------------- install
if (-not (Test-Path $BuildExe)) {
    throw "Viewer not built: $BuildExe`nBuild it first:  powershell -ExecutionPolicy Bypass -File .\viewer\build_viewer.ps1"
}

Write-Host '=== deploying the viewer ===' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $BuildExe $InstallDir -Force
Write-Host "  [OK] $ExePath"

# WebView2Loader.dll must sit next to the exe
$loaderSrc = Join-Path $Root 'bin\WebView2Loader.dll'
if (Test-Path $loaderSrc) {
    Copy-Item $loaderSrc $InstallDir -Force
    Write-Host "  [OK] $(Join-Path $InstallDir 'WebView2Loader.dll')"
} else {
    Write-Host '  [!!] bin\WebView2Loader.dll not found - the viewer may fail to render.' -ForegroundColor Yellow
}

# right-click verb for each extension
$hive = if ($Machine) { 'HKLM:\SOFTWARE\Classes' } else { 'HKCU:\Software\Classes' }
Write-Host "=== registering the verb in $hive ===" -ForegroundColor Cyan

foreach ($ext in $Extensions) {
    $base = Join-Path $hive $ext
    New-Item -Force -Path $base | Out-Null

    $verb = Join-Path $base "shell\$VerbName"
    New-Item -Force -Path $verb | Out-Null
    Set-ItemProperty -Path $verb -Name '(default)' -Value $VerbLabel
    New-ItemProperty -Path $verb -Name 'Icon' -Value "`"$ExePath`"" -PropertyType String -Force | Out-Null

    $cmd = Join-Path $verb 'command'
    New-Item -Force -Path $cmd | Out-Null
    Set-ItemProperty -Path $cmd -Name '(default)' -Value "`"$ExePath`" `"%1`""
    Write-Host "  $ext -> $VerbLabel"
}

# "Send To" shortcut as a second, always-available route
if (-not $NoSendTo) {
    Write-Host '=== adding a Send To shortcut ===' -ForegroundColor Cyan
    $sendToDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Windows\SendTo'
    New-Item -ItemType Directory -Force -Path $sendToDir | Out-Null
    $lnk = Join-Path $sendToDir 'Markdown 查看器.lnk'
    try {
        $ws = New-Object -ComObject WScript.Shell
        $sc = $ws.CreateShortcut($lnk)
        $sc.TargetPath = $ExePath
        $sc.WorkingDirectory = $InstallDir
        $sc.Description = 'Markdown 查看器'
        $sc.Save()
        Write-Host "  [OK] $lnk"
    } catch {
        Write-Host "  [!!] shortcut creation failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host 'Done. Right-click any .md file -> "用 Markdown 查看器打开".' -ForegroundColor Green
Write-Host 'Or drag a file onto the viewer, or use Send To -> Markdown 查看器.' -ForegroundColor Green
Write-Host ''
Write-Host 'Note: Explorer may cache the context menu; restart it if the entry does not appear:'
Write-Host '  Stop-Process -Name explorer -Force; Start-Process explorer'
