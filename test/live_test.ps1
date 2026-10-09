# Live test for the Markdown preview handler.
#
#     powershell -ExecutionPolicy Bypass -File .\test\live_test.ps1
#
# It clears the log, watches in real time, and records:
#   * when prevhost.exe starts (and with which CLSID argument)
#   * whether our handler writes its log, and how far it gets
#   * the preview-pane window state before/after
#
# Just follow the on-screen instructions while it runs.

param(
    [string]$Folder = 'D:\BuddyData\Tasks\markdown-preview-handler\test',
    [string]$MarkdownFile = 'sample.md',
    [int]$Seconds = 60
)

$ClsidOurs = '3511EA64'
$LogDir = Join-Path $env:LOCALAPPDATA 'MarkdownPreviewHandler'
$Log    = Join-Path $LogDir 'handler.log'

function Get-PrevhostInfo {
    $out = @()
    foreach ($p in @(Get-Process prevhost -ErrorAction SilentlyContinue)) {
        $cmd = ''
        try {
            $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)" -ErrorAction SilentlyContinue).CommandLine
        } catch { }
        $out += [pscustomobject]@{ Pid = $p.Id; Path = $p.Path; CommandLine = $cmd }
    }
    return $out
}

Write-Host '=== Live preview-handler test ===' -ForegroundColor Cyan
if (Test-Path $LogDir) { Remove-Item $LogDir -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "[OK] log cleared"

$before = @(Get-PrevhostInfo)
Write-Host ("[--] prevhost before: " + $(if ($before.Count) { ($before | ForEach-Object { $_.Pid }) -join ', ' } else { 'none' }))
Write-Host ''
Write-Host 'DO THIS NOW (you have ' -NoNewline -ForegroundColor Yellow
Write-Host "$Seconds seconds):" -ForegroundColor Yellow
Write-Host '  1. open File Explorer and go to:'
Write-Host "     $Folder"
Write-Host '  2. make sure the preview pane is on (Alt+P); it should be a wide panel on the right'
Write-Host "  3. click ONCE on  $MarkdownFile   and wait ~3s"
Write-Host '  4. then click ONCE on a .png in the same folder and wait ~3s'
Write-Host '  5. then click ONCE on a .cs file, to see whether the pane works at all'
Write-Host ''
Write-Host 'Watching...' -ForegroundColor Cyan

$seenPrevhost = @{}
$logSeen = $false
$deadline = (Get-Date).AddSeconds($Seconds)
$t0 = Get-Date

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 300
    $el = [int]((Get-Date) - $t0).TotalSeconds

    foreach ($p in @(Get-PrevhostInfo)) {
        if (-not $seenPrevhost.ContainsKey($p.Pid)) {
            $seenPrevhost[$p.Pid] = $true
            $isOurs = $p.CommandLine -match $ClsidOurs
            $color = if ($isOurs) { 'Green' } else { 'DarkGray' }
            Write-Host ("  [t+{0,2}s] prevhost pid={1} {2}" -f $el, $p.Pid,
                $(if ($isOurs) { '<== OUR CLSID' } else { ($p.CommandLine -replace '.*prevhost\.exe ', '') })) -ForegroundColor $color
        }
    }

    if (-not $logSeen -and (Test-Path $Log)) {
        $logSeen = $true
        Write-Host ("  [t+{0,2}s] *** handler.log appeared - our code is being loaded ***" -f $el) -ForegroundColor Green
    }
}

Write-Host ''
Write-Host '=== handler log ===' -ForegroundColor Cyan
if (Test-Path $Log) {
    Get-Content $Log
} else {
    Write-Host '(nothing - our module was never loaded during this test)' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '=== prevhost processes at the end ===' -ForegroundColor Cyan
foreach ($p in @(Get-PrevhostInfo)) {
    Write-Host ("  pid={0}  {1}" -f $p.Pid, $p.CommandLine)
    Write-Host ("       {0}" -f $p.Path)
}

Write-Host ''
Write-Host 'Please also tell me what the pane showed for the .md, the .png and the .cs file.' -ForegroundColor Cyan
