// DllMain.cpp
//
// Module entry point, GDI+ lifetime, and registration (DllRegisterServer /
// DllUnregisterServer) so the DLL can be installed with regsvr32.
//
// Registration writes exactly the keys this machine's working preview handlers
// use (verified against the Windows TXT Previewer and the Photos previewer):
//
//   HKLM\SOFTWARE\Classes\CLSID\{3511EA64-...}
//        (default)     = MarkdownPreview.NativeHandler
//        InprocServer32(default)     = <this dll>
//                      ThreadingModel = Apartment
//   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers
//        {3511EA64-...} = Markdown & Image Preview Handler (native)
//   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved
//        {3511EA64-...} = Markdown & Image Preview Handler (native)
//   HKLM\SOFTWARE\Classes\.md\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}
//        (default)     = {3511EA64-...}      <-- the SHELL's guid, not the IID

#include "MarkdownPreviewHandler.h"
#include <olectl.h>
#include <string>
#include <vector>

// Exports are declared in MarkdownPreviewNative.def (referenced by the vcxproj);
// do not also use /EXPORT pragmas here or the linker reports LNK1161.

static ULONG_PTR g_gdiplusToken = 0;
static HINSTANCE g_instance = nullptr;

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_instance = hinst;
        g_hModule = hinst;
        DisableThreadLibraryCalls(hinst);
        // Log first: if the shell loads us and then something fails, we want at
        // least this line on record.
        NativeLog(L"=== DllMain: PROCESS_ATTACH (module=%p) ===", hinst);
        {
            Gdiplus::GdiplusStartupInput input;
            Gdiplus::Status st = Gdiplus::GdiplusStartup(&g_gdiplusToken, &input, nullptr);
            NativeLog(L"DllMain: GdiplusStartup status=%d token=%p", (int)st, (void*)g_gdiplusToken);
        }
        break;

    case DLL_PROCESS_DETACH:
        NativeLog(L"DllMain: PROCESS_DETACH");
        if (g_gdiplusToken)
        {
            Gdiplus::GdiplusShutdown(g_gdiplusToken);
            g_gdiplusToken = 0;
        }
        break;
    }
    return TRUE;
}

// ---------------------------------------------------------------------------
// registration helpers
// ---------------------------------------------------------------------------

