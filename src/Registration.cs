using System;
using System.IO;
using Microsoft.Win32;

namespace MarkdownPreview
{
    /// <summary>
    /// 预览处理器的注册 / 反注册实现（等价于手写 regasm 输出，但修正了
    /// 关键细节，见下）。由 <c>MarkdownPreviewHandler.dll -Register</c> 调用，
    /// 因此必须从 64 位进程、以管理员权限运行。
    ///
    /// 与原 install.ps1 相比的三处关键修正：
    ///
    /// 1. AppID 必须是 **64 位** 预览处理器代进程
    ///    {6d2b5079-2f0b-48dd-ab7f-97cec514d30b}（DllSurrogate =
    ///    C:\Windows\System32\prevhost.exe）。
    ///    原脚本用的是 32 位代进程 {534A1E02-D58F-44f0-B58B-36CBED287C7C}
    ///    （DllSurrogate = SysWOW64\prevhost.exe）。本程序集是 AnyCPU 强名称
    ///    程序集，只会加载进 64 位 CLR，指向 32 位代进程时 COM 激活失败，
    ///    资源管理器表现为“无可用预览”，且处理器内部一行日志都不会写。
    ///
    /// 2. 必须写入 HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\
    ///    PreviewHandlers\{CLSID}。缺少该键时资源管理器显示
    ///    “This file can't be previewed”。
    ///
    /// 3. 必须写入 Shell Extensions\Approved 白名单（Win11 24H2+），
    ///    否则扩展被静默忽略。
    /// </summary>
    public static class Registration
    {
        public const string ClsidString = PreviewHandler.ClsidString;
        public const string HandlerName = PreviewHandler.HandlerName;
        public const string ProgId = "MarkdownPreview.PreviewHandler";

        /// <summary>
        /// 注册扩展名预览处理器时使用的 shell GUID。
        ///
        /// ⚠️ 必须用这一个：<c>{8895b1c6-b41f-4c1c-a562-0d564250836f}</c>
        ///
        /// 另一个长得很像的 <c>{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}</c> 是
        /// IPreviewHandler 的 **接口 IID**，不是 shell 解析处理器时用的
        /// shellex 标识。历史上本项目的注册脚本误用了后者，结果是：
        ///   * <c>AssocQueryString(..., IID_IPreviewHandler)</c> 这类查询会“看起来”
        ///     能解析到本 CLSID（因为它就是按 IID 查的）；
        ///   * 但资源管理器真正解析预览处理器时读的是前者，于是永远找不到我们，
        ///     表现为“注册项全都对、日志一行没有”。
        /// </summary>
        public const string PreviewHandlerShellex = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

        /// <summary>IID_IPreviewHandler：仅用于接口声明/查询，不能用于 shellex 注册。</summary>
        public const string IID_IPreviewHandler = "{8895b1c6-b7f3-4824-b022-4fdd1c0fe5e2}";

        /// <summary>64 位预览处理器代进程（system32\prevhost.exe）。</summary>
        public const string SurrogateAppId64 = "{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}";

        /// <summary>32 位预览处理器代进程（SysWOW64\prevhost.exe）。</summary>
        public const string SurrogateAppId32 = "{534A1E02-D58F-44f0-B58B-36CBED287C7C}";

        public const string RuntimeVersion = "v4.0.30319";

        /// <summary>
        /// 默认注册的扩展名。
        ///
        /// ⚠️ 这里**故意不含 .md / .markdown / .mdown / .mkd**：
        /// 目前本机的 .md 预览走「Plain Text 持久处理器」（见 install_text_preview.ps1），
        /// 稳定可用。而本处理器在 Windows 11 25H2 的 prevhost 宿主里初始化会失败，
        /// 一旦把 .md 关联到它，资源管理器会对**所有**类型弹出
        /// “你尝试预览的文件可能对计算机有害…”。等 prevhost 那个问题解决后再加回来。
        /// </summary>
        public static readonly string[] Extensions = new string[]
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico"
        };

        /// <summary>Markdown 扩展名：仅在确认处理器可用时才注册（见 <see cref="Extensions"/> 说明）。</summary>
        public static readonly string[] MarkdownExtensions = new string[]
        {
            ".md", ".markdown", ".mdown", ".mkd"
        };

        // ---------------------------------------------------------------

