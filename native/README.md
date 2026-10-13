# 原生 Markdown 预览处理器（native）

这是**第二条实现路线**：用 C++ 写一个**原生 in-proc COM 预览处理器**，取代原来的 .NET 版本。

## 为什么要换原生

本机（Windows 11 25H2 / 26200）实测的结论：

| 形态 | 结果 |
|---|---|
| .NET + `mscoree` shim（原方案） | 注册全部正确、进程内 `CoCreateInstance` + 5 个接口 QI 全部成功，但资源管理器始终不实例化它 |
| .NET + `LocalServer32` 进程外 | 我完整实现并跑通了类工厂/激活，但 `IPreviewHandler` 跨进程 **E_NOINTERFACE**（`tlbexp` 只导出 coclass，系统又没有它的 proxy/stub，补需要 `midl`） |
| **原生 in-proc** | **本机所有能正常工作的预览器都是这个形态**：`{1531d583-…}` Windows TXT 预览器、`{d7ac9243-…}` 照片、Edge PDF 预览器 |

所以原生 in-proc 是这台机器上唯一被系统自身证明可行的形态。

另外这条线还顺手修掉了原方案的另一个真 bug：**shellex 注册用错了 GUID**。正确的是

```
{8895b1c6-b41f-4c1c-a562-0d564250836f}   ← shell 解析预览处理器用这个
{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}   ← 这是 IPreviewHandler 的接口 IID，不能用来注册
```

## 目录结构

```
native/
  build_native.cmd        一键编译（要 MSVC）
  build_native.ps1        同上，PowerShell 版，带 -Install / -Test / -Uninstall
  MarkdownPreviewHandler.vcxproj
  MarkdownPreviewNative.def       DLL 导出表
  src/
    MarkdownPreviewHandler.h      接口定义、GUID、精简 ComPtr
    MarkdownPreviewHandler.cpp    处理器实现（WebView2 / GDI+ 渲染）
    MarkdownToHtml.cpp            自研 GFM→HTML 转换器 + 样式表
    DllMain.cpp                   DllMain、GDI+ 生命周期、regsvr32 注册
```

## 编译

### 1. 装 C++ 工具链（一次性）

任选一种：

```powershell
# 方式 A：只装 Build Tools（体积小，推荐）
winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

```text
方式 B：装 Visual Studio Community 时勾选 “使用 C++ 的桌面开发”
```

### 2. 准备 WebView2 头文件与库（建议，但可选）

运行时（Edge WebView2 Runtime）本机**已装**（154.0.4258.62）。编译还需要 SDK 的头文件与导入库：

```powershell
# 在项目根目录执行
mkdir native\webview2 -Force
cd native\webview2
curl.exe -L -o wv2.zip https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/1.0.2592.51
Expand-Archive wv2.zip -DestinationPath . -Force
```

展开后会得到 `build\native\include\WebView2.h`、`build\native\x64\WebView2Loader.dll` 等。把
`-WebView2Dir` 指向 `native\webview2\build\native` 即可。

> **没有 WebView2 也能编译**：不加 `-WebView2Dir` 时，DLL 用 GDI 直接画纯文本作为兜底，
> 预览窗格不会空白，只是没有 Markdown 排版。

### 3. 编译 + 注册 + 测试

```powershell
# 只编译
powershell -ExecutionPolicy Bypass -File .\native\build_native.ps1 -WebView2Dir .\native\webview2\build\native

# 编译 + 注册（需要管理员）
powershell -ExecutionPolicy Bypass -File .\native\build_native.ps1 -WebView2Dir .\native\webview2\build\native -Install

# 注册后测试：脚本会清空日志并等 60 秒
powershell -ExecutionPolicy Bypass -File .\native\build_native.ps1 -Install -Test

# 反注册
powershell -ExecutionPolicy Bypass -File .\native\build_native.ps1 -Uninstall
```

输出：`bin\MarkdownPreviewNative.dll`（以及 `WebView2Loader.dll`，需与 DLL 同目录）。

## 注册内容

`regsvr32` 调用 DLL 自己的 `DllRegisterServer`，写入的键与系统可用的预览器一致：

|注册表键|值|
|-|-|
|`HKLM\SOFTWARE\Classes\CLSID\{3511EA64-…}\InprocServer32`|`<dll 路径>`，`ThreadingModel=Apartment`|
|`HKLM\…\CLSID\{3511EA64-…}` 的 `AppID`|**删除**（有它会把激活推给代进程）|
|`HKLM\…\CurrentVersion\PreviewHandlers\{3511EA64-…}`|`Markdown & Image Preview Handler`|
|`HKLM\…\Shell Extensions\Approved\{3511EA64-…}`|同上|
|`HKLM\SOFTWARE\Classes\<ext>\shellex\{8895b1c6-b41f-…}`|`{3511EA64-…}`（每个扩展名，另写 `SystemFileAssociations` 一份）|

默认认领的扩展名：`.md .markdown .mdown .mkd` + 图片。想改就编辑
`src/DllMain.cpp` 里的 `kExtensions`。

## 排错

处理器会把每一步写进 `%LOCALAPPDATA%\MarkdownPreviewHandler\handler.log`：

```
ctor
IInitializeWithFile::Initialize(D:\...\sample.md)
  read 1363 bytes
SetWindow hwnd=... rect=0,0 600x859
CreatePreviewWindow hwnd=... parent=...
DoPreview isImage=0 markdownLen=1363
WebView2 ready
```

判据：

| 日志情况 | 含义 |
|---|---|
| 有 `ctor` 到最后一步 | 处理器跑通了；若窗格仍空白，是渲染/尺寸问题 |
| 停在某一步 | 那一步失败，按日志定位 |
| 完全没有日志 | DLL 没被加载 —— 检查 `InprocServer32` 路径、`AppID` 是否已删、位数是否匹配 |

## 已知限制

* 只编译 x64（本机 shell 是 64 位）。若要支持 32 位宿主需另出 x86 版本并在 `Wow6432Node` 注册。
* WebView2 的 controller 创建是异步的：`DoPreview` 返回后内容才出现，属正常。
* 图片解码用 GDI+，不支持 WebP（GDI+ 无内置解码器）。
* 链接点击会交给系统默认程序打开。
