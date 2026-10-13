// MarkdownPreviewHandler.cpp
//
// The native in-process preview handler implementation.
//
// Rendering strategy:
//   * Markdown -> HTML (MarkdownToHtml.cpp) -> WebView2 (Edge runtime).
//     WebView2 is loaded dynamically from WebView2Loader.dll; if it is missing
//     we fall back to a plain GDI text drawing so the pane still shows content.
//   * Images -> GDI+ with EXIF-orientation handling and fit-to-window scaling.

#include "MarkdownPreviewHandler.h"
#include <eventtoken.h>     // EventRegistrationToken (WebView2 event methods)
#include <shlwapi.h>        // SHCreateMemStream

#pragma comment(lib, "gdiplus.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shlwapi.lib")

using namespace Gdiplus;

// ---------------------------------------------------------------------------
// module state
// ---------------------------------------------------------------------------

LONG g_moduleRefCount = 0;
HMODULE g_hModule = nullptr;

void DllAddRef() { InterlockedIncrement(&g_moduleRefCount); }
void DllRelease() { InterlockedDecrement(&g_moduleRefCount); }

namespace {

// Log file resolution with fallbacks: the surrogate host may not expose
// LOCALAPPDATA, in which case we still want a record of being loaded.
std::wstring LogFilePath()
{
    static std::wstring cached;
    if (!cached.empty()) return cached;

    wchar_t buf[MAX_PATH] = {};

    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
    if (n > 0 && n < MAX_PATH)
    {
        cached = std::wstring(buf) + L"\\MarkdownPreviewHandler";
    }
    else
    {
        wchar_t temp[MAX_PATH] = {};
        if (GetTempPathW(MAX_PATH, temp))
            cached = std::wstring(temp) + L"MarkdownPreviewHandler";
    }

    if (cached.empty()) return cached;

    CreateDirectoryW(cached.c_str(), nullptr);
    cached += L"\\handler.log";
    return cached;
}

} // namespace

void NativeLog(const wchar_t* fmt, ...)
{
    std::wstring path = LogFilePath();
    if (path.empty()) return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    wchar_t prefix[160];
    swprintf_s(prefix, L"%04d-%02d-%02d %02d:%02d:%02d.%03d | pid=%lu | tid=%lu | ",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
        GetCurrentProcessId(), GetCurrentThreadId());

    wchar_t body[2048];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(body, _TRUNCATE, fmt, args);
    va_end(args);

    std::wstring line = std::wstring(prefix) + body + L"\r\n";

    // append as UTF-8 so Chinese paths survive
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"a, ccs=UTF-8") == 0 && f)
    {
        fputws(line.c_str(), f);
        fclose(f);
    }
}

namespace {
void Log(const wchar_t* fmt, ...)
{
    va_list args;
    va_start(args, fmt);
    wchar_t body[2048];
    _vsnwprintf_s(body, _TRUNCATE, fmt, args);
    va_end(args);
    NativeLog(L"%s", body);
}
} // namespace

// EXIF orientation
UINT ReadExifOrientation(IStream* stream)
{
    if (!stream) return 1;
    LARGE_INTEGER zero = {};
    stream->Seek(zero, STREAM_SEEK_SET, nullptr);
    Gdiplus::Bitmap bmp(stream, FALSE);
    if (bmp.GetLastStatus() != Ok) return 1;
    UINT orientation = 1;
    UINT size = bmp.GetPropertyItemSize(PropertyTagOrientation);
    if (size > 0)
    {
        PropertyItem* item = (PropertyItem*)malloc(size);
        if (item)
        {
            if (bmp.GetPropertyItem(PropertyTagOrientation, size, item) == Ok)
                orientation = *(UINT*)item->value;
            free(item);
        }
    }
    return orientation ? orientation : 1;
}

// ---------------------------------------------------------------------------
// construction
// ---------------------------------------------------------------------------

