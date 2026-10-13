// TestHarness.cpp
//
// Standalone in-process test for MarkdownPreviewNative.dll.
//
// It does exactly what the shell does:
//   LoadLibrary(dll) -> DllGetClassObject -> CreateInstance
//   -> IInitializeWithFile::Initialize -> IPreviewHandler::SetWindow -> DoPreview
// then pumps messages so the WebView2 rendering completes and saves a
// screenshot of the host window.
//
// Usage: TestHarness.exe <handler.dll> <file> [out.png] [seconds]

#include <windows.h>
#include <shobjidl.h>
#include <gdiplus.h>
#include <stdio.h>
#include <string>

#pragma comment(lib, "gdiplus.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

// The handler's CLSID and the shell's preview-handler shelex GUID
static const GUID CLSID_Handler =
    { 0x3511ea64, 0xb51a, 0x4298, { 0xb6, 0xc3, 0x6a, 0xb8, 0x1f, 0x17, 0x9e, 0x1c } };

typedef HRESULT (STDAPICALLTYPE *DllGetClassObjectFn)(REFCLSID, REFIID, void**);

static int CountChildren(HWND parent)
{
    int n = 0;
    EnumChildWindows(parent, [](HWND, LPARAM p) -> BOOL { (*(int*)p)++; return TRUE; }, (LPARAM)&n);
    return n;
}

static void SaveWindow(HWND hwnd, const wchar_t* path)
{
    RECT r;
    GetWindowRect(hwnd, &r);
    int w = r.right - r.left, h = r.bottom - r.top;
    if (w <= 0 || h <= 0) return;

    HDC screen = GetDC(nullptr);
    HDC mem = CreateCompatibleDC(screen);
    HBITMAP bmp = CreateCompatibleBitmap(screen, w, h);
    HGDIOBJ old = SelectObject(mem, bmp);
    BitBlt(mem, 0, 0, w, h, screen, r.left, r.top, SRCCOPY);

    Gdiplus::Bitmap gb(bmp, nullptr);
    CLSID pngClsid;
    CLSIDFromString(L"{557cf406-1a04-11d3-9a73-0000f81ef32e}", &pngClsid); // PNG encoder
    gb.Save(path, &pngClsid, nullptr);

    SelectObject(mem, old);
    DeleteObject(bmp);
    DeleteDC(mem);
    ReleaseDC(nullptr, screen);
}

