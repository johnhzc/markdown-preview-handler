# Markdown 纯文本预览（免管理员，每用户注册）
#
#     powershell -ExecutionPolicy Bypass -File .\install_text_preview.ps1
#     powershell -ExecutionPolicy Bypass -File .\install_text_preview.ps1 -Remove
#
# 背景（本机实测结论，2026-10）：
#   在 Windows 11 25H2（build 26200）上，第三方托管预览处理器（IPreviewHandler）
#   无法在预览窗格里被调用，即使注册完全正确：
#     - CLSID / InprocServer32 = mscoree.dll / Assembly / RuntimeVersion / AppID 全部就位
#     - 已列入 HKLM\...\CurrentVersion\PreviewHandlers
#     - 已在 Shell Extensions\Approved 白名单
#     - shellex\{8895b1c6-...} 在 HKLM、HKCU、SystemFileAssociations 三处都注册
#     - AssocQueryString(ASSOCSTR_SHELLEXTENSION, ".md", IID_IPreviewHandler) 确认返回本 CLSID
#     - 程序集确实在 GAC 中，且用 CoCreateInstance 激活后 SetWindow/DoPreview 全部成功
#   资源管理器仍然不会加载它（handler.log 一行都没有，prevhost 里也从未出现 mscoree）。
#   且 .md 的关联被“应用商店版记事本”这个 AppX ProgID 占用（UserChoice 受 Hash 保护，
#   非 GUI 方式改不了）。
#
#   相比之下，资源管理器**会**使用 Plain Text 持久处理器（.txt/.cs/.js/.sql 都是它）。
#   所以这里把 .md 也接到同一个持久处理器上，预览窗格即显示 Markdown 源码文本。
#
# 本脚本只写 HKCU，不需要管理员权限，-Remove 可完全撤销。

[CmdletBinding()]
param(
    [switch]$Remove,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'

# "Plain Text persistent handler"（.txt / .cs / .js / .sql 在用的同一个）
$TxtPersistent = '{5e941d80-bf96-11cd-b579-08002b30bfeb}'
$Exts = @('.md', '.markdown', '.mdown', '.mkd')

function Restart-Explorer {
    if ($NoRestart) { return }
    Write-Host '[..] 重启 explorer.exe'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1500
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
    Write-Host '[OK] explorer 已重启'
}

Write-Host '=== Markdown 纯文本预览 ===' -ForegroundColor Cyan

foreach ($ext in $Exts) {
    $extKey = "HKCU:\Software\Classes\$ext"
    $phKey  = "$extKey\PersistentHandler"

    if ($Remove) {
        Remove-Item -Path $phKey -Recurse -Force -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $extKey -Name 'PerceivedType' -ErrorAction SilentlyContinue
        Write-Host "[OK] $ext 已撤销纯文本预览注册"
        continue
    }

    New-Item -Force -Path $extKey | Out-Null
    New-Item -Force -Path $phKey | Out-Null
    Set-ItemProperty -Path $phKey -Name '(default)' -Value $TxtPersistent
    New-ItemProperty -Path $extKey -Name 'PerceivedType' -Value 'text' -PropertyType String -Force | Out-Null

    $v = (Get-ItemProperty $phKey).'(default)'
    Write-Host "[OK] $ext -> PersistentHandler $v  (PerceivedType=text)"
}

if (-not $Remove) {
    Write-Host ''
    Write-Host '现在在资源管理器里选中 .md 文件（预览窗格 Alt+P），应能直接看到 Markdown 源码。'
    Write-Host '撤销：  .\install_text_preview.ps1 -Remove'
}
Restart-Explorer