MarkdownPreviewHandler::MarkdownPreviewHandler()
    : refCount_(1), parentHwnd_(nullptr), childHwnd_(nullptr), site_(nullptr),
      isImage_(false), webView_(nullptr), webViewEnv_(nullptr), webViewReady_(false)
{
    ZeroMemory(&bounds_, sizeof(bounds_));
    DllAddRef();
    Log(L"ctor");
}

MarkdownPreviewHandler::~MarkdownPreviewHandler()
{
    Log(L"dtor");
    if (site_) { site_->Release(); site_ = nullptr; }
    // WebView2 controller teardown is handled in Unload()
    DllRelease();
}

// ---------------------------------------------------------------------------
// IUnknown
// ---------------------------------------------------------------------------

STDMETHODIMP MarkdownPreviewHandler::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;

    if (riid == IID_IUnknown || riid == IID_IPreviewHandler_)
        *ppv = static_cast<IPreviewHandler*>(this);
    else if (riid == IID_IInitializeWithStream)
        *ppv = static_cast<IInitializeWithStream*>(this);
    else if (riid == IID_IInitializeWithFile)
        *ppv = static_cast<IInitializeWithFile*>(this);
    else if (riid == IID_IObjectWithSite)
        *ppv = static_cast<IObjectWithSite*>(this);
    else if (riid == IID_IOleWindow)
        *ppv = static_cast<IOleWindow*>(this);
    else
        return E_NOINTERFACE;

    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) MarkdownPreviewHandler::AddRef()
{
    return InterlockedIncrement(&refCount_);
}

STDMETHODIMP_(ULONG) MarkdownPreviewHandler::Release()
{
    LONG c = InterlockedDecrement(&refCount_);
    if (c == 0) delete this;
    return c;
}

// ---------------------------------------------------------------------------
// initialization
// ---------------------------------------------------------------------------

bool MarkdownPreviewHandler::ReadAllBytes(const std::wstring& path, std::vector<BYTE>& out)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER size = {};
    GetFileSizeEx(h, &size);
    out.resize((size_t)size.QuadPart);

    DWORD read = 0;
    BOOL ok = out.empty() ? TRUE : ReadFile(h, out.data(), (DWORD)out.size(), &read, nullptr);
    CloseHandle(h);
    return ok && read == out.size();
}

bool MarkdownPreviewHandler::LooksLikeImage(const BYTE* d, size_t n)
{
    if (!d || n < 12) return false;
    if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return true;                       // JPEG
    if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true;       // PNG
    if (d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return true;                       // GIF
    if (d[0] == 0x42 && d[1] == 0x4D) return true;                                       // BMP
    if (d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A) return true;                       // TIFF
    if (d[0] == 0x4D && d[1] == 0x4D && d[2] == 0x00 && d[3] == 0x2A) return true;       // TIFF
    if (d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46) return true;       // RIFF/WEBP
    return false;
}

// decode bytes into a wstring, honouring BOM and falling back to UTF-8 / ANSI
static std::wstring DecodeText(const std::vector<BYTE>& data)
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
        s.reserve((n - 2) / 2);
        for (size_t i = 2; i + 1 < n; i += 2)
            s += (wchar_t)((p[i] << 8) | p[i + 1]);
        return s;
    }

    // try strict UTF-8
    int len = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (LPCSTR)p, (int)n, nullptr, 0);
    if (len > 0)
    {
        std::wstring s(len, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, (LPCSTR)p, (int)n, &s[0], len);
        return s;
    }
    // fall back to the system ANSI code page (GBK on Chinese systems)
    len = MultiByteToWideChar(CP_ACP, 0, (LPCSTR)p, (int)n, nullptr, 0);
    std::wstring s(len, L'\0');
    MultiByteToWideChar(CP_ACP, 0, (LPCSTR)p, (int)n, &s[0], len);
    return s;
}