int wmain(int argc, wchar_t** argv)
{
    const wchar_t* dllPath = argc > 1 ? argv[1] : L"MarkdownPreviewNative.dll";
    const wchar_t* file    = argc > 2 ? argv[2] : L"sample.md";
    const wchar_t* outPng  = argc > 3 ? argv[3] : L"harness_native.png";
    int seconds            = argc > 4 ? _wtoi(argv[4]) : 6;

    Gdiplus::GdiplusStartupInput gdipIn;
    ULONG_PTR gdipToken = 0;
    Gdiplus::GdiplusStartup(&gdipToken, &gdipIn, nullptr);

    wprintf(L"=== native handler test ===\n");
    wprintf(L"dll    : %s\n", dllPath);
    wprintf(L"file   : %s\n", file);
    wprintf(L"exists : %d\n\n", GetFileAttributesW(file) != INVALID_FILE_ATTRIBUTES);

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    wprintf(L"CoInitializeEx hr=0x%08X\n", hr);

    HMODULE mod = LoadLibraryW(dllPath);
    if (!mod) { wprintf(L"LoadLibrary failed: %lu\n", GetLastError()); return 1; }
    wprintf(L"LoadLibrary OK\n");

    auto getClassObject = (DllGetClassObjectFn)GetProcAddress(mod, "DllGetClassObject");
    if (!getClassObject) { wprintf(L"DllGetClassObject not exported\n"); return 1; }

    IClassFactory* factory = nullptr;
    hr = getClassObject(CLSID_Handler, IID_IClassFactory, (void**)&factory);
    wprintf(L"DllGetClassObject hr=0x%08X\n", hr);
    if (FAILED(hr) || !factory) return 1;

    // The handler implements IPreviewHandler, IInitializeWithFile,
    // IInitializeWithStream, IObjectWithSite and IOleWindow; declare the ones
    // we drive here (vtable order matters).
    struct IPreviewHandlerX : public IUnknown
    {
        virtual HRESULT STDMETHODCALLTYPE SetWindow(HWND, const RECT*) = 0;
        virtual HRESULT STDMETHODCALLTYPE SetRect(const RECT*) = 0;
        virtual HRESULT STDMETHODCALLTYPE DoPreview() = 0;
        virtual HRESULT STDMETHODCALLTYPE Unload() = 0;
        virtual HRESULT STDMETHODCALLTYPE SetFocus() = 0;
        virtual HRESULT STDMETHODCALLTYPE QueryFocus(HWND*) = 0;
        virtual HRESULT STDMETHODCALLTYPE TranslateAccelerator(MSG*) = 0;
    };
    struct IInitializeWithFileX : public IUnknown
    {
        virtual HRESULT STDMETHODCALLTYPE Initialize(LPCWSTR, DWORD) = 0;
    };

    static const GUID IID_IPreviewHandlerX =
        { 0x8895b1c6, 0xb7f3, 0x4824, { 0xb0, 0x22, 0x4f, 0xdd, 0x1c, 0x0f, 0xe5, 0xe2 } };
    static const GUID IID_IInitializeWithFileX =
        { 0xb7d14566, 0x0509, 0x4cce, { 0xa7, 0x1f, 0x0a, 0x55, 0x42, 0x33, 0xbd, 0x9b } };

    IUnknown* unknown = nullptr;
    hr = factory->CreateInstance(nullptr, IID_IUnknown, (void**)&unknown);
    wprintf(L"CreateInstance hr=0x%08X\n", hr);
    factory->Release();
    if (FAILED(hr) || !unknown) return 1;

    IInitializeWithFileX* initFile = nullptr;
    hr = unknown->QueryInterface(IID_IInitializeWithFileX, (void**)&initFile);
    wprintf(L"QI IInitializeWithFile   hr=0x%08X\n", hr);

    IPreviewHandlerX* preview = nullptr;
    hr = unknown->QueryInterface(IID_IPreviewHandlerX, (void**)&preview);
    wprintf(L"QI IPreviewHandler       hr=0x%08X\n", hr);

    // host window
    WNDCLASSEXW wc = { sizeof(wc) };
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"NativeHarnessHost";
    wc.hbrBackground = (HBRUSH)GetStockObject(WHITE_BRUSH);
    RegisterClassExW(&wc);

    HWND host = CreateWindowExW(0, wc.lpszClassName, L"Native harness host",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 80, 80, 900, 700,
        nullptr, nullptr, wc.hInstance, nullptr);
    wprintf(L"host hwnd = %p\n", host);

    if (initFile)
    {
        hr = initFile->Initialize(file, 0);
        wprintf(L"IInitializeWithFile::Initialize hr=0x%08X\n", hr);
    }
    if (preview)
    {
        RECT rc = { 0, 0, 900, 700 };
        hr = preview->SetWindow(host, &rc);
        wprintf(L"SetWindow hr=0x%08X\n", hr);
        hr = preview->DoPreview();
        wprintf(L"DoPreview hr=0x%08X\n", hr);
    }

    wprintf(L"pumping messages for %d s ...\n", seconds);
    DWORD end = GetTickCount() + (DWORD)seconds * 1000;
    while (GetTickCount() < end)
    {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        Sleep(30);
    }

    wprintf(L"host children: %d\n", CountChildren(host));
    SaveWindow(host, outPng);
    wprintf(L"screenshot -> %s\n", outPng);

    if (preview) { preview->Unload(); preview->Release(); }
    if (initFile) initFile->Release();
    unknown->Release();

    wprintf(L"keep the window open 8 s ...\n");
    end = GetTickCount() + 8000;
    while (GetTickCount() < end)
    {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        Sleep(30);
    }

    Gdiplus::GdiplusShutdown(gdipToken);
    wprintf(L"=== done ===\n");
    return 0;
}
