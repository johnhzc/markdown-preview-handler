// WebView2Interfaces.cpp
//
// Shared WebView2 bootstrap helpers.

#include "WebView2Interfaces.h"

// The module that is calling us; set by the executable/DLL at startup.
extern HMODULE g_hModule;

CreateCoreWebView2EnvironmentWithOptionsFn ResolveWebView2Loader()
{
    // 1) next to the calling module
    wchar_t self[MAX_PATH] = {};
    GetModuleFileNameW(g_hModule, self, MAX_PATH);
    std::wstring dir = self;
    size_t slash = dir.find_last_of(L"\\/");
    if (slash != std::wstring::npos) dir.erase(slash);

    HMODULE loader = LoadLibraryW((dir + L"\\WebView2Loader.dll").c_str());
    if (!loader) loader = LoadLibraryW(L"WebView2Loader.dll");

    // 2) the Edge WebView2 runtime install folder
    if (!loader)
    {
        wchar_t pf[MAX_PATH] = {};
        if (GetEnvironmentVariableW(L"ProgramFiles(x86)", pf, MAX_PATH))
        {
            std::wstring p = std::wstring(pf) +
                L"\\Microsoft\\EdgeWebView\\Application\\WebView2Loader.dll";
            loader = LoadLibraryW(p.c_str());
        }
    }
    if (!loader) return nullptr;

    return (CreateCoreWebView2EnvironmentWithOptionsFn)
        GetProcAddress(loader, "CreateCoreWebView2EnvironmentWithOptions");
}

bool PrepareWebView2UserDataFolder(std::wstring& out)
{
    wchar_t temp[MAX_PATH] = {};
    if (!GetTempPathW(MAX_PATH, temp)) return false;

    std::wstring base = std::wstring(temp) + L"MarkdownPreviewHandler";
    CreateDirectoryW(base.c_str(), nullptr);

    wchar_t udf[MAX_PATH] = {};
    swprintf_s(udf, L"%s\\udf_%lu", base.c_str(), GetCurrentProcessId());
    CreateDirectoryW(udf, nullptr);
    out = udf;
    return true;
}