        public static int Register()
        {
            try
            {
                if (!Is64BitProcess())
                {
                    Console.Error.WriteLine("错误：请用 64 位运行时注册（" +
                        @"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319 下的 CLR）。");
                    return 2;
                }

                Console.WriteLine("== 注册 Markdown & Image 预览处理器 ==");
                Console.WriteLine("CLSID        : " + ClsidString);
                Console.WriteLine("AppID(代进程): " + SurrogateAppId64 + "  (System32\\prevhost.exe)");
                Console.WriteLine();

                WriteClsidKeys();
                Console.WriteLine("[OK] HKLM\\SOFTWARE\\Classes\\CLSID\\" + ClsidString);

                WritePreviewHandlersKey();
                Console.WriteLine("[OK] HKLM\\...\\CurrentVersion\\PreviewHandlers\\" + ClsidString);
                Console.WriteLine("[OK] HKLM\\...\\Shell Extensions\\Approved\\" + ClsidString);

                int n = 0;
                foreach (string ext in Extensions)
                {
                    if (WriteExtension(ext)) n++;
                }
                Console.WriteLine("[OK] 扩展名关联 " + n + " 个：" + string.Join(" ", Extensions));

                TryWriteWow6432MirrorClsid();

                Console.WriteLine();
                return Verify();
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine("错误：需要管理员权限（" + ex.Message + "）");
                return 5;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("注册失败：" + ex);
                return 1;
            }
        }