STDMETHODIMP MarkdownPreviewHandler::Initialize(IStream* pstream, DWORD grfMode)
{
    Log(L"IInitializeWithStream::Initialize");
    if (!pstream) return E_INVALIDARG;

    STATSTG stat = {};
    pstream->Stat(&stat, STATFLAG_NONAME);
    ULONGLONG size = stat.cbSize.QuadPart;

    LARGE_INTEGER zero = {};
    pstream->Seek(zero, STREAM_SEEK_SET, nullptr);

    bytes_.clear();
    bytes_.reserve((size_t)size);
    BYTE buf[16384];
    ULONG read = 0;
    while (pstream->Read(buf, sizeof(buf), &read) == S_OK && read > 0)
        bytes_.insert(bytes_.end(), buf, buf + read);

    Log(L"  read %zu bytes", bytes_.size());

    isImage_ = LooksLikeImage(bytes_.empty() ? nullptr : bytes_.data(), bytes_.size());
    if (!isImage_) markdownText_ = DecodeText(bytes_);
    return S_OK;
}

STDMETHODIMP MarkdownPreviewHandler::Initialize(LPCWSTR pszFilePath, DWORD grfMode)
{
    Log(L"IInitializeWithFile::Initialize(%s)", pszFilePath ? pszFilePath : L"(null)");
    if (!pszFilePath) return E_INVALIDARG;

    filePath_ = pszFilePath;
    if (!ReadAllBytes(filePath_, bytes_))
    {
        Log(L"  failed to read file");
        return HRESULT_FROM_WIN32(GetLastError());
    }
    Log(L"  read %zu bytes", bytes_.size());

    isImage_ = LooksLikeImage(bytes_.empty() ? nullptr : bytes_.data(), bytes_.size());
    if (!isImage_) markdownText_ = DecodeText(bytes_);
    return S_OK;
}

// ---------------------------------------------------------------------------
// WebView2, dynamically loaded.
//
// Only four objects and two callbacks are needed, so the interfaces are
// declared here (vtable order per the WebView2 IDL) instead of pulling in
// WebView2.h + WRL. That keeps the DLL dependency-free: WebView2Loader.dll is
// resolved with LoadLibrary at runtime.
// ---------------------------------------------------------------------------

typedef interface ICoreWebView2 ICoreWebView2;
typedef interface ICoreWebView2Controller ICoreWebView2Controller;
typedef interface ICoreWebView2Environment ICoreWebView2Environment;
typedef interface ICoreWebView2EnvironmentOptions ICoreWebView2EnvironmentOptions;

typedef interface ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
    ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler;
typedef interface ICoreWebView2CreateCoreWebView2ControllerCompletedHandler
    ICoreWebView2CreateCoreWebView2ControllerCompletedHandler;

MIDL_INTERFACE("19a319f2-6f0d-4d3a-9c33-5c1b4a1d1f2b")
ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler : public IUnknown
{
public:
    virtual HRESULT STDMETHODCALLTYPE Invoke(
        HRESULT errorCode, ICoreWebView2Environment* createdEnvironment) = 0;
};

MIDL_INTERFACE("6c4819f3-c9b7-4260-8127-c9f5bd9cbc53")
ICoreWebView2CreateCoreWebView2ControllerCompletedHandler : public IUnknown
{
public:
    virtual HRESULT STDMETHODCALLTYPE Invoke(
        HRESULT errorCode, ICoreWebView2Controller* createdController) = 0;
};

MIDL_INTERFACE("c979a61a-0d4d-4d1b-9d5b-2a4e2e5f7f1a")
ICoreWebView2 : public IUnknown
{
public:
    virtual HRESULT STDMETHODCALLTYPE get_Settings(void** settings) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_Source(LPWSTR* source) = 0;
    virtual HRESULT STDMETHODCALLTYPE Navigate(LPCWSTR uri) = 0;
    virtual HRESULT STDMETHODCALLTYPE NavigateToString(LPCWSTR htmlContent) = 0;
    virtual HRESULT STDMETHODCALLTYPE add_NavigationStarting(IUnknown* handler, EventRegistrationToken* token) = 0;
    virtual HRESULT STDMETHODCALLTYPE remove_NavigationStarting(EventRegistrationToken token) = 0;
};

