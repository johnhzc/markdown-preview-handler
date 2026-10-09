# -*- coding: utf-8 -*-
"""
Determines, WITHOUT a human at the PC, whether Explorer will now select our
Markdown preview handler for .md files.

Method: call the SAME Win32 API the shell uses to resolve a preview handler:
    shlwapi!AssocQueryString(ASSOCSTR_SHELLEXTENSION, ext, IID_IPreviewHandler)
This walks UserChoice -> ProgID -> shellex exactly like Explorer/prevhost.
If it returns our CLSID, Explorer WILL invoke our handler.
"""
import ctypes
from ctypes import wintypes

shlwapi = ctypes.windll.shlwapi

ASSOCF_NONE = 0
ASSOCSTR_SHELLEXTENSION = 16
IID_IPreviewHandler = "{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}"
OUR_CLSID = "{3511EA64-B51A-4298-B6C3-6AB81F179E1C}"


def assoc_query_string(str_, assoc, extra=None):
    buf = ctypes.create_unicode_buffer(1024)
    length = wintypes.DWORD(1024)
    hr = shlwapi.AssocQueryStringW(
        ASSOCF_NONE, str_, assoc, extra, buf, ctypes.byref(length))
    if hr != 0:
        return "(hr=0x%08X)" % hr
    return buf.value


def reg_read(path, name="(Default)"):
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as k:
            return winreg.QueryValueEx(k, name)[0]
    except Exception as e:
        return "(absent: %s)" % e


print("=== Explorer-equivalent preview-handler resolution ===")
print("process bits :", "64" if ctypes.sizeof(ctypes.c_void_p) == 8 else "32")
print()

exts = [".md", ".markdown", ".mdown", ".mkd", ".jpg", ".png"]
all_ok = True
for ext in exts:
    resolved = assoc_query_string(ASSOCSTR_SHELLEXTENSION, ext, IID_IPreviewHandler)
    status = "OK -> ours" if resolved.upper() == OUR_CLSID.upper() else "NOT ours"
    if ext in (".md", ".markdown", ".mdown", ".mkd") and resolved.upper() != OUR_CLSID.upper():
        all_ok = False
    print("  %-9s preview CLSID = %-42s [%s]" % (ext, resolved, status))

print()
print("=== Cross-check: registry key we just wrote ===")
appx = r"Software\Classes\AppXkv2jqn1pq8ajm0p5dhgqde7aafykkrrn\shellex\{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}"
print("  HKCU\\" + appx + " =", reg_read(appx))

print()
if all_ok:
    print("RESULT: .md family resolves to OUR handler. Explorer will now invoke it.")
else:
    print("RESULT: .md still resolves elsewhere -- fix not effective.")
