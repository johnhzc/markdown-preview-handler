// StandaloneViewer.cpp
//
// A standalone Markdown viewer: renders a .md file with WebView2 in its own
// window, with no dependency on Explorer's preview pane.
//
//   MarkdownViewer.exe <file.md>
//   MarkdownViewer.exe            (opens a file picker)
//
// Registered as a right-click verb ("用 Markdown 查看器打开") by
// viewer\install_viewer.ps1, or simply drop it somewhere and pass a path.
//
// Reuses the same Markdown -> HTML converter and WebView2 bootstrap as the
// preview handler in ..\src.

#include "WebView2Interfaces.h"

#include <windows.h>
#include <commdlg.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <string>
#include <vector>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "comdlg32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "shell32.lib")

HMODULE g_hModule = nullptr;

namespace {

const wchar_t* kWindowClass = L"MarkdownViewerWindow";
const wchar_t* kWindowTitle = L"Markdown 查看器";

std::wstring g_filePath;
HWND         g_hwnd = nullptr;
HWND         g_child = nullptr;
ICoreWebView2Controller* g_controller = nullptr;
ICoreWebView2Environment* g_environment = nullptr;
bool         g_ready = false;

// --------------------------------------------------------------------------
// logging (same location as the handler, different file)
// --------------------------------------------------------------------------

void Log(const wchar_t* fmt, ...)
{
    static std::wstring path;
    if (path.empty())
    {
        wchar_t buf[MAX_PATH] = {};
        DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
        std::wstring dir = (n > 0 && n < MAX_PATH)
            ? std::wstring(buf) + L"\\MarkdownPreviewHandler"
            : std::wstring(L"C:\\ProgramData\\MarkdownPreviewHandler");
        CreateDirectoryW(dir.c_str(), nullptr);
        path = dir + L"\\viewer.log";
    }

    SYSTEMTIME st;
    GetLocalTime(&st);
    wchar_t line[4096];
    wchar_t body[3072];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(body, _TRUNCATE, fmt, args);
    va_end(args);
    swprintf_s(line, L"%04d-%02d-%02d %02d:%02d:%02d.%03d | %s\r\n",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, body);

    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"a, ccs=UTF-8") == 0 && f)
    {
        fputws(line, f);
        fclose(f);
    }
}

// --------------------------------------------------------------------------
// file reading
// --------------------------------------------------------------------------

bool ReadFileBytes(const std::wstring& path, std::vector<BYTE>& out)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size = {};
    GetFileSizeEx(h, &size);
    if (size.QuadPart <= 0 || size.QuadPart > (64LL << 20)) { CloseHandle(h); return false; }

    out.resize((size_t)size.QuadPart);
    DWORD read = 0;
    BOOL ok = ReadFile(h, out.data(), (DWORD)out.size(), &read, nullptr);
    CloseHandle(h);
    return ok && read == out.size();
}

std::wstring DecodeText(const std::vector<BYTE>& data)
{
    if (data.empty()) return L"";
    const BYTE* p = data.data();
    size_t n = data.size();

    if (n >= 3 && p[0] == 0xEF && p[1] == 0xBB && p[2] == 0xBF)
    {
        int len = MultiByteToWideChar(CP_UTF8, 0, (LPCSTR)(p + 3), (int)(n - 3), nullptr, 0);
        std::wstring s(len, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, (LPCSTR)(p + 3), (int)(n - 3), &s[0], len);
        return s;
    }
    if (n >= 2 && p[0] == 0xFF && p[1] == 0xFE)
        return std::wstring((const wchar_t*)(p + 2), (n - 2) / 2);
    if (n >= 2 && p[0] == 0xFE && p[1] == 0xFF)
    {
        std::wstring s;
        for (size_t i = 2; i + 1 < n; i += 2) s += (wchar_t)((p[i] << 8) | p[i + 1]);
        return s;
    }
    int len = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (LPCSTR)p, (int)n, nullptr, 0);
    if (len > 0)
    {
        std::wstring s(len, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, (LPCSTR)p, (int)n, &s[0], len);
        return s;
    }
    len = MultiByteToWideChar(CP_ACP, 0, (LPCSTR)p, (int)n, nullptr, 0);
    std::wstring s(len, L'\0');
    MultiByteToWideChar(CP_ACP, 0, (LPCSTR)p, (int)n, &s[0], len);
    return s;
}

std::wstring SourceDirOf(const std::wstring& path)
{
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"" : path.substr(0, slash);
}