MIDL_INTERFACE("4d00c0d1-9434-498b-9c1b-1f1c0c8a1d10")
ICoreWebView2Controller : public IUnknown
{
public:
    virtual HRESULT STDMETHODCALLTYPE get_IsVisible(BOOL* isVisible) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_IsVisible(BOOL isVisible) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_Bounds(RECT* bounds) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_Bounds(RECT bounds) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_ZoomFactor(double* zoomFactor) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_ZoomFactor(double zoomFactor) = 0;
    virtual HRESULT STDMETHODCALLTYPE add_ZoomFactorChanged(IUnknown* handler, EventRegistrationToken* token) = 0;
    virtual HRESULT STDMETHODCALLTYPE remove_ZoomFactorChanged(EventRegistrationToken token) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetBoundsAndZoomFactor(RECT bounds, double zoomFactor) = 0;
    virtual HRESULT STDMETHODCALLTYPE MoveFocus(int reason) = 0;
    virtual HRESULT STDMETHODCALLTYPE add_MoveFocusRequested(IUnknown* handler, EventRegistrationToken* token) = 0;
    virtual HRESULT STDMETHODCALLTYPE remove_MoveFocusRequested(EventRegistrationToken token) = 0;
    virtual HRESULT STDMETHODCALLTYPE add_GotFocus(IUnknown* handler, EventRegistrationToken* token) = 0;
    virtual HRESULT STDMETHODCALLTYPE remove_GotFocus(EventRegistrationToken token) = 0;
    virtual HRESULT STDMETHODCALLTYPE add_LostFocus(IUnknown* handler, EventRegistrationToken* token) = 0;
    virtual HRESULT STDMETHODCALLTYPE remove_LostFocus(EventRegistrationToken token) = 0;
    virtual HRESULT STDMETHODCALLTYPE add_AcceleratorKeyPressed(IUnknown* handler, EventRegistrationToken* token) = 0;
    virtual HRESULT STDMETHODCALLTYPE remove_AcceleratorKeyPressed(EventRegistrationToken token) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_ParentWindow(HWND* parentWindow) = 0;
    virtual HRESULT STDMETHODCALLTYPE put_ParentWindow(HWND parentWindow) = 0;
    virtual HRESULT STDMETHODCALLTYPE NotifyParentWindowPositionChanged() = 0;
    virtual HRESULT STDMETHODCALLTYPE Close() = 0;
    virtual HRESULT STDMETHODCALLTYPE get_CoreWebView2(ICoreWebView2** coreWebView2) = 0;
};

MIDL_INTERFACE("b96d755e-0319-4e92-a296-23436f46a1fc")
ICoreWebView2Environment : public IUnknown
{
public:
    virtual HRESULT STDMETHODCALLTYPE CreateCoreWebView2Controller(
        HWND parentWindow,
        ICoreWebView2CreateCoreWebView2ControllerCompletedHandler* handler) = 0;
    virtual HRESULT STDMETHODCALLTYPE CreateWebResourceResponse(
        IStream* content, int statusCode, LPCWSTR reasonPhrase, LPCWSTR headers, void** response) = 0;
    virtual HRESULT STDMETHODCALLTYPE get_BrowserVersionString(LPWSTR* versionInfo) = 0;
};

typedef HRESULT (STDAPICALLTYPE *CreateCoreWebView2EnvironmentWithOptionsFn)(
    PCWSTR browserExecutableFolder, PCWSTR userDataFolder,
    ICoreWebView2EnvironmentOptions* environmentOptions,
    ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler* environmentCreatedHandler);

namespace {

// The user data folder must be created before the environment is built, so
// each preview session gets its own directory (avoids lock contention).
HRESULT PrepareUserDataFolder(std::wstring& out)
{
    wchar_t temp[MAX_PATH] = {};
    if (!GetTempPathW(MAX_PATH, temp)) return E_FAIL;
    std::wstring base = std::wstring(temp) + L"MarkdownPreviewHandler";
    CreateDirectoryW(base.c_str(), nullptr);

    wchar_t udf[MAX_PATH] = {};
    swprintf_s(udf, L"%s\\udf_%lu", base.c_str(), GetCurrentProcessId());
    CreateDirectoryW(udf, nullptr);
    out = udf;
    return S_OK;
}

// Environment creation is asynchronous; this callback receives the environment
// and then creates the controller on the child window.
class EnvCompletedHandler : public ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
{
public:
    EnvCompletedHandler(MarkdownPreviewHandler* owner) : ref_(1), owner_(owner) {}

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

