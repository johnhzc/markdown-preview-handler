# Markdown & Image Preview Handler - installer.
#
# Run from an ELEVATED PowerShell in the project folder:
#     .\install.ps1                 # install (registers, then restarts Explorer)
#     .\install.ps1 -NoRestart      # install without restarting Explorer
#     .\install.ps1 -Uninstall      # remove everything
#
# Registration logic lives inside the assembly (MarkdownPreview.Registration),
# so install.ps1 and fix-preview.ps1 never drift apart. It mirrors regasm
# output but fixes the details that decide whether Explorer invokes us:
#   * InprocServer32 = mscoree.dll + Assembly/RuntimeVersion (the .NET shim)
#   * AppID          = {6d2b5079-2f0b-48dd-ab7f-97cec514d30b} (prevhost)
#   * HKLM\...\CurrentVersion\PreviewHandlers\{CLSID}   <- required, otherwise
#                                                          "cannot preview"
#   * Shell Extensions\Approved\{CLSID}                 <- required on 24H2+
#   * <ext>\shellex\{8895b1c6-...} plus SystemFileAssociations fallback
#
# Note: the GAC is refreshed BEFORE the registration code is loaded, because
# PowerShell's assembly binding always prefers an existing GAC copy.

#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\MarkdownPreviewHandler",
    [switch]$NoRestart,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

if (-not [Environment]::Is64BitProcess) {
    throw 'Run this from 64-bit PowerShell, otherwise only the 32-bit registry view is written.'
}

$SrcDll  = Join-Path $PSScriptRoot 'bin\MarkdownPreviewHandler.dll'
$GacRoot = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\MarkdownPreviewHandler'

function Restart-Explorer {
    if ($NoRestart) { return }
    Write-Host '[..] Restarting explorer.exe'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        Start-Process explorer.exe
    }
    Write-Host '[OK] explorer restarted'
}

function Copy-ToGac([string]$dllPath) {
    $name  = [Reflection.AssemblyName]::GetAssemblyName($dllPath)
    $token = ($name.GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') }) -join ''
    if (-not $token) { throw "$dllPath is not strong-name signed; run build.cmd again." }
    $dir = Join-Path $GacRoot ("v4.0_{0}__{1}" -f $name.Version.ToString(), $token)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $dst = Join-Path $dir 'MarkdownPreviewHandler.dll'
    if (Test-Path $dst) {
        if ((Get-FileHash $dllPath).Hash -eq (Get-FileHash $dst).Hash) {
            Write-Host "[OK] GAC already up to date: $dst"
            return $dst
        }
    }
    Copy-Item $dllPath $dst -Force
    Write-Host "[OK] GAC refreshed: $dst"
    return $dst
}

if ($Uninstall) {
    $candidate = Join-Path $InstallDir 'MarkdownPreviewHandler.dll'
    if (-not (Test-Path $candidate)) { $candidate = $SrcDll }
    if (Test-Path $candidate) {
        $asm = [Reflection.Assembly]::LoadFrom($candidate)
        $reg = $asm.GetType('MarkdownPreview.Registration', $true)
        [void]$reg.GetMethod('Unregister').Invoke($null, @())
    } else {
        Write-Host '[!] No assembly available to unregister with; remove keys manually.'
    }
    Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $GacRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[OK] Removed $InstallDir and the GAC copy"
    Restart-Explorer
    return
}

if (-not (Test-Path $SrcDll)) {
    throw "Build output not found: $SrcDll - run build.cmd first."
}

# 1. Program Files copy
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $SrcDll $InstallDir -Force
$DllPath = Join-Path $InstallDir 'MarkdownPreviewHandler.dll'
Write-Host "[OK] Copied to $DllPath"

# 2. GAC first, then load the registration code from there
$gacDll = Copy-ToGac $DllPath
$asm = [Reflection.Assembly]::LoadFrom($gacDll)
$reg = $asm.GetType('MarkdownPreview.Registration', $true)
Write-Host ("[--] using registration code from: " + $asm.Location)

# 3. registry
$rc = [int]$reg.GetMethod('Register').Invoke($null, @())
if ($rc -ne 0) { throw "Registration failed with code $rc" }

Write-Host ''
Write-Host 'Tip: press Alt+P in Explorer to open the preview pane, then select a .md or image file.'
Write-Host "     Handler log: $env:LOCALAPPDATA\MarkdownPreviewHandler\handler.log"

Restart-Explorer
