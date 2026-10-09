import winreg

clsid = "{3511EA64-B51A-4298-B6C3-6AB81F179E1C}"
base = "Software\\Classes\\CLSID\\" + clsid
codebase = "file:///D:/BuddyData/Tasks/markdown-preview-handler/test/nospathtest/MarkdownPreviewHandler.dll"
asm = "MarkdownPreviewHandler, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"

for sub in ("", "\\1.0.0.0"):
    k = winreg.CreateKey(winreg.HKEY_CURRENT_USER, base + sub)
    winreg.SetValueEx(k, None, 0, winreg.REG_SZ, "mscoree.dll")
    winreg.SetValueEx(k, "ThreadingModel", 0, winreg.REG_SZ, "Apartment")
    winreg.SetValueEx(k, "Class", 0, winreg.REG_SZ, "MarkdownPreview.PreviewHandler")
    winreg.SetValueEx(k, "Assembly", 0, winreg.REG_SZ, asm)
    winreg.SetValueEx(k, "RuntimeVersion", 0, winreg.REG_SZ, "v4.0.30319")
    winreg.SetValueEx(k, "CodeBase", 0, winreg.REG_SZ, codebase)
    k.Close()

print("HKCU override written: CodeBase -> no-space path")