    STDMETHODIMP Invoke(HRESULT errorCode, ICoreWebView2Environment* env) override;

private:
    LONG ref_;
    MarkdownPreviewHandler* owner_;
};

// Controller creation is asynchronous too; once it exists we can navigate.
class ControllerCompletedHandler : public ICoreWebView2CreateCoreWebView2ControllerCompletedHandler
{
public:
    ControllerCompletedHandler(MarkdownPreviewHandler* owner) : ref_(1), owner_(owner) {}

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

    STDMETHODIMP Invoke(HRESULT errorCode, ICoreWebView2Controller* controller) override;

private:
    LONG ref_;
    MarkdownPreviewHandler* owner_;
};

} // namespace

STDMETHODIMP EnvCompletedHandler::Invoke(HRESULT errorCode, ICoreWebView2Environment* env)
{
    if (FAILED(errorCode) || !env)
    {
        Log(L"WebView2 environment failed 0x%08X", errorCode);
        return errorCode;
    }
    env->AddRef();
    owner_->SetWebViewEnvironment(env);

    ControllerCompletedHandler* handler = new (std::nothrow) ControllerCompletedHandler(owner_);
    if (!handler) return E_OUTOFMEMORY;
    HRESULT hr = env->CreateCoreWebView2Controller(owner_->GetChildHwnd(), handler);
    handler->Release();
    return hr;
}

STDMETHODIMP ControllerCompletedHandler::Invoke(HRESULT errorCode, ICoreWebView2Controller* controller)
{
    if (FAILED(errorCode) || !controller)
    {
        Log(L"WebView2 controller failed 0x%08X", errorCode);
        return errorCode;
    }
    controller->AddRef();
    owner_->SetWebViewController(controller);

    RECT rc = {};
    GetClientRect(owner_->GetChildHwnd(), &rc);
    controller->put_Bounds(rc);
    controller->put_IsVisible(TRUE);

    ICoreWebView2* wv = nullptr;
    if (SUCCEEDED(controller->get_CoreWebView2(&wv)) && wv)
    {
        wv->NavigateToString(owner_->BuildHtml().c_str());
        wv->Release();
    }
    owner_->SetWebViewReady();
    Log(L"WebView2 ready");
    return S_OK;
}

HRESULT MarkdownPreviewHandler::LoadWebView()
{
    if (webView_) return S_OK;   // already up

    // Resolve WebView2Loader.dll: DLL folder first, then PATH and the Edge
    // install folder.
    wchar_t self[MAX_PATH] = {};
    GetModuleFileNameW(g_hModule, self, MAX_PATH);
    std::wstring dir = self;
    size_t slash = dir.find_last_of(L"\\/");
    if (slash != std::wstring::npos) dir.erase(slash);

    HMODULE loader = LoadLibraryW((dir + L"\\WebView2Loader.dll").c_str());
    if (!loader) loader = LoadLibraryW(L"WebView2Loader.dll");
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
    if (!loader)
    {
        Log(L"WebView2Loader.dll not found - GDI fallback");
        return E_FAIL;
    }

    auto createEnv = (CreateCoreWebView2EnvironmentWithOptionsFn)
        GetProcAddress(loader, "CreateCoreWebView2EnvironmentWithOptions");
    if (!createEnv)
    {
        Log(L"CreateCoreWebView2EnvironmentWithOptions missing - GDI fallback");
        return E_FAIL;
    }

    std::wstring udf;
    if (FAILED(PrepareUserDataFolder(udf)))
    {
        Log(L"user data folder preparation failed");
        return E_FAIL;
    }

    EnvCompletedHandler* handler = new (std::nothrow) EnvCompletedHandler(this);
    if (!handler) return E_OUTOFMEMORY;

    HRESULT hr = createEnv(nullptr, udf.c_str(), nullptr, handler);
    handler->Release();

    Log(L"CreateCoreWebView2EnvironmentWithOptions hr=0x%08X", hr);
    return hr;
}

