// MarkdownPreviewHandler.h
//
// Native (in-process) Explorer preview handler for Markdown and images.
//
// Why native: on this machine the managed handler could be registered
// perfectly and activated in-process, yet the shell never surfaced it; the
// handlers that DO work here (Windows TXT Previewer, Photos, Edge PDF) are all
// native in-proc COM servers. This DLL follows exactly that shape:
//   InprocServer32 -> this DLL, ThreadingModel=Apartment
//
// Rendering uses WebView2 (Edge runtime, present on the machine) which gives
// modern CSS/GFM output; images are decoded with GDI+.

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <windows.h>
#include <objidl.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <thumbcache.h>      // IInitializeWithItem / IPreviewHandler decls
#include <gdiplus.h>
#include <string>
#include <vector>

// Exactly one translation unit must instantiate the GUIDs declared with
// DEFINE_GUID below; initguid.h turns those declarations into definitions.
// It is included only here (the header), guarded so it happens once.
#ifndef MARKDOWN_PREVIEW_INITGUID
#define MARKDOWN_PREVIEW_INITGUID
#include <initguid.h>
#endif

// ---------------------------------------------------------------------------
// Interface definitions used by the shell's preview machinery.
// IPreviewHandler normally comes from shobjidl_core.h, but we declare the
// vtable order explicitly so the layout cannot drift with SDK versions.
// ---------------------------------------------------------------------------

#ifndef __IPreviewHandler_INTERFACE_DEFINED__
#define __IPreviewHandler_INTERFACE_DEFINED__

MIDL_INTERFACE("8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2")
IPreviewHandler : public IUnknown
{
public:
    virtual HRESULT STDMETHODCALLTYPE SetWindow(HWND hwnd, const RECT *prc) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetRect(const RECT *prc) = 0;
    virtual HRESULT STDMETHODCALLTYPE DoPreview() = 0;
    virtual HRESULT STDMETHODCALLTYPE Unload() = 0;
    virtual HRESULT STDMETHODCALLTYPE SetFocus() = 0;
    virtual HRESULT STDMETHODCALLTYPE QueryFocus(HWND *phwnd) = 0;
    virtual HRESULT STDMETHODCALLTYPE TranslateAccelerator(MSG *pmsg) = 0;
};

#endif

// {8895b1c6-b41f-4c1c-a562-0d564250836f} - shell's sheet-ext GUID for
// "preview handler". NOTE: this is NOT the IPreviewHandler IID above; mixing
// them up was the original bug in this project.
DEFINE_GUID(CLSID_MarkdownPreviewHandler,
    0x3511ea64, 0xb51a, 0x4298, 0xb6, 0xc3, 0x6a, 0xb8, 0x1f, 0x17, 0x9e, 0x1c);
DEFINE_GUID(SHELLEX_PreviewHandler,
    0x8895b1c6, 0xb41f, 0x4c1c, 0xa5, 0x62, 0x0d, 0x56, 0x42, 0x50, 0x83, 0x6f);
DEFINE_GUID(IID_IPreviewHandler_,
    0x8895b1c6, 0xb7f3, 0x4824, 0xb0, 0x22, 0x4f, 0xdd, 0x1c, 0x0f, 0xe5, 0xe2);

// ---------------------------------------------------------------------------
// Minimal COM smart pointer (no ATL/WIL dependency)
// ---------------------------------------------------------------------------

template <class T>
class ComPtr
{
public:
    ComPtr() : p_(nullptr) {}
    ComPtr(const ComPtr& other) : p_(other.p_) { if (p_) p_->AddRef(); }
    ~ComPtr() { Release(); }

    ComPtr& operator=(const ComPtr& other)
    {
        if (this != &other) { if (other.p_) other.p_->AddRef(); Release(); p_ = other.p_; }
        return *this;
    }

    T* operator->() const { return p_; }
    T* get() const { return p_; }
    T** operator&() { return &p_; }
    operator bool() const { return p_ != nullptr; }

