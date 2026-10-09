# Markdown \& Image Preview Handler

Windows 资源管理器预览处理器插件：在**预览窗格**（Alt+P）中直接渲染 Markdown 文档与图片文件，无需打开外部应用。

**仓库**：[GitHub](https://github.com/johnhzc/markdown-preview-handler) · [Gitee](https://gitee.com/kongjingnengbai/markdown-preview-handler) ｜ **协议**：[MIT](LICENSE)

> ## ⚠️ 2026-10 实测结论（Windows 11 25H2 / build 26200）
>
> ### 一、最关键的坑：注册时用错了 GUID
>
> |用途|GUID|本项目历史|
> |-|-|-|
> |shell 解析扩展名预览处理器|`{8895b1c6-b41f-4c1c-a562-0d564250836f}`|❌ 一直用错|
> |`IPreviewHandler` 的接口 IID|`{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}`|✅ 被误当成上面那个|
>
> 两者只差中段。误用后会出现一种极具误导性的现象：
> `AssocQueryString(ASSOCSTR_SHELLEXTENSION, ext, IID_IPreviewHandler)` 这类查询
> **能解析到本 CLSID**（因为它就是按 IID 查的），但资源管理器解析预览处理器时读的是
> 前一个 GUID，于是**永远找不到我们**——表现为"注册项逐条都对、处理器日志一行没有"。
> `src/Registration.cs` 已改为使用正确的 `{…b41f-…}`。
>
> ### 二、改用正确 GUID 之后
>
> * 资源管理器**确实会调用**本处理器（`prevhost.exe` 里出现 `mscoree.dll`/`clr.dll`），
>   但初始化阶段失败，预览窗格显示"由于 Markdown & Image Preview Handler 中出现错误，无法预览此文件"。
> * 同一份程序集用 `CoCreateInstance` 在本机激活时**全链路成功**
>   （`MODULE LOADED → InitializeWithFile → SetWindow → DoPreview`，见 `test/PreviewHarness.cs`），
>   说明渲染代码本身没问题，问题只出现在 `prevhost` 这个特定宿主里（CLR 起来了但未执行托管代码）。
> * 结论修正：**不是 Windows 拒绝第三方预览处理器**，而是注册 GUID 用错 + `prevhost` 宿主内的初始化问题。
>   这一条尚未解决，留待后续（建议用 Process Monitor 抓 shell 侧真实调用）。
>
> ### 三、一句安全警告的来源
>
> 注册在**错误 GUID** 上的处理器加载失败时，资源管理器会对**所有**文件类型弹出
> "你尝试预览的文件可能对计算机有害…"。清理掉那些残留键后即恢复正常。
>
> ### 四、当前采用的方案：`.md` 使用纯文本预览
>
> 系统自带文本预览走 **Plain Text 持久处理器** `{5e941d80-bf96-11cd-b579-08002b30bfeb}`
> （`.txt`/`.cs`/`.js`/`.sql` 都是它）。把 `.md` 接到同一个处理器上，预览窗格即显示
> Markdown 源码，稳定可靠。见下方「Markdown 纯文本预览」。

## 功能

* **Markdown 渲染**：内置轻量 GFM 风格解析器（零第三方依赖），支持标题、嵌套列表、任务列表、表格（含对齐）、围栏代码块、行内代码、引用块（可嵌套）、粗体/斜体/删除线、链接/图片/自动链接；文档内相对路径的图片与链接自动解析为绝对地址
* **图片渲染**：JPG / PNG / GIF（含动画）/ BMP / TIFF / ICO，自动适应窗口缩放、按 EXIF Orientation 校正手机照片方向
* **智能初始化**：同时实现 `IInitializeWithFile` 与 `IInitializeWithStream`；流初始化无扩展名时按魔数与文本特征嗅探内容类型
* **编码兼容**：UTF-8（含/不含 BOM）、UTF-16、GBK 自动识别
* **安全模型**：运行在系统代进程 `prevhost.exe` 中，不侵入 Explorer 主进程；文件解析异常只影响当前预览，显示错误提示条

## 架构

```
Explorer 预览窗格
   │ CoCreateInstance(CLSID)
   ▼
prevhost.exe（64位代进程，隔离运行）
   │ 加载 MarkdownPreviewHandler.dll（.NET Framework 4.x）
   ▼
PreviewHandler : IPreviewHandler        ← COM 接口实现
   │  ├─ IInitializeWithFile/Stream     ← 文件数据传入
   │  └─ IObjectWithSite                ← 站点对象
   ▼
┌─ .md / .markdown 等 ──────────┐   ┌─ .jpg / .png / .gif 等 ─┐
│ MarkdownConverter（自研解析器）│   │ ImagePreviewControl      │
│   → HTML                       │   │ （GDI+，Zoom/EXIF/GIF）  │
│   → MarkdownPreviewControl     │   └──────────────────────────┘
│     （内置 IE 引擎渲染）        │
└────────────────────────────────┘
```

关键实现要点：

* `SetWindow` 收到宿主 HWND 后，用 `SetParent` 把 WinForms 控件挂为子窗口，`SetRect`/`SetWindowPos` 控制布局
* COM 注册键结构与 `regasm /codebase` 输出一致（`Class`/`Assembly`/`RuntimeVersion`/`CodeBase`），由 mscoree shim 在代进程中启动 CLR
* 代进程 AppID：`{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}`（`System32\prevhost.exe`）

## 构建

```
build.cmd
```

使用系统自带的 .NET Framework 4 编译器（无需安装 SDK/VS），输出 `bin\MarkdownPreviewHandler.dll`（约 48 KB，单文件零依赖）。

> `bin/` 未纳入版本管理；`src/handler.snk` 是开发用的强名称密钥，随仓库一起提供以便直接编译，**请勿复用到其他项目**。

## 仓库与同步

|平台|地址|
|-|-|
|GitHub|https://github.com/johnhzc/markdown-preview-handler|
|Gitee|https://gitee.com/kongjingnengbai/markdown-preview-handler|

已配置一个 `all` remote，一次推送两个平台：

```bash
git push all main
```

## Markdown 纯文本预览（本机实际使用的方案）

`.md` 目前接到系统自带的 **Plain Text 持久处理器**上（`.txt`/`.cs`/`.js`/`.sql` 用的就是它），
预览窗格直接显示 Markdown 源码。渲染版处理器在本机的状态见顶部说明。

```powershell
# 每用户注册，不需要管理员；可随时撤销
powershell -ExecutionPolicy Bypass -File .\install_text_preview.ps1
powershell -ExecutionPolicy Bypass -File .\install_text_preview.ps1 -Remove
```

写入内容（仅 HKCU）：

|注册表键|值|
|-|-|
|`HKCU\Software\Classes\.md\PersistentHandler`|`{5e941d80-bf96-11cd-b579-08002b30bfeb}`（Plain Text）|
|`HKCU\Software\Classes\.md\PerceivedType`|`text`|

`.markdown / .mdown / .mkd` 同样处理。当前解析结果：`.md` → `{1531d583-…}`（Windows TXT 预览器）。

默认打开程序（双击）由 `UserChoice` 决定，该键受 Hash 保护、只能通过 GUI 修改：
右键 `.md` → 打开方式 → 选择其他应用。

## 安装 / 卸载

以**管理员身份**打开 PowerShell，进入项目目录：

```powershell
# 安装（拷贝 DLL 到 Program Files、刷新 GAC、写注册表、重启 Explorer）
.\install.ps1

# 或者：已经装歪了（注册表状态不一致），一键修复；脚本会自行提权
.\fix-preview.ps1

# 卸载（清理注册表键、GAC 副本与安装目录）
.\install.ps1 -Uninstall
```

安装内容：

|注册表键|作用|
|-|-|
|`HKLM\SOFTWARE\Classes\CLSID\{3511EA64-…}`|COM 类注册（InprocServer32 = `mscoree.dll` + Assembly/RuntimeVersion）|
|同上的 `AppID` 值|`{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}`（prevhost 代进程）|
|`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers\{3511EA64-…}`|预览处理器系统列表（**缺失时显示“无法预览此文件”**）|
|`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved`|Win11 24H2+ 扩展白名单（**缺失会被静默忽略**）|
|`HKLM\SOFTWARE\Classes\<ext>\shellex\{8895b1c6-…}`|扩展名关联（HKCU 视图同步写一份）|
|`…\Classes\SystemFileAssociations\<ext>\shellex\{…}`|扩展名兜底关联|
|`%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\…`|强名称程序集的 GAC 副本（mscoree shim 只从 GAC / 宿主目录解析）|

注册的扩展名：`.md .markdown .mdown .mkd` + `.jpg .jpeg .png .gif .bmp .tif .tiff .ico`

> **注意**：图片扩展名的关联会**覆盖 Windows 内置图片预览**；如需保留系统默认行为，编辑 `src/Registration.cs` 中的 `Extensions` 列表去掉图片扩展名即可。

安装后按提示重启 Explorer，按 `Alt+P` 打开预览窗格测试。

## 自检与排错

```powershell
powershell -ExecutionPolicy Bypass -File .\test\check_preview.ps1
```

脚本按四步给出结论：用 shell 同源的 `AssocQueryString` 看 `.md` 解析到谁、
检查 CLSID / AppID / 代进程、检查 PreviewHandlers / Approved / GAC、
最后读取处理器日志。

**关键判据**：处理器日志 `%LOCALAPPDATA%\MarkdownPreviewHandler\handler.log`

* 出现 `SetWindow` / `DoPreview: file=…` —— shell 已找到并激活处理器，问题在渲染侧；
* **完全没有新增记录** —— shell 根本没激活处理器，问题在注册侧
  （AppID / PreviewHandlers / shellex / Approved 任一不对都会这样，且不会留下任何报错）。

## 本地验证渲染效果

```
test\\run\_render\_test.cmd
```

用 `test\\sample.md` 生成 `test\\preview\_render\_test.html`，双击在浏览器中查看转换器输出（浏览器渲染效果优于内置 IE 引擎，但内容一致）。

## 已知限制

* **渲染引擎**：HTML 由系统内置 IE（Trident）引擎渲染，现代 CSS 特性部分受限；预览场景所用样式均兼容 IE11
* **WebP**：GDI+ 不支持 WebP 解码，未注册 `.webp`（需 WebP codec 或 WebView2 渲染时再加）
* **首次预览延迟**：prevhost 冷启动 CLR 约 0.5–2 秒，属正常现象
* **链接点击**：在预览窗格点击链接会转交系统默认程序打开，不会在窗格内跳转
* **表格内竖线**：单元格中的 `\\|` 转义未实现

## 故障排查

|现象|处理|
|-|-|
|预览窗格无内容|确认已按 Alt+P 开启预览窗格；重启 Explorer|
|显示“无法预览此文件”|多半是 `HKLM\…\CurrentVersion\PreviewHandlers` 里没有本 CLSID；跑 `install.ps1`|
|显示“无可用预览”/什么都没有|`handler.log` 无记录 = shell 没激活处理器；跑 `fix-preview.ps1`，再跑 `test\check_preview.ps1`|
|`.md` 被应用商店版记事本接管|已通过 `HKCU\Software\Classes\<ext>\shellex` 与 `SystemFileAssociations` 双保险覆盖，无需改默认打开方式|
|完全不触发|检查组策略是否禁用第三方预览处理器（`DisablePreviewHandlers`）|
|32 位软件的预览对话框不生效|正常现象，本插件只注册到 64 位视图|

## 关键实现备忘（踩过的坑）

1. **`AppID` 决定代进程**：写 `{6d2b5079-…}`（prevhost）。若错写成 32 位代进程
   `{534A1E02-…}`，64 位 CLR 无法在其中加载 AnyCPU 强名称程序集，COM 激活失败，
   资源管理器只显示“无可用预览”，处理器日志一行都不会有。
2. **`PreviewHandlers` 列表是必需的**，缺了会显示“无法预览此文件”。
3. **`mscoree` shim 只从 GAC / 宿主目录解析程序集**，`CodeBase` 会被忽略，
   所以必须把强名称程序集放进 `%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\…`。
4. **PowerShell 不能用来“试加载”新版程序集做验证**：程序集标识相同，绑定一定会
   命中已存在的 GAC 旧副本，看起来像“类型不存在”。必须先用文件复制刷新 GAC，
   再 `LoadFrom`（`install.ps1` / `fix-preview.ps1` 就是按这个顺序做的）。
5. **宿主线程是 MTA**（日志里能看到 `MTA`），而 WebBrowser/ActiveX 要求 STA，
   所以 UI 一律委托给内部 STA 线程构建并泵消息。

## 进阶路线：WebView2 渲染

本机已检测到 Edge WebView2 运行时（154.x）。如需 Chrome 级渲染质量，可给 `MarkdownPreviewControl` 换用 `Microsoft.Web.WebView2.WinForms.WebView2`（NuGet：`Microsoft.Web.WebView2`），要点：

1. `CoreWebView2Environment.CreateAsync(null, udfPath)` 指定独立用户数据目录（按进程加随机后缀，避免多实例锁冲突）
2. `NavigateToString(html)` 替代 `DocumentText`；初始化是异步的，在 `DoPreview` 中 awaiting 完成后再显示
3. 安装脚本需把 `Microsoft.Web.WebView2.Core.dll`、`WinForms.dll`、`WebView2Loader.dll` 一并拷贝到安装目录
4. `.webp`、`.avif`、`.svg` 可随后加入 `Extensions`