std::wstring MarkdownPreviewHandler::BuildHtml() const
{
    std::wstring sourceDir;
    if (!filePath_.empty())
    {
        size_t slash = filePath_.find_last_of(L"\\/");
        if (slash != std::wstring::npos) sourceDir = filePath_.substr(0, slash);
    }
    return MarkdownToHtmlDocument(markdownText_, sourceDir);
}

// ---------------------------------------------------------------------------
// window management
// ---------------------------------------------------------------------------

static LRESULT CALLBACK PreviewChildProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    switch (msg)
    {
    case WM_PAINT:
    {
        PAINTSTRUCT ps;
        HDC hdc = BeginPaint(hwnd, &ps);
        RECT rc;
        GetClientRect(hwnd, &rc);
        FillRect(hdc, &rc, (HBRUSH)GetStockObject(WHITE_BRUSH));
        EndPaint(hwnd, &ps);
        return 0;
    }
    case WM_ERASEBKGND:
        return 1;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static const wchar_t* kChildClass = L"MarkdownPreviewNativeChild";

static void EnsureChildClass()
{
    static bool registered = false;
    if (registered) return;
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = PreviewChildProc;
    wc.hInstance = g_hModule;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)GetStockObject(WHITE_BRUSH);
    wc.lpszClassName = kChildClass;
    RegisterClassExW(&wc);
    registered = true;
}

HRESULT MarkdownPreviewHandler::CreatePreviewWindow()
{
    if (childHwnd_) return S_OK;

    EnsureChildClass();
    RECT rc = bounds_;
    childHwnd_ = CreateWindowExW(0, kChildClass, L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
        rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top,
        parentHwnd_, nullptr, g_hModule, nullptr);

    Log(L"CreatePreviewWindow hwnd=%p parent=%p", childHwnd_, parentHwnd_);
    return childHwnd_ ? S_OK : E_FAIL;
}

void MarkdownPreviewHandler::ResizeToRect()
{
    if (!childHwnd_) return;
    SetWindowPos(childHwnd_, nullptr, bounds_.left, bounds_.top,
        bounds_.right - bounds_.left, bounds_.bottom - bounds_.top,
        SWP_NOZORDER | SWP_NOACTIVATE);

    if (webView_)
    {
        RECT rc = {};
        GetClientRect(childHwnd_, &rc);
        static_cast<ICoreWebView2Controller*>(webView_)->put_Bounds(rc);
    }
    InvalidateRect(childHwnd_, nullptr, TRUE);
}

void MarkdownPreviewHandler::SetWebViewEnvironment(void* env)
{
    webViewEnv_ = env;
}

void MarkdownPreviewHandler::SetWebViewController(void* controller)
{
    webView_ = controller;
}

void MarkdownPreviewHandler::SetWebViewReady()
{
    webViewReady_ = true;
    if (childHwnd_) InvalidateRect(childHwnd_, nullptr, TRUE);
}

// ---------------------------------------------------------------------------
// IPreviewHandler
// ---------------------------------------------------------------------------

STDMETHODIMP MarkdownPreviewHandler::SetWindow(HWND hwnd, const RECT* prc)
{
    Log(L"SetWindow hwnd=%p rect=%ld,%ld %ldx%ld", hwnd,
        prc ? prc->left : 0, prc ? prc->top : 0,
        prc ? prc->right - prc->left : 0, prc ? prc->bottom - prc->top : 0);

    if (!hwnd || !prc) return E_INVALIDARG;
    parentHwnd_ = hwnd;
    bounds_ = *prc;
    return CreatePreviewWindow();
}

STDMETHODIMP MarkdownPreviewHandler::SetRect(const RECT* prc)
{
    if (!prc) return E_INVALIDARG;
    bounds_ = *prc;
    ResizeToRect();
    return S_OK;
}