    void Release()
    {
        if (p_) { p_->Release(); p_ = nullptr; }
    }

    HRESULT QueryInterface(REFIID riid, void** ppv)
    {
        *ppv = nullptr;
        if (!p_) return E_FAIL;
        return p_->QueryInterface(riid, ppv);
    }

private:
    T* p_;
};

// ---------------------------------------------------------------------------
// Global module state
// ---------------------------------------------------------------------------

extern LONG g_moduleRefCount;
extern HMODULE g_hModule;

void DllAddRef();
void DllRelease();

// Diagnostic log (defined in MarkdownPreviewHandler.cpp). Safe to call from
// DllMain: it only touches the filesystem, no COM/loader locks.
void NativeLog(const wchar_t* fmt, ...);

// ---------------------------------------------------------------------------
// Markdown -> HTML
// ---------------------------------------------------------------------------

// Converts Markdown text to a full HTML document, inlining the stylesheet.
// sourceDir is used to rewrite relative image paths.
std::wstring MarkdownToHtmlDocument(const std::wstring& markdown, const std::wstring& sourceDir);

// ---------------------------------------------------------------------------
// The handler
// ---------------------------------------------------------------------------

class MarkdownPreviewHandler :
    public IPreviewHandler,
    public IInitializeWithStream,
    public IInitializeWithFile,
    public IObjectWithSite,
    public IOleWindow
{
public:
    MarkdownPreviewHandler();

    // IUnknown
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;

    // IInitializeWithStream
    STDMETHOD(Initialize)(IStream* pstream, DWORD grfMode) override;

    // IInitializeWithFile
    STDMETHOD(Initialize)(LPCWSTR pszFilePath, DWORD grfMode) override;

    // IPreviewHandler
    STDMETHOD(SetWindow)(HWND hwnd, const RECT* prc) override;
    STDMETHOD(SetRect)(const RECT* prc) override;
    STDMETHOD(DoPreview)() override;
    STDMETHOD(Unload)() override;
    STDMETHOD(SetFocus)() override;
    STDMETHOD(QueryFocus)(HWND* phwnd) override;
    STDMETHOD(TranslateAccelerator)(MSG* pmsg) override;

    // IObjectWithSite
    STDMETHOD(SetSite)(IUnknown* punkSite) override;
    STDMETHOD(GetSite)(REFIID riid, void** ppvSite) override;

    // IOleWindow
    STDMETHOD(GetWindow)(HWND* phwnd) override;
    STDMETHOD(ContextSensitiveHelp)(BOOL fEnterMode) override;

    // --- accessors used by the WebView2 completion callbacks -------------
    HWND GetChildHwnd() const { return childHwnd_; }
    void SetWebViewEnvironment(void* env);
    void SetWebViewController(void* controller);
    void SetWebViewReady();
    std::wstring BuildHtml() const;

private:
    ~MarkdownPreviewHandler();

    HRESULT CreatePreviewWindow();
    HRESULT LoadWebView();
    HRESULT RenderImage();
    void    ResizeToRect();
    static bool LooksLikeImage(const BYTE* data, size_t len);
    static bool ReadAllBytes(const std::wstring& path, std::vector<BYTE>& out);

    LONG            refCount_;
    HWND            parentHwnd_;
    HWND            childHwnd_;
    RECT            bounds_;
    IUnknown*       site_;

    std::wstring    filePath_;      // when initialized by path
    std::vector<BYTE> bytes_;       // when initialized by stream (or file contents)

    // parsed content
    bool            isImage_;
    std::wstring    markdownText_;  // decoded text for markdown files

    // WebView2 (loaded dynamically so the DLL has no hard dependency)
    void*           webView_;       // ICoreWebView2Controller*
    void*           webViewEnv_;    // ICoreWebView2Environment*
    bool            webViewReady_;
};