namespace {

const wchar_t* kClsid = L"{3511EA64-B51A-4298-B6C3-6AB81F179E1C}";
const wchar_t* kProgId = L"MarkdownPreview.NativeHandler";
const wchar_t* kDisplayName = L"Markdown & Image Preview Handler";
const wchar_t* kShellexGuid = L"{8895b1c6-b41f-4c1c-a562-0d564250836f}";

// Extensions we claim. Markdown is intentionally FIRST and images are optional;
// edit this list to taste.
const wchar_t* kExtensions[] = {
    L".md", L".markdown", L".mdown", L".mkd",
    L".jpg", L".jpeg", L".png", L".gif", L".bmp", L".tif", L".tiff", L".ico"
};

bool SetKeyValue(HKEY root, const std::wstring& subkey, const wchar_t* name, const std::wstring& value)
{
    HKEY key = nullptr;
    if (RegCreateKeyExW(root, subkey.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr) != ERROR_SUCCESS)
        return false;
    LONG r = RegSetValueExW(key, name, 0, REG_SZ,
        (const BYTE*)value.c_str(), (DWORD)((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return r == ERROR_SUCCESS;
}

bool DeleteKeyTree(HKEY root, const std::wstring& subkey)
{
    LONG r = RegDeleteTreeW(root, subkey.c_str());
    return r == ERROR_SUCCESS || r == ERROR_FILE_NOT_FOUND;
}

std::wstring ModulePath()
{
    wchar_t buf[MAX_PATH] = {};
    GetModuleFileNameW(g_hModule, buf, MAX_PATH);
    return buf;
}

} // namespace

STDAPI DllRegisterServer()
{
    std::wstring dllPath = ModulePath();

    // COM class
    std::wstring clsidKey = std::wstring(L"SOFTWARE\\Classes\\CLSID\\") + kClsid;
    SetKeyValue(HKEY_LOCAL_MACHINE, clsidKey, nullptr, kDisplayName);
    SetKeyValue(HKEY_LOCAL_MACHINE, clsidKey, L"DisplayName", kDisplayName);
    SetKeyValue(HKEY_LOCAL_MACHINE, clsidKey, L"AppID", L"");   // no surrogate
    SetKeyValue(HKEY_LOCAL_MACHINE, clsidKey + L"\\InprocServer32", nullptr, dllPath);
    SetKeyValue(HKEY_LOCAL_MACHINE, clsidKey + L"\\InprocServer32", L"ThreadingModel", L"Apartment");
    // The managed build used an AppID pointing at a DllSurrogate; a native
    // in-proc handler must NOT have one, or activation gets pushed out of proc.
    RegDeleteKeyW(HKEY_LOCAL_MACHINE, (clsidKey + L"\\AppID").c_str());

    SetKeyValue(HKEY_LOCAL_MACHINE, std::wstring(L"SOFTWARE\\Classes\\") + kProgId, nullptr, kDisplayName);
    SetKeyValue(HKEY_LOCAL_MACHINE, std::wstring(L"SOFTWARE\\Classes\\") + kProgId, L"FriendlyTypeName", kDisplayName);
    SetKeyValue(HKEY_LOCAL_MACHINE, std::wstring(L"SOFTWARE\\Classes\\") + kProgId, L"AppID", L"");
    SetKeyValue(HKEY_LOCAL_MACHINE, std::wstring(L"SOFTWARE\\Classes\\") + kProgId + L"\\CLSID", nullptr, kClsid);

    // Preview handler lists
    SetKeyValue(HKEY_LOCAL_MACHINE,
        L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\PreviewHandlers", kClsid, kDisplayName);
    SetKeyValue(HKEY_LOCAL_MACHINE,
        L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved", kClsid, kDisplayName);

    // Extensions
    for (const wchar_t* ext : kExtensions)
    {
        std::wstring base = std::wstring(L"SOFTWARE\\Classes\\") + ext;
        SetKeyValue(HKEY_LOCAL_MACHINE, base + L"\\shellex\\" + kShellexGuid, nullptr, kClsid);
        SetKeyValue(HKEY_LOCAL_MACHINE,
            std::wstring(L"SOFTWARE\\Classes\\SystemFileAssociations\\") + ext +
            L"\\shellex\\" + kShellexGuid, nullptr, kClsid);
    }

    return S_OK;
}

STDAPI DllUnregisterServer()
{
    std::wstring clsidKey = std::wstring(L"SOFTWARE\\Classes\\CLSID\\") + kClsid;
    DeleteKeyTree(HKEY_LOCAL_MACHINE, clsidKey);
    DeleteKeyTree(HKEY_LOCAL_MACHINE, std::wstring(L"SOFTWARE\\Classes\\") + kProgId);

    RegDeleteKeyW(HKEY_LOCAL_MACHINE,
        (std::wstring(L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\PreviewHandlers\\") + kClsid).c_str());

    HKEY approved = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
            L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Approved",
            0, KEY_WRITE, &approved) == ERROR_SUCCESS)
    {
        RegDeleteValueW(approved, kClsid);
        RegCloseKey(approved);
    }

    for (const wchar_t* ext : kExtensions)
    {
        DeleteKeyTree(HKEY_LOCAL_MACHINE,
            std::wstring(L"SOFTWARE\\Classes\\") + ext + L"\\shellex\\" + kShellexGuid);
        DeleteKeyTree(HKEY_LOCAL_MACHINE,
            std::wstring(L"SOFTWARE\\Classes\\SystemFileAssociations\\") + ext +
            L"\\shellex\\" + kShellexGuid);
    }
    return S_OK;
}