// --------------------------------------------------------------------------
// WebView2 callbacks
// --------------------------------------------------------------------------

class ControllerHandler : public ICoreWebView2CreateCoreWebView2ControllerCompletedHandler
{
public:
    explicit ControllerHandler(HWND host) : ref_(1), host_(host) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown ||
            riid == __uuidof(ICoreWebView2CreateCoreWebView2ControllerCompletedHandler))
        {
            *ppv = static_cast<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&ref_); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG c = InterlockedDecrement(&ref_);
        if (c == 0) delete this;
        return c;
    }

    STDMETHODIMP Invoke(HRESULT errorCode, ICoreWebView2Controller* controller) override
    {
        if (FAILED(errorCode) || !controller)
        {
            Log(L"controller failed 0x%08X", errorCode);
            MessageBoxW(host_, L"WebView2 控制器创建失败。\n请确认已安装 Edge WebView2 运行时。",
                kWindowTitle, MB_ICONERROR);
            return errorCode;
        }
        controller->AddRef();
        g_controller = controller;

        RECT rc = {};
        GetClientRect(host_, &rc);
        controller->put_Bounds(rc);
        controller->put_IsVisible(TRUE);

        ICoreWebView2* wv = nullptr;
        if (SUCCEEDED(controller->get_CoreWebView2(&wv)) && wv)
        {
            std::vector<BYTE> bytes;
            std::wstring html;
            if (ReadFileBytes(g_filePath, bytes))
                html = MarkdownToHtmlDocument(DecodeText(bytes), SourceDirOf(g_filePath));
            else
                html = L"<html><body><p>无法读取文件。</p></body></html>";

            wv->NavigateToString(html.c_str());
            wv->Release();
        }
        g_ready = true;
        Log(L"WebView2 ready");
        return S_OK;
    }

private:
    LONG ref_;
    HWND host_;
};

class EnvironmentHandler : public ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
{
public:
    explicit EnvironmentHandler(HWND host) : ref_(1), host_(host) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown ||
            riid == __uuidof(ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler))
        {
            *ppv = static_cast<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&ref_); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG c = InterlockedDecrement(&ref_);
        if (c == 0) delete this;
        return c;
    }

    STDMETHODIMP Invoke(HRESULT errorCode, ICoreWebView2Environment* env) override
    {
        if (FAILED(errorCode) || !env)
        {
            Log(L"environment failed 0x%08X", errorCode);
            MessageBoxW(host_, L"WebView2 环境创建失败。\n请确认已安装 Edge WebView2 运行时。",
                kWindowTitle, MB_ICONERROR);
            return errorCode;
        }
        env->AddRef();
        g_environment = env;

        ControllerHandler* handler = new (std::nothrow) ControllerHandler(host_);
        if (!handler) return E_OUTOFMEMORY;
        HRESULT hr = env->CreateCoreWebView2Controller(host_, handler);
        handler->Release();
        return hr;
    }

private:
    LONG ref_;
    HWND host_;
};

// --------------------------------------------------------------------------
// window procedure
// --------------------------------------------------------------------------

void StartWebView(HWND hwnd)
{
    auto createEnv = ResolveWebView2Loader();
    if (!createEnv)
    {
        Log(L"WebView2Loader.dll not found");
        MessageBoxW(hwnd,
            L"未找到 WebView2Loader.dll。\n请把它与本程序放在同一目录，"
            L"或先安装 Edge WebView2 运行时。",
            kWindowTitle, MB_ICONERROR);
        return;
    }

    std::wstring udf;
    if (!PrepareWebView2UserDataFolder(udf))
    {
        Log(L"user data folder failed");
        return;
    }

    EnvironmentHandler* handler = new (std::nothrow) EnvironmentHandler(hwnd);
    if (!handler) return;
    HRESULT hr = createEnv(nullptr, udf.c_str(), nullptr, handler);
    handler->Release();
    Log(L"CreateCoreWebView2EnvironmentWithOptions hr=0x%08X", hr);
}

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_CREATE:
        Log(L"WM_CREATE");
        StartWebView(hwnd);
        return 0;

    case WM_SIZE:
        if (g_controller)
        {
            RECT rc = { 0, 0, LOWORD(lp), HIWORD(lp) };
            g_controller->put_Bounds(rc);
        }
        return 0;

    case WM_SETFOCUS:
        if (g_controller) g_controller->MoveFocus(0 /* PROGRAMMATIC */);
        return 0;

    case WM_KEYDOWN:
        // Ctrl+R reloads, Esc closes, F5 reloads
        if (wp == VK_ESCAPE) { DestroyWindow(hwnd); return 0; }
        if (wp == VK_F5 || (wp == 'R' && (GetKeyState(VK_CONTROL) & 0x8000)))
        {
            if (g_controller)
            {
                ICoreWebView2* wv = nullptr;
                if (SUCCEEDED(g_controller->get_CoreWebView2(&wv)) && wv)
                {
                    std::vector<BYTE> bytes;
                    std::wstring html;
                    if (ReadFileBytes(g_filePath, bytes))
                        html = MarkdownToHtmlDocument(DecodeText(bytes), SourceDirOf(g_filePath));
                    wv->NavigateToString(html.c_str());
                    wv->Release();
                }
            }
            return 0;
        }
        break;

    case WM_DESTROY:
        Log(L"WM_DESTROY");
        if (g_controller)
        {
            g_controller->Close();
            g_controller->Release();
            g_controller = nullptr;
        }
        if (g_environment)
        {
            g_environment->Release();
            g_environment = nullptr;
        }
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