STDMETHODIMP MarkdownPreviewHandler::DoPreview()
{
    Log(L"DoPreview isImage=%d markdownLen=%zu", isImage_ ? 1 : 0, markdownText_.size());

    if (!childHwnd_)
    {
        Log(L"DoPreview: no child window (SetWindow not called?)");
        return E_UNEXPECTED;
    }

    if (isImage_)
        return RenderImage();

    // markdown / text -> HTML -> WebView2, with GDI fallback
    HRESULT hr = LoadWebView();
    if (SUCCEEDED(hr) || hr == E_NOTIMPL)
        return S_OK;

    // fallback: draw the raw text so the pane is never empty
    HDC hdc = GetDC(childHwnd_);
    RECT rc;
    GetClientRect(childHwnd_, &rc);
    FillRect(hdc, &rc, (HBRUSH)GetStockObject(WHITE_BRUSH));
    SetBkMode(hdc, TRANSPARENT);
    SetTextColor(hdc, RGB(30, 30, 30));
    rc.left += 16; rc.top += 16; rc.right -= 16; rc.bottom -= 16;
    DrawTextW(hdc, markdownText_.c_str(), (int)markdownText_.size(), &rc, DT_LEFT | DT_WORDBREAK | DT_NOPREFIX);
    ReleaseDC(childHwnd_, hdc);
    Log(L"DoPreview: GDI fallback drawn");
    return S_OK;
}

STDMETHODIMP MarkdownPreviewHandler::Unload()
{
    Log(L"Unload");
    if (webView_)
    {
        static_cast<ICoreWebView2Controller*>(webView_)->Close();
        static_cast<ICoreWebView2Controller*>(webView_)->Release();
        webView_ = nullptr;
    }
    if (webViewEnv_)
    {
        static_cast<ICoreWebView2Environment*>(webViewEnv_)->Release();
        webViewEnv_ = nullptr;
    }
    if (childHwnd_)
    {
        DestroyWindow(childHwnd_);
        childHwnd_ = nullptr;
    }
    webViewReady_ = false;
    markdownText_.clear();
    bytes_.clear();
    return S_OK;
}

STDMETHODIMP MarkdownPreviewHandler::SetFocus()
{
    if (childHwnd_) ::SetFocus(childHwnd_);
    return S_OK;
}

STDMETHODIMP MarkdownPreviewHandler::QueryFocus(HWND* phwnd)
{
    if (!phwnd) return E_POINTER;
    *phwnd = childHwnd_ ? childHwnd_ : parentHwnd_;
    return *phwnd ? S_OK : E_FAIL;
}

STDMETHODIMP MarkdownPreviewHandler::TranslateAccelerator(MSG* pmsg)
{
    return S_FALSE;   // let the host handle keys
}

// ---------------------------------------------------------------------------
// IObjectWithSite
// ---------------------------------------------------------------------------

STDMETHODIMP MarkdownPreviewHandler::SetSite(IUnknown* punkSite)
{
    Log(L"SetSite");
    if (site_) { site_->Release(); site_ = nullptr; }
    site_ = punkSite;
    if (site_) site_->AddRef();
    return S_OK;
}

STDMETHODIMP MarkdownPreviewHandler::GetSite(REFIID riid, void** ppvSite)
{
    if (!ppvSite) return E_POINTER;
    *ppvSite = nullptr;
    if (!site_) return E_FAIL;
    return site_->QueryInterface(riid, ppvSite);
}

// ---------------------------------------------------------------------------
// IOleWindow
// ---------------------------------------------------------------------------

STDMETHODIMP MarkdownPreviewHandler::GetWindow(HWND* phwnd)
{
    if (!phwnd) return E_POINTER;
    *phwnd = childHwnd_ ? childHwnd_ : parentHwnd_;
    return S_OK;
}

STDMETHODIMP MarkdownPreviewHandler::ContextSensitiveHelp(BOOL)
{
    return S_OK;
}

