# Register the standalone Markdown viewer as a right-click verb (per-user).
#
#     powershell -ExecutionPolicy Bypass -File .\viewer\register_verb.ps1
#     powershell -ExecutionPolicy Bypass -File .\viewer\register_verb.ps1 -Uninstall
#
# Writes under HKCU\Software\Classes for each Markdown extension, and also under
# the ProgID that currently owns .md (WPS.MARKDOWN.md), because the Windows 11
# compact context menu resolves verbs from the owning ProgID.
#
# Two details that are easy to get wrong:
#   * the command value must contain the FULL path and an explicit "%1";
#     REG_SZ values are not expanded, so %LOCALAPPDATA% would stay literal
#   * the path must be quoted because it contains spaces

[CmdletBinding()]
param(
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$Root    = Split-Path -Parent $PSScriptRoot
$Install = Join-Path $env:LOCALAPPDATA 'MarkdownViewer'
$Exe     = Join-Path $Install 'MarkdownViewer.exe'
$Verb    = 'MarkdownViewer'
$Label   = '用 Markdown 查看器打开'

$Bases = @(
    'HKCU:\Software\Classes\.md',
    'HKCU:\Software\Classes\.markdown',
    'HKCU:\Software\Classes\.mdown',
    'HKCU:\Software\Classes\.mkd'
)

# .md is currently owned by this ProgID on this machine; add the verb there too
$ownerProgId = $null
try {
    $uc = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice' -ErrorAction SilentlyContinue
    if ($uc -and $uc.ProgId) {
        $ownerProgId = $uc.ProgId
        $Bases += "HKCU:\Software\Classes\$ownerProgId"
    }
} catch { }

if ($Uninstall) {
    foreach ($b in $Bases) {
        $k = Join-Path $b "shell\$Verb"
        if (Test-Path $k) { Remove-Item $k -Recurse -Force; Write-Host "  removed $k" }
    }
    Write-Host '[OK] viewer verb removed'
    return
}

if (-not (Test-Path $Exe)) {
    throw "Viewer not found: $Exe`nRun viewer\install_viewer.ps1 first (which also builds nothing - build with build_viewer.ps1)."
}

# The value we store. Note the explicit quotes and the literal %1.
$command = '"' + $Exe + '" "%1"'
Write-Host "command value: $command"

foreach ($b in $Bases) {
    $verbKey = Join-Path $b "shell\$Verb"
    New-Item -Force -Path $verbKey | Out-Null
    Set-ItemProperty -Path $verbKey -Name '(default)' -Value $Label -Type String
    # remove a stale Icon value (it was written with an unexpanded %LOCALAPPDATA%)
    Remove-ItemProperty -Path $verbKey -Name 'Icon' -ErrorAction SilentlyContinue

    $cmdKey = Join-Path $verbKey 'command'
    New-Item -Force -Path $cmdKey | Out-Null
    Set-ItemProperty -Path $cmdKey -Name '(default)' -Value $command -Type String

    Write-Host "  $b"
}

Write-Host ''
Write-Host '=== verification ===' -ForegroundColor Cyan
foreach ($b in $Bases) {
    $cmdKey = Join-Path $b "shell\$Verb\command"
    $v = (Get-ItemProperty $cmdKey -ErrorAction SilentlyContinue).'(default)'
    $lbl = (Get-ItemProperty (Join-Path $b "shell\$Verb") -ErrorAction SilentlyContinue).'(default)'
    $ok = ($v -eq $command) -and $lbl
    Write-Host ("  [{0}] {1}" -f $(if ($ok) { 'OK' } else { '!!' }), $b)
    Write-Host ("        label: {0}" -f $lbl)
    Write-Host ("        cmd  : {0}" -f $v)
}

Write-Host ''
Write-Host 'Restart Explorer if the entry does not show up:' -ForegroundColor Yellow
Write-Host '  Stop-Process -Name explorer -Force; Start-Process explorer'
Write-Host 'In Windows 11 the legacy verbs may also live under "显示更多选项" (Shift+F10).' -ForegroundColor Yellow
