# Markdown & Image Preview Handler - uninstaller.
#
# Kept as a thin wrapper so there is a single implementation:
#     .\uninstall.ps1              # same as  .\install.ps1 -Uninstall
#     .\uninstall.ps1 -NoRestart   # do not restart Explorer
#
# (The old inline version removed only the PreviewHandlers key and the CLSID
#  key, leaving the GAC copy and the extension associations behind.)

#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [switch]$NoRestart
)

$installer = Join-Path $PSScriptRoot 'install.ps1'
if (-not (Test-Path $installer)) {
    throw "install.ps1 not found next to this script: $installer"
}

$params = @{ Uninstall = $true }
if ($NoRestart) { $params['NoRestart'] = $true }

& $installer @params
