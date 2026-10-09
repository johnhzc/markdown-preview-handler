# One-shot verification of the Markdown preview handler.
#
#     powershell -ExecutionPolicy Bypass -File .\test\verify_preview.ps1
#
# It resets the log, checks registration, then waits while you test in Explorer
# and reports exactly how far the shell gets:
#   * "MODULE LOADED" missing      -> the shell never loads our code at all
#   * "MODULE LOADED" but no more  -> the shell loaded us, then gave up
#   * through "DoPreview"          -> the shell invoked us; any remaining
#                                     problem is on the rendering side

param(
    [string]$Probe = 'D:\BuddyData\Tasks\markdown-preview-handler\test\sample.md',
    [int]$WaitSeconds = 45
)

$Clsid  = '{3511EA64-B51A-4298-B6C3-6AB81F179E1C}'
$LogDir = Join-Path $env:LOCALAPPDATA 'MarkdownPreviewHandler'
$Log    = Join-Path $LogDir 'handler.log'

Write-Host '=== Markdown preview handler verification ===' -ForegroundColor Cyan

# 1. reset the log so any content afterwards is from NOW
if (Test-Path $LogDir) { Remove-Item $LogDir -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "[OK] cleared old logs ($LogDir)"

# 2. registration sanity
$ips   = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$Clsid\InprocServer32" -ErrorAction SilentlyContinue).'(default)'
$appid = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$Clsid" -ErrorAction SilentlyContinue).AppID
$gac   = "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\MarkdownPreviewHandler\v4.0_1.0.0.0__aad039b6d8722aba\MarkdownPreviewHandler.dll"
$gacLen = if (Test-Path $gac) { (Get-Item $gac).Length } else { 0 }
$ph    = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers' -ErrorAction SilentlyContinue).$Clsid

Write-Host ("  InprocServer32 : " + $ips)
Write-Host ("  AppID          : " + $appid)
Write-Host ("  PreviewHandlers: " + $(if ($ph) { $ph } else { '(missing)' }))
Write-Host ("  GAC copy       : " + $(if ($gacLen) { "$gacLen bytes" } else { '(missing)' }))
if ($gacLen -ne 46592 -and $gacLen -ne 0) {
    Write-Host ("  [!!] GAC copy is a DIFFERENT build ($gacLen bytes; the instrumented build is 46592)") -ForegroundColor Yellow
}
Write-Host ''

# 3. wait for the human to test
Write-Host 'Now, in Explorer:' -ForegroundColor Cyan
Write-Host "  1. press Alt+P to turn the preview pane on"
Write-Host "  2. click this file: $Probe"
Write-Host "  3. also click a .png and a .jpg in the same folder, to compare"
Write-Host ''
Write-Host "Waiting $WaitSeconds s ..." -ForegroundColor Cyan

$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $Log) {
        $n = (Get-Content $Log | Measure-Object).Count
        Write-Host "  log grew to $n line(s) ..." -ForegroundColor Green
        break
    }
}
# give it a few more seconds to accumulate
Start-Sleep -Seconds 3

Write-Host ''
Write-Host '=== handler log ===' -ForegroundColor Cyan
if (Test-Path $Log) {
    Get-Content $Log
    Write-Host ''
    $text = Get-Content $Log -Raw
    if ($text -match 'DoPreview') {
        Write-Host 'VERDICT: the shell invoked the handler -> remaining problem is on the rendering side.' -ForegroundColor Green
    } elseif ($text -match 'MODULE LOADED') {
        Write-Host 'VERDICT: the shell loaded our module but never reached DoPreview.' -ForegroundColor Yellow
    } else {
        Write-Host 'VERDICT: our code was never loaded.' -ForegroundColor Yellow
    }
} else {
    Write-Host '(no log file at all)' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'VERDICT: the shell never loaded our module, so the problem is still on the' -ForegroundColor Yellow
    Write-Host '         registration / activation side, not in the rendering code.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Also check the pane behaviour for .png / .jpg: if those render, the pane is'
    Write-Host 'fine and only the .md association is being ignored.'
}
