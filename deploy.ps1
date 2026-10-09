#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Rebuild-deploy: refresh the GAC copy with the freshly built DLL and
    make sure all COM registration values are current.
.DESCRIPTION
    Run after build.cmd whenever the source changes. This keeps the GAC
    assembly (which the mscoree shim actually loads) in sync with bin\.
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\MarkdownPreviewHandler",
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"

$Clsid      = "{3511EA64-B51A-4298-B6C3-6AB81F179E1C}"
$ShellexKey = "{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}"
$Token      = "aad039b6d8722aba"
$GacDir     = "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\MarkdownPreviewHandler\v4.0_1.0.0.0__$Token"

$srcDll = Join-Path $PSScriptRoot "bin\MarkdownPreviewHandler.dll"
if (-not (Test-Path $srcDll)) { throw "Not built yet: $srcDll" }

# 1. Refresh Program Files copy
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $srcDll $InstallDir -Force

# 2. Refresh GAC copy (this is what actually gets loaded)
New-Item -ItemType Directory -Force -Path $GacDir | Out-Null
Copy-Item $srcDll $GacDir -Force

# 3. Keep threading model current (Both = correct for .NET COM + ActiveX children)
$ips = "HKLM:\SOFTWARE\Classes\CLSID\$Clsid\InprocServer32"
New-ItemProperty -Path $ips -Name "ThreadingModel" -Value "Both" -Force | Out-Null

# 3b. CRITICAL: approve the shell extension - without this entry Explorer
#     silently ignores the preview handler on Win11 24H2+.
$approved = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"
New-Item -Force -Path $approved | Out-Null
New-ItemProperty -Path $approved -Name $Clsid -Value "Markdown & Image Preview Handler" -PropertyType String -Force | Out-Null

# 4. Verify the shell can resolve the preview handler for .md
$verify = @()
foreach ($ext in @(".md", ".markdown")) {
    $k = "HKLM:\SOFTWARE\Classes\$ext\shellex\$ShellexKey"
    New-Item -Force -Path $k | Out-Null
    Set-ItemProperty -Path $k -Name "(default)" -Value $Clsid
    $verify += "  $ext -> $((Get-ItemProperty $k).'(default)')"
}

Write-Host "[OK] Deployed:"
Write-Host "     Program Files : $InstallDir"
Write-Host "     GAC           : $GacDir"
Write-Host "     ThreadingModel: Both"
Write-Host "Extensions:"
$verify | ForEach-Object { Write-Host $_ }

if ($NoRestart) { return }
Write-Host ""
Read-Host "Press Enter to restart Explorer and activate" | Out-Null
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 1500
if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
Write-Host "[OK] Explorer restarted."
