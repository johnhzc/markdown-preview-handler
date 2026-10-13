# Restore the Windows preview-handler whitelists that were wiped by mistake.
#
#   Run from an ADMIN PowerShell:
#       powershell -ExecutionPolicy Bypass -File .\restore_preview_handlers.ps1
#
# Background: an earlier elevated cleanup deleted the *contents* of
#   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers
#   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved
# leaving only the Markdown handler entry. The shell only surfaces preview
# handlers that are listed there, which broke previews for .txt / .cs / .pdf
# and everything else that used to work.
#
# The list below is the set captured from this machine before the damage
# (plus the two per-user WPS entries). Entries that are no longer installed
# are harmless: the shell simply skips CLSIDs it cannot load.

#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'

$PreviewHandlersKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers'
$ApprovedKey        = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved'
$Wsa64Key           = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\PreviewHandlers'

# CLSID -> display name, exactly as observed before the damage
$handlers = [ordered]@{
    '{13D3C4B8-B179-4ebb-BF62-F704173E7448}' = 'Windows Contact Preview Handler'
    '{1531d583-8375-4d3f-b5fb-d23bbd169f22}' = 'Windows TXT Previewer'
    '{53BEDF0B-4E5B-4183-8DC9-B844344FA104}' = 'Microsoft Windows MAPI Preview Handler'
    '{8a7cae0e-5951-49cb-bf20-ab3fa1e44b01}' = 'Windows Font previewer'
    '{92dbad9f-5025-49b0-9078-2d78f935e341}' = 'Microsoft Windows Mail Mime Preview Handler'
    '{a42c2ccb-67d3-46fa-abe6-7d2f3488c7a3}' = 'Windows RTF Previewer'
    '{b9815375-5d7f-4ce2-9245-c9d4da436930}' = 'Microsoft Windows Mail Mime Preview Handler'
    '{BFD468D2-D0A0-4bdc-878C-E69C2F5B435D}' = 'Microsoft Windows Mail Html Preview Handler'
    '{E64164EB-1AE0-4C50-BAEF-A413C2B3A4BC}' = 'Microsoft 3MF Shell Thumbnail and Preview Handler'
    '{f8b8412b-dea3-4130-b36c-5e8be73106ac}' = 'Microsoft Windows Mail Html Preview Handler'
    '{031EE060-67BC-460d-8847-E4A7C5E45A27}' = 'Windows Media Player Rich Preview Handler'
    '{3A84F9C2-6164-485C-A7D9-4B27F8AC009E}' = 'Microsoft PDF Previewer'
    '{0C7FEF07-DCD9-4120-9647-D1CE32F289CD}' = 'WPS文字 预览器'
    '{A1BBCFD9-B54C-443D-BC56-0BC3840120DB}' = 'WPS演示 预览器'
    '{E260F96C-8EF4-4C24-A2B9-455F1D116531}' = 'WPS表格 预览器'
    '{d7ac9243-9d84-4498-8192-d7ad263b63c8}' = 'Preview Handler Class'
    '{A3DA03D4-2B35-4072-83DC-5E5631AF019D}' = 'ArchivePreviewHandler'
    '{DDB16B7D-1D9D-441C-8B52-D3435C5FAEF1}' = 'Adobe Illustrator Preview Handler'
    # our own handler must stay listed
    '{3511EA64-B51A-4298-B6C3-6AB81F179E1C}' = 'Markdown & Image Preview Handler'
}

Write-Host '=== restoring HKLM PreviewHandlers ===' -ForegroundColor Cyan
New-Item -Force -Path $PreviewHandlersKey | Out-Null
New-Item -Force -Path $Wsa64Key | Out-Null

$added = 0
foreach ($clsid in $handlers.Keys) {
    $name = $handlers[$clsid]
    if (-not (Get-ItemProperty $PreviewHandlersKey -ErrorAction SilentlyContinue).$clsid) {
        New-ItemProperty -Path $PreviewHandlersKey -Name $clsid -Value $name -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $Wsa64Key        -Name $clsid -Value $name -PropertyType String -Force | Out-Null
        Write-Host ("  + {0}  {1}" -f $clsid, $name)
        $added++
    }
}
Write-Host ("  added $added entries")

# ------------------------------------------------------------------ approved
Write-Host '=== restoring Shell Extensions\Approved (preview handlers) ===' -ForegroundColor Cyan
New-Item -Force -Path $ApprovedKey | Out-Null
$addedA = 0
foreach ($clsid in $handlers.Keys) {
    if (-not (Get-ItemProperty $ApprovedKey -ErrorAction SilentlyContinue).$clsid) {
        New-ItemProperty -Path $ApprovedKey -Name $clsid -Value $handlers[$clsid] -PropertyType String -Force | Out-Null
        $addedA++
    }
}
Write-Host ("  added $addedA entries")

# ------------------------------------------------------------------- verify
Write-Host ''
Write-Host '=== verification ===' -ForegroundColor Cyan
foreach ($check in @(
        @{ n = 'Windows TXT Previewer'; c = '{1531d583-8375-4d3f-b5fb-d23bbd169f22}' },
        @{ n = 'Microsoft PDF Previewer'; c = '{3A84F9C2-6164-485C-A7D9-4B27F8AC009E}' },
        @{ n = 'Markdown handler'; c = '{3511EA64-B51A-4298-B6C3-6AB81F179E1C}' })) {
    $v = (Get-ItemProperty $PreviewHandlersKey -ErrorAction SilentlyContinue).($check.c)
    $ok = [bool]$v
    Write-Host ("  [{0}] {1,-32} {2}" -f $(if ($ok) { 'OK' } else { '!!' }), $check.n, $v)
}

if (-not $NoRestart) {
    Write-Host '[..] restarting explorer.exe'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1500
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
    Write-Host '[OK] explorer restarted'
}

Write-Host ''
Write-Host 'Now previews for .txt / .cs / .pdf / images should work again.' -ForegroundColor Green
Write-Host 'Please verify, then tell me so I can continue with the standalone viewer.' -ForegroundColor Green