// --------------------------------------------------------------------------
// helpers
// --------------------------------------------------------------------------

std::wstring PickFile(HWND owner)
{
    wchar_t buf[MAX_PATH] = {};
    OPENFILENAMEW ofn = { sizeof(ofn) };
    ofn.hwndOwner = owner;
    ofn.lpstrFilter =
        L"Markdown 文件\0*.md;*.markdown;*.mdown;*.mkd\0文本文件\0*.txt\0所有文件\0*.*\0";
    ofn.lpstrFile = buf;
    ofn.nMaxFile = MAX_PATH;
    ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER;
    ofn.lpstrTitle = L"选择要查看的 Markdown 文件";
    if (GetOpenFileNameW(&ofn)) return buf;
    return L"";
}

bool FileExists(const std::wstring& p)
{
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

} // namespace

// --------------------------------------------------------------------------
// entry point
// --------------------------------------------------------------------------

int APIENTRY wWinMain(HINSTANCE hInstance, HINSTANCE, LPWSTR lpCmdLine, int nCmdShow)
{
    g_hModule = hInstance;
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    // command line: first non-switch argument is the file
    {
        int argc = 0;
        LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        if (argv)
        {
            for (int i = 1; i < argc; i++)
            {
                if (argv[i][0] == L'-' || argv[i][0] == L'/') continue;
                if (FileExists(argv[i])) { g_filePath = argv[i]; break; }
            }
            LocalFree(argv);
        }
    }

    Log(L"=== viewer start, file=%s ===", g_filePath.empty() ? L"(none)" : g_filePath.c_str());

    if (g_filePath.empty())
    {
        g_filePath = PickFile(nullptr);
        if (g_filePath.empty())
        {
            Log(L"no file selected, exiting");
            CoUninitialize();
            return 0;
        }
    }

    // window
    WNDCLASSEXW wc = { sizeof(wc) };
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)GetStockObject(WHITE_BRUSH);
    wc.lpszClassName = kWindowClass;
    wc.hIcon = LoadIcon(nullptr, IDI_APPLICATION);
    RegisterClassExW(&wc);

    std::wstring title = kWindowTitle;
    {
        size_t slash = g_filePath.find_last_of(L"\\/");
        std::wstring name = slash == std::wstring::npos ? g_filePath : g_filePath.substr(slash + 1);
        title += L" - " + name;
    }

    RECT rc = { 0, 0, 980, 760 };
    AdjustWindowRect(&rc, WS_OVERLAPPEDWINDOW, FALSE);
    int w = rc.right - rc.left, h = rc.bottom - rc.top;
    int x = (GetSystemMetrics(SM_CXSCREEN) - w) / 2;
    int y = (GetSystemMetrics(SM_CYSCREEN) - h) / 2;

    g_hwnd = CreateWindowExW(0, kWindowClass, title.c_str(),
        WS_OVERLAPPEDWINDOW, x, y, w, h, nullptr, nullptr, hInstance, nullptr);
    if (!g_hwnd)
    {
        Log(L"CreateWindow failed %lu", GetLastError());
        CoUninitialize();
        return 1;
    }

    ShowWindow(g_hwnd, nCmdShow);
    UpdateWindow(g_hwnd);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    Log(L"=== viewer exit ===");
    CoUninitialize();
    return 0;
}
