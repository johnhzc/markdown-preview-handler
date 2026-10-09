# Markdown & Image Preview Handler - one-shot fix / registration.
#
# Usage (normal PowerShell is fine; the script self-elevates):
#     powershell -ExecutionPolicy Bypass -File .\fix-preview.ps1
#
# What it does:
#   1. Copies bin\MarkdownPreviewHandler.dll to Program Files
#   2. Seeds the .NET 4 GAC with that exact file
#      (the mscoree shim only resolves the assembly from the GAC / host dir,
#       and PowerShell's assembly binding always prefers the GAC copy, so the
#       GAC must be refreshed BEFORE the registration code is loaded --
#       otherwise you would invoke the stale registration logic from an old
#       GAC copy)
#   3. Runs the registration implementation that ships inside the assembly
#      (MarkdownPreview.Registration):
#        - CLSID\InprocServer32 = mscoree.dll + Assembly/RuntimeVersion
#        - AppID  = {6d2b5079-2f0b-48dd-ab7f-97cec514d30b} (prevhost)
#        - HKLM\...\CurrentVersion\PreviewHandlers\{CLSID}
#        - Shell Extensions\Approved\{CLSID}
#        - <ext>\shellex\{8895b1c6-...} + SystemFileAssociations fallback
#   4. Restarts explorer.exe
#
# Options:
#   -Unregister   remove registration and the install directory
#   -NoRestart    do not restart explorer.exe

[CmdletBinding()]
param(
    [switch]$Unregister,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'

$InstallDir = Join-Path $env:ProgramFiles 'MarkdownPreviewHandler'
$SrcDll     = Join-Path $PSScriptRoot 'bin\MarkdownPreviewHandler.dll'
$GacRoot    = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\MarkdownPreviewHandler'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

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

# ---------- self-elevate ----------
if (-not (Test-Admin)) {
    Write-Host '[..] Administrator rights required, elevating...'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Unregister) { $argList += '-Unregister' }
    if ($NoRestart) { $argList += '-NoRestart' }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs
    return
}

Write-Host '=== Markdown & Image Preview Handler ===' -ForegroundColor Cyan
Write-Host ("64-bit PowerShell: " + [Environment]::Is64BitProcess)

if (-not [Environment]::Is64BitProcess) {
    throw 'Run from 64-bit PowerShell, otherwise only the 32-bit registry view is written.'
}

function Get-GacDir([string]$dllPath) {
    $name = [Reflection.AssemblyName]::GetAssemblyName($dllPath)
    $token = ($name.GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') }) -join ''
    if (-not $token) { throw "$dllPath is not strong-name signed; run build.cmd again." }
    $version = $name.Version.ToString()
    return (Join-Path $GacRoot ("v4.0_{0}__{1}" -f $version, $token))
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
    try {
        Copy-Item $dllPath $dst -Force -ErrorAction Stop
    } catch {
        # prevhost.exe keeps the assembly loaded; it must be released first.
        Write-Host '[..] GAC copy is locked by prevhost.exe, stopping it and retrying'
        Get-Process prevhost -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
        Copy-Item $dllPath $dst -Force
    }
    Write-Host "[OK] GAC refreshed: $dst"
    return $dst
}

if ($Unregister) {
    $candidate = Join-Path $InstallDir 'MarkdownPreviewHandler.dll'
    if (-not (Test-Path $candidate)) { $candidate = $SrcDll }
    if (Test-Path $candidate) {
        $asm = [Reflection.Assembly]::LoadFrom($candidate)
        $reg = $asm.GetType('MarkdownPreview.Registration', $true)
        [void]$reg.GetMethod('Unregister').Invoke($null, @())
    }
    Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $GacRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[OK] Removed $InstallDir and the GAC copy"
    Restart-Explorer
    return
}

if (-not (Test-Path $SrcDll)) {
    throw "Not built yet: $SrcDll  (run build.cmd first)"
}

# ---------- 1. Program Files ----------
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $SrcDll $InstallDir -Force
$DllPath = Join-Path $InstallDir 'MarkdownPreviewHandler.dll'
Write-Host "[OK] Copied to $DllPath"

# ---------- 2. GAC (must happen before loading the registration code) ----------
$gacDll = Copy-ToGac $DllPath

# ---------- 3. registration inside the freshly deployed assembly ----------
$asm = [Reflection.Assembly]::LoadFrom($gacDll)
$reg = $asm.GetType('MarkdownPreview.Registration', $true)
if (-not $reg) {
    throw 'MarkdownPreview.Registration not found - the deployed assembly is stale.'
}
Write-Host ("[--] using registration code from: " + $asm.Location)

$rc = [int]$reg.GetMethod('Register').Invoke($null, @())
if ($rc -ne 0) { throw "Registration failed with code $rc" }

Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
Write-Host '  1. In Explorer press Alt+P to open the preview pane, select a .md file'
Write-Host '  2. Re-run .\test\check_preview.ps1 to confirm, and look at'
Write-Host "     $env:LOCALAPPDATA\MarkdownPreviewHandler\handler.log"
Write-Host '     - SetWindow/DoPreview entries  -> shell invoked us (rendering side)'
Write-Host '     - still no entries             -> shell never activated us (registration side)'

Restart-Explorer