        public static int Unregister()
        {
            try
            {
                using (RegistryKey classes = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes", true))
                {
                    if (classes == null) return 5;
                    TryDeleteTree(classes, @"CLSID\" + ClsidString);
                    TryDeleteTree(classes, ProgId);
                    foreach (string ext in Extensions)
                    {
                        TryDeleteTree(classes, ext + @"\shellex\" + PreviewHandlerShellex);
                        TryDeleteTree(classes,
                            @"SystemFileAssociations\" + ext + @"\shellex\" + PreviewHandlerShellex);
                    }
                }
                using (RegistryKey classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", true))
                {
                    if (classes != null)
                    {
                        foreach (string ext in Extensions)
                        {
                            TryDeleteTree(classes, ext + @"\shellex\" + PreviewHandlerShellex);
                            TryDeleteTree(classes,
                                @"SystemFileAssociations\" + ext + @"\shellex\" + PreviewHandlerShellex);
                        }
                    }
                }
                using (RegistryKey ph = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers", true))
                {
                    if (ph != null) { try { ph.DeleteSubKeyTree(ClsidString, false); } catch { } }
                }
                using (RegistryKey approved = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", true))
                {
                    if (approved != null) { try { approved.DeleteValue(ClsidString, false); } catch { } }
                }
                Console.WriteLine("[OK] 已反注册。");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("反注册失败：" + ex.Message);
                return 1;
            }
        }

        // ---------------------------------------------------------------
        // 注册细节
        // ---------------------------------------------------------------

        private static void WriteClsidKeys()
        {
            using (RegistryKey classes = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes"))
            {
                using (RegistryKey clsid = classes.CreateSubKey(@"CLSID\" + ClsidString))
                {
                    clsid.SetValue(null, ProgId, RegistryValueKind.String);
                    clsid.SetValue("AppID", SurrogateAppId64, RegistryValueKind.String);
                    clsid.SetValue("DisplayName", HandlerName, RegistryValueKind.String);

                    using (RegistryKey ips = clsid.CreateSubKey("InprocServer32"))
                    {
                        // mscoree.dll 是 .NET COM 的 shim：由它启动 CLR 并转发
                        // DllGetClassObject，不能写托管 DLL 的路径。
                        ips.SetValue(null, "mscoree.dll", RegistryValueKind.String);
                        ips.SetValue("ThreadingModel", "Both", RegistryValueKind.String);
                        ips.SetValue("Class", ProgId, RegistryValueKind.String);
                        ips.SetValue("Assembly", AssemblyIdentity, RegistryValueKind.String);
                        ips.SetValue("RuntimeVersion", RuntimeVersion, RegistryValueKind.String);
                        TryDeleteValue(ips, "CodeBase");

                        using (RegistryKey ver = ips.CreateSubKey(Version4))
                        {
                            ver.SetValue("Class", ProgId, RegistryValueKind.String);
                            ver.SetValue("Assembly", AssemblyIdentity, RegistryValueKind.String);
                            ver.SetValue("RuntimeVersion", RuntimeVersion, RegistryValueKind.String);
                        }
                    }

                    // .NET 实现类标记（regasm 输出之一）
                    clsid.CreateSubKey(
                        @"Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}").Close();

                    using (RegistryKey pid = clsid.CreateSubKey("ProgId"))
                    {
                        pid.SetValue(null, ProgId, RegistryValueKind.String);
                    }
                }

                using (RegistryKey progId = classes.CreateSubKey(ProgId))
                {
                    progId.SetValue(null, ProgId, RegistryValueKind.String);
                    using (RegistryKey c = progId.CreateSubKey("CLSID"))
                    {
                        c.SetValue(null, ClsidString, RegistryValueKind.String);
                    }
                }
            }
        }

        private static void WritePreviewHandlersKey()
        {
            using (RegistryKey ph = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers"))
            {
                ph.SetValue(ClsidString, HandlerName, RegistryValueKind.String);
            }

            using (RegistryKey approved = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"))
            {
                approved.SetValue(ClsidString, HandlerName, RegistryValueKind.String);
            }
        }

        private static bool WriteExtension(string ext)
        {
            using (RegistryKey classes = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes"))
            {
                using (RegistryKey k = classes.CreateSubKey(
                    ext + @"\shellex\" + PreviewHandlerShellex))
                {
                    k.SetValue(null, ClsidString, RegistryValueKind.String);
                }
                // .md 的有效 ProgID 可能被 AppX（记事本）或第三方（坚果云）占用，
                // SystemFileAssociations 是 shell 的兜底查找位置，一并写。
                using (RegistryKey k = classes.CreateSubKey(
                    @"SystemFileAssociations\" + ext + @"\shellex\" + PreviewHandlerShellex))
                {
                    k.SetValue(null, ClsidString, RegistryValueKind.String);
                }
            }

            // 每用户视图同样写一份：当扩展名被“每用户”的 AppX ProgID 接管时
            // （UserChoice -> AppX...），HKCU 的关联优先级更高。
            using (RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            {
                using (RegistryKey k = classes.CreateSubKey(
                    ext + @"\shellex\" + PreviewHandlerShellex))
                {
                    k.SetValue(null, ClsidString, RegistryValueKind.String);
                }
                using (RegistryKey k = classes.CreateSubKey(
                    @"SystemFileAssociations\" + ext + @"\shellex\" + PreviewHandlerShellex))
                {
                    k.SetValue(null, ClsidString, RegistryValueKind.String);
                }
            }
            return true;
        }

        private static void TryWriteWow6432MirrorClsid()
        {
            try
            {
                using (RegistryKey w = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Classes\Wow6432Node\CLSID\" + ClsidString))
                {
                    w.SetValue(null, ProgId, RegistryValueKind.String);
                    w.SetValue("AppID", SurrogateAppId32, RegistryValueKind.String);
                    using (RegistryKey ips = w.CreateSubKey("InprocServer32"))
                    {
                        ips.SetValue(null, "mscoree.dll", RegistryValueKind.String);
                        ips.SetValue("ThreadingModel", "Both", RegistryValueKind.String);
                        ips.SetValue("Class", ProgId, RegistryValueKind.String);
                        ips.SetValue("Assembly", AssemblyIdentity, RegistryValueKind.String);
                        ips.SetValue("RuntimeVersion", RuntimeVersion, RegistryValueKind.String);
                    }
                }
                Console.WriteLine("[OK] 32 位视图 CLSID 镜像（供 32 位宿主使用）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] 32 位视图镜像写入跳过：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------

        /// <summary>输出注册现状汇总，返回 0 表示全部就绪。</summary>
        public static int Verify()
        {
            int problems = 0;
            Console.WriteLine("== 注册校验 ==");

            string ipsV = ReadValue(Registry.LocalMachine,
                @"SOFTWARE\Classes\CLSID\" + ClsidString + @"\InprocServer32", null);
            problems += Check(ipsV == "mscoree.dll", "InprocServer32 = mscoree.dll",
                "InprocServer32 = " + ipsV);

            string appid = ReadValue(Registry.LocalMachine,
                @"SOFTWARE\Classes\CLSID\" + ClsidString, "AppID");
            bool appidOk = string.Equals(appid, SurrogateAppId64, StringComparison.OrdinalIgnoreCase);
            string appidMsg = "AppID = " + (appid ?? "(缺失)");
            if (string.Equals(appid, SurrogateAppId32, StringComparison.OrdinalIgnoreCase))
                appidMsg += "   <-- 错！32 位代进程，64 位程序集无法在其中激活";
            problems += Check(appidOk, "AppID = " + SurrogateAppId64 + " (64 位代进程)", appidMsg);

            string surrogate = ReadValue(Registry.LocalMachine,
                @"SOFTWARE\Classes\AppID\" + SurrogateAppId64, "DllSurrogate");
            bool surrogateOk = !string.IsNullOrEmpty(surrogate) &&
                surrogate.IndexOf("SysWOW64", StringComparison.OrdinalIgnoreCase) < 0;
            problems += Check(surrogateOk, "代进程 DllSurrogate = " + surrogate,
                "代进程 DllSurrogate = " + (surrogate ?? "(缺失)"));

            string ph = ReadValue(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\PreviewHandlers", ClsidString);
            problems += Check(ph != null, "已列入 PreviewHandlers",
                "PreviewHandlers 列表缺少本 CLSID（资源管理器会显示“无法预览此文件”）");

            string approved = ReadValue(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved", ClsidString);
            problems += Check(approved != null, "已在 Approved 白名单", "Approved 白名单缺少本 CLSID");

            foreach (string ext in Extensions)
            {
                string v = ReadValue(Registry.LocalMachine,
                    @"SOFTWARE\Classes\" + ext + @"\shellex\" + PreviewHandlerShellex, null);
                problems += Check(string.Equals(v, ClsidString, StringComparison.OrdinalIgnoreCase),
                    ext + " -> 本处理器", ext + " -> " + (v ?? "(未注册)"));

                string vu = ReadValue(Registry.CurrentUser,
                    @"Software\Classes\" + ext + @"\shellex\" + PreviewHandlerShellex, null);
                problems += Check(string.Equals(vu, ClsidString, StringComparison.OrdinalIgnoreCase),
                    ext + " (HKCU) -> 本处理器", ext + " (HKCU) -> " + (vu ?? "(未注册)"));
            }

            // Markdown 扩展名有意不注册（见 Extensions 注释）：这里只提示，不算问题。
            foreach (string ext in MarkdownExtensions)
            {
                string v = ReadValue(Registry.LocalMachine,
                    @"SOFTWARE\Classes\" + ext + @"\shellex\" + PreviewHandlerShellex, null);
                if (v != null)
                {
                    Console.WriteLine("  [--] " + ext + " -> " + v +
                        "（Markdown 扩展名当前按设计不注册；如需启用请改 Extensions）");
                }
            }

            Console.WriteLine();
            if (problems == 0)
            {
                Console.WriteLine("结果：注册完整。若仍无预览，请重启 explorer.exe 后重试；");
                Console.WriteLine(@"      然后检查 %LOCALAPPDATA%\MarkdownPreviewHandler\handler.log 是否出现 SetWindow/DoPreview。");
            }
            else
            {
                Console.WriteLine("结果：发现 " + problems + " 处问题（见上）。");
            }
            return problems == 0 ? 0 : 3;
        }

        private static int Check(bool ok, string okText, string failText)
        {
            Console.WriteLine((ok ? "  [OK] " : "  [!!] ") + (ok ? okText : failText));
            return ok ? 0 : 1;
        }

        private static string ReadValue(RegistryKey root, string path, string name)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(path))
                {
                    if (k == null) return null;
                    object v = k.GetValue(name);
                    return v == null ? null : v.ToString();
                }
            }
            catch { return null; }
        }

        private static void TryDeleteTree(RegistryKey parent, string path)
        {
            try { parent.DeleteSubKeyTree(path, false); } catch { }
        }

        private static void TryDeleteValue(RegistryKey k, string name)
        {
            try { k.DeleteValue(name, false); } catch { }
        }

        private static string AssemblyIdentity
        {
            get { return typeof(Registration).Assembly.FullName; }
        }

        private static string Version4
        {
            get { return typeof(Registration).Assembly.GetName().Version.ToString(); }
        }

        private static bool Is64BitProcess()
        {
            return IntPtr.Size == 8;
        }

        /// <summary>把程序集复制进 .NET 4 GAC（mscoree shim 只会从 GAC / 宿主目录解析）。</summary>
        public static int InstallToGac()
        {
            try
            {
                string src = typeof(Registration).Assembly.Location;
                if (string.IsNullOrEmpty(src) || !File.Exists(src))
                {
                    Console.Error.WriteLine("找不到程序集文件：" + src);
                    return 1;
                }
                string token = TokenOf(typeof(Registration).Assembly);
                if (string.IsNullOrEmpty(token))
                {
                    Console.Error.WriteLine("程序集未强名称签名，无法装入 GAC。请用 build.cmd 重新编译。");
                    return 2;
                }
                string version = typeof(Registration).Assembly.GetName().Version.ToString();
                string gacDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"Microsoft.NET\assembly\GAC_MSIL\MarkdownPreviewHandler",
                    "v4.0_" + version + "__" + token);
                Directory.CreateDirectory(gacDir);
                string dst = Path.Combine(gacDir, "MarkdownPreviewHandler.dll");
                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[OK] 已在 GAC 中：" + dst);
                    return 0;
                }
                File.Copy(src, dst, true);
                Console.WriteLine("[OK] 已安装到 GAC：" + dst);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("GAC 安装失败：" + ex.Message);
                return 1;
            }
        }

        internal static string TokenOf(System.Reflection.Assembly asm)
        {
            byte[] pk = asm.GetName().GetPublicKeyToken();
            if (pk == null || pk.Length == 0) return null;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (byte b in pk) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
