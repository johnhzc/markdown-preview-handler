$r = @()
$clsid = "{3511EA64-B51A-4298-B6C3-6AB81F179E1C}"
$shellex = "{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}"

$r += "=== Did fix script run? Check its target keys ==="
$sfa = "HKLM:\SOFTWARE\Classes\SystemFileAssociations\.md\shellex\$shellex"
$r += "SystemFileAssociations\.md: $(Test-Path $sfa)"
if (Test-Path $sfa) { $r += "  value: $((Get-ItemProperty $sfa).'(default)')" }

foreach ($p in @("WorkBuddy.md", "Nutstore.LightApp.md")) {
    $k = "HKLM:\SOFTWARE\Classes\$p\shellex\$shellex"
    $r += "ProgID $p: $(Test-Path $k)"
}

$r += "=== UserChoice (was it cleared?) ==="
$uc = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice"
if (Test-Path $uc) {
    $r += "STILL EXISTS -> ProgId = $((Get-ItemProperty $uc).ProgId)"
} else {
    $r += "CLEARED (script step 4 ran)"
}

$r += "=== Explorer uptime (did it restart?) ==="
$exp = Get-Process explorer -ErrorAction SilentlyContinue | Select-Object -First 1
if ($exp) { $r += "started: $($exp.StartTime) | now: $(Get-Date)" }

$r += "=== Elevation of MY current process ==="
$r += "isAdmin: $(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"

Set-Content -Path 'D:\BuddyData\Tasks\markdown-preview-handler\diag_result20.txt' -Value $r -Encoding UTF8