// ---------------------------------------------------------------------------
// image rendering
// ---------------------------------------------------------------------------

STDMETHODIMP MarkdownPreviewHandler::RenderImage()
{
    // Render the decoded image into the child window with fit-to-window scaling
    // and EXIF orientation applied.
    IStream* stream = SHCreateMemStream(bytes_.data(), (UINT)bytes_.size());
    if (!stream)
    {
        Log(L"RenderImage: SHCreateMemStream failed");
        return E_FAIL;
    }

    UINT orientation = ReadExifOrientation(stream);
    LARGE_INTEGER zero = {};
    stream->Seek(zero, STREAM_SEEK_SET, nullptr);

    Gdiplus::Bitmap* bmp = Gdiplus::Bitmap::FromStream(stream);
    stream->Release();

    if (!bmp || bmp->GetLastStatus() != Ok)
    {
        Log(L"RenderImage: GDI+ decode failed");
        delete bmp;
        return E_FAIL;
    }

    // apply EXIF orientation by rotating/flipping into a new bitmap
    if (orientation >= 2 && orientation <= 8)
    {
        RotateFlipType op = RotateNoneFlipNone;
        switch (orientation)
        {
        case 2: op = RotateNoneFlipX; break;
        case 3: op = Rotate180FlipNone; break;
        case 4: op = Rotate180FlipX; break;
        case 5: op = Rotate90FlipX; break;
        case 6: op = Rotate90FlipNone; break;
        case 7: op = Rotate270FlipX; break;
        case 8: op = Rotate270FlipNone; break;
        }
        bmp->RotateFlip(op);
    }

    RECT rc = {};
    GetClientRect(childHwnd_, &rc);
    int cw = rc.right - rc.left, ch = rc.bottom - rc.top;
    if (cw <= 0 || ch <= 0) { delete bmp; return S_OK; }

    HDC hdc = GetDC(childHwnd_);
    {
        Graphics g(hdc);
        g.SetInterpolationMode(InterpolationModeHighQualityBicubic);
        g.Clear(Color(255, 255, 255));

        UINT iw = bmp->GetWidth(), ih = bmp->GetHeight();
        if (iw && ih)
        {
            double sx = (double)cw / iw, sy = (double)ch / ih;
            double s = min(sx, sy);
            if (s > 1.0) s = 1.0;             // never upscale past 100%
            int dw = (int)(iw * s), dh = (int)(ih * s);
            int dx = (cw - dw) / 2, dy = (ch - dh) / 2;
            g.DrawImage(bmp, dx, dy, dw, dh);
        }
    }
    ReleaseDC(childHwnd_, hdc);
    delete bmp;

    Log(L"RenderImage done %ldx%ld (orientation=%u)", rc.right, rc.bottom, orientation);
    return S_OK;
}

// ---------------------------------------------------------------------------
// class factory
// ---------------------------------------------------------------------------

class MarkdownPreviewClassFactory : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
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

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        Log(L"IClassFactory::CreateInstance");
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;

        MarkdownPreviewHandler* handler = new (std::nothrow) MarkdownPreviewHandler();
        if (!handler) return E_OUTOFMEMORY;

        HRESULT hr = handler->QueryInterface(riid, ppv);
        handler->Release();
        if (FAILED(hr)) Log(L"CreateInstance QI failed 0x%08X", hr);
        return hr;
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) DllAddRef(); else DllRelease();
        return S_OK;
    }

private:
    LONG ref_ = 1;
};

// ---------------------------------------------------------------------------
// DLL exports
// ---------------------------------------------------------------------------

extern "C" HRESULT STDAPICALLTYPE DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (rclsid != CLSID_MarkdownPreviewHandler) return CLASS_E_CLASSNOTAVAILABLE;

    MarkdownPreviewClassFactory* factory = new (std::nothrow) MarkdownPreviewClassFactory();
    if (!factory) return E_OUTOFMEMORY;

    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

extern "C" HRESULT STDAPICALLTYPE DllCanUnloadNow()
{
    return (g_moduleRefCount == 0) ? S_OK : S_FALSE;
}
