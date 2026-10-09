# Diagnostic: test whether SystemFileAssociations-based preview registration
# is picked up, and dump the exact .md resolution chain the shell would use.
$r = @()
$clsid = "{3511EA64-B51A-4298-B6C3-6AB81F179E1C}"
$shellex = "{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}"

$r += "=== 1. HKLM .md shellex (already present?) ==="
$k = "HKLM:\SOFTWARE\Classes\.md\shellex\$shellex"
$r += "exists: $(Test-Path $k)"

$r += "=== 2. SystemFileAssociations\.md ==="
$sfa = "HKLM:\SOFTWARE\Classes\SystemFileAssociations\.md"
$r += "base exists: $(Test-Path $sfa)"
$r += "shellex exists: $(Test-Path "$sfa\shellex")"
$r += "our handler: $(Test-Path "$sfa\shellex\$shellex")"

$r += "=== 3. How other extensions do it (jpg) ==="
$jpg = "HKLM:\SOFTWARE\Classes\.jpg\shellex"
$r += "jpg shellex subkeys: $((Get-Item $jpg -ErrorAction SilentlyContinue).SubKeyNames -join ', ')"
$jpgSfa = "HKLM:\SOFTWARE\Classes\SystemFileAssociations\.jpg\shellex"
$r += "jpg SFA shellex subkeys: $((Get-Item $jpgSfa -ErrorAction SilentlyContinue).SubKeyNames -join ', ')"
if (Test-Path $jpgSfa) { foreach ($s in (Get-Item $jpgSfa).SubKeyNames) { $r += "  $s = $((Get-ItemProperty "$jpgSfa\$s").'(default)')" } }

$r += "=== 4. Registry: is our handler disabled? ==="
$r += "Blocked: $(Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked\{3511EA64-B51A-4298-B6C3-6AB81F179E1C}')"

$r += "=== 5. Current PreviewHandlers entries ==="
Get-ChildItem "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers" | ForEach-Object {
    $r += "  $($_.PSChildName.Substring(0,38))... = $((Get-ItemProperty $_.PSPath).'(default)')"
}

Set-Content -Path 'D:\BuddyData\Tasks\markdown-preview-handler\diag_result18.txt' -Value $r -Encoding UTF8
