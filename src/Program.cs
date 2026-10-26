/* Qoder Status Bar 一键安装器
 *
 * 双击这个 exe，把 Qoder 桌面端输入框底栏的用量读数装好：
 *   1. 从 CNB release 下载插件包（SHA256 校验，对不上就中止）
 *   2. 解压到临时目录，把 runtime/ 复制到 %USERPROFILE%\.qoder\qoder-statusbar
 *   3. 机器上没有 Node 时，用 Qoder.exe 的硬链接做一个 node.exe（v24 运行时）
 *   4. 备份并替换桌面/开始菜单里的 Qoder 快捷方式（隐藏窗口启动）
 *   5. 拉起 Qoder（带 --remote-debugging-port）和后台注入器
 *
 * 为什么要等 Qoder 退出：注入器要用 CDP 端口连渲染进程，而已经在跑的 Qoder
 * 没开这个端口。必须先完全退出，再由我们的快捷方式带端口启动。
 *
 * 为什么用 C# 而不是 Python/Node：exe 要在任何 Windows 上双击就跑，
 * 不该要求对方先装运行时。csc.exe（.NET Framework 4 自带）编出来的 exe
 * 只要有 Windows 就有。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

internal static class Program
{
    private const string ProductName = "Qoder Status Bar";
    private const string PluginVersion = "1.0.1";

    /* 换版本时这三处要一起改：PluginVersion / ZipUrl / ZipSha256 */
    private const string ZipUrl =
        "https://cnb.cool/kybll/Qoder-StatusBar/-/releases/download/v1.0.1/Qoder-Status-TypeBar-v1.0.1.zip";
    private const string ZipSha256 = "de279c648fad6e8c4bd53698b0de0c1f15aff534be6e2835c3cb28fb795b8f0c";

    private const int WaitTimeoutSeconds = 1800;   /* 等 Qoder 退出，最多 30 分钟 */
    private const int PollIntervalMs = 800;

    private static readonly List<string> Log = new List<string>();
    private static int _port = 9222;
    private static string _installDirOverride;
    private static string _shortcutDirOverride;

    private static string InstallDir
    {
        get
        {
            if (!string.IsNullOrEmpty(_installDirOverride)) return _installDirOverride;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".qoder", "qoder-statusbar");
        }
    }

    private static string DataDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Qoder-Status-TypeBar-Installer");
        }
    }

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try { Console.Title = "Qoder 用量状态条 · 一键安装"; } catch { }

        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            PrintHelp();
            return 0;
        }

        bool noRestart = HasFlag(args, "--no-restart");
        bool dryRun = HasFlag(args, "--dry-run");
        bool uninstall = HasFlag(args, "--uninstall");
        string qoderDirArg = GetArgValue(args, "--qoder-dir");
        string offlineZip = GetArgValue(args, "--offline");
        string portArg = GetArgValue(args, "--port");
        string installDirArg = GetArgValue(args, "--install-dir");
        if (!string.IsNullOrEmpty(installDirArg))
            _installDirOverride = Path.GetFullPath(installDirArg);
        _shortcutDirOverride = GetArgValue(args, "--shortcut-dir");
        bool noWait = HasFlag(args, "--no-wait");
        bool noShortcut = HasFlag(args, "--no-shortcut");
        if (!string.IsNullOrEmpty(portArg))
        {
            int p;
            if (int.TryParse(portArg, out p) && p > 1024 && p < 65536) _port = p;
        }

        Say("==================================================");
        Say("  Qoder 用量状态条 · 一键安装器");
        Say("  v" + PluginVersion + "  (https://cnb.cool/kybll/Qoder-StatusBar)");
        Say("==================================================");
        Say("");

        /* 1. 定位 Qoder */
        QoderInfo qoder = null;
        if (!string.IsNullOrEmpty(qoderDirArg))
        {
            qoder = MakeQoderInfo(Path.Combine(qoderDirArg, "Qoder.exe"), "命令行指定");
            if (qoder == null)
                Say("      [x] 指定的目录里找不到 Qoder.exe 或 resources\\app.asar: " + qoderDirArg);
        }
        if (qoder == null) qoder = FindQoder();
        if (qoder == null)
        {
            Say("[x] 没有找到 Qoder 桌面端。");
            Say("");
            Say("    试过的位置：");
            Say("      · 正在运行的 Qoder 进程");
            Say("      · 桌面与开始菜单里的 Qoder 快捷方式");
            Say("      · 卸载注册表项");
            Say("      · 常见的默认安装目录");
            Say("      · 全盘按 resources\\app.asar 特征扫描");
            Say("");
            Say("    如果 Qoder 装在非常规位置，可以手动指定：");
            Say("      Install-Qoder-Status-TypeBar.exe --qoder-dir \"D:\\Apps\\Qoder\"");
            return Finish(2);
        }
        Say("[1/5] Qoder: " + qoder.ExePath);
        Say("      定位方式: " + qoder.Source);

        if (uninstall)
        {
            Say("");
            Say("[2/5] 卸载模式：结束注入器、恢复原始快捷方式、删除安装目录");
            if (!noShortcut) UninstallShortcuts(qoder);
            else Say("      （--no-shortcut：不改快捷方式）");
            KillInjectors();
            try
            {
                if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
                Say("      已删除: " + InstallDir);
            }
            catch (Exception e) { Say("      [x] 删除失败: " + e.Message); }
            Say("");
            Say("  卸载完成。Qoder 本身没有被动过。");
            return Finish(0);
        }

        /* 2. 下载 / 校验 / 解压 */
        Say("");
        Say("[2/5] 获取插件包 v" + PluginVersion + " ...");
        string zipPath;
        if (!string.IsNullOrEmpty(offlineZip))
        {
            zipPath = Path.GetFullPath(offlineZip);
            if (!File.Exists(zipPath))
            {
                Say("[x] --offline 指定的文件不存在: " + zipPath);
                return Finish(2);
            }
            if (!VerifyZip(zipPath)) return Finish(1);
        }
        else
        {
            if (!FetchPackage(out zipPath)) return Finish(1);
        }

        string extractDir = Path.Combine(DataDir, "packages", "v" + PluginVersion);
        if (!ExtractZip(zipPath, extractDir))
        {
            Say("[x] 解压失败，已中止（没有改动任何文件）。");
            return Finish(1);
        }
        string runtimeDir = LocateRuntimeDir(extractDir);
        if (runtimeDir == null)
        {
            Say("[x] 解压后找不到 runtime\\injector.mjs，包结构不对。");
            return Finish(1);
        }
        Say("      运行时: " + runtimeDir);

        if (dryRun)
        {
            Say("");
            Say("[3/5] --dry-run：跳过等待进程。");
            Say("[4/5] --dry-run：跳过安装。");
            Say("[5/5] --dry-run：跳过快捷方式。");
            Say("");
            Say("  下载、校验、解压全部通过，可以正式安装。");
            return Finish(0);
        }

        /* 3. 等 Qoder 退出 */
        Say("");
        Say("[3/5] 检查 Qoder 进程...");
        KillInjectors();
        if (!noWait && IsQoderRunning())
        {
            Say("      检测到 Qoder 正在运行。");
            Say("");
            Say("      请手动完全退出 Qoder：");
            Say("        1. 关掉所有 Qoder 窗口");
            Say("        2. 在系统托盘（右下角隐藏图标里）右键 Qoder，选择\"退出\"");
            Say("");
            Say("      只关窗口不够 —— 托盘进程还在时会占用调试端口。");
            Say("      正在等待（最多 " + (WaitTimeoutSeconds / 60) + " 分钟）...");
            if (!WaitForQoderExit(WaitTimeoutSeconds))
            {
                Say("");
                Say("[x] 等待超时，Qoder 仍未退出，已中止（没有改动任何文件）。");
                return Finish(3);
            }
        }
        if (noWait) Say("      （--no-wait：跳过进程检查）");
        else Say("      Qoder 已退出。");

        /* 4. 安装运行时 */
        Say("");
        Say("[4/5] 安装运行时到 " + InstallDir);
        if (!InstallRuntime(runtimeDir, qoder))
        {
            Say("[x] 安装失败。");
            return Finish(1);
        }
        Say("      完成。");

        /* 5. 快捷方式 + 启动 */
        Say("");
        Say("[5/5] 配置桌面快捷方式...");
        if (!noShortcut) InstallShortcuts(qoder);
        else Say("      （--no-shortcut：跳过）");

        Say("");
        Say("==================================================");
        Say("  安装成功。");
        Say("==================================================");
        Say("");
        Say("  · 以后请用桌面上的 Qoder 快捷方式启动（图标不变）。");
        Say("  · 原始快捷方式已备份为「Qoder（原始启动）.lnk」。");
        Say("  · 打开任意聊天会话，内置上下文控件左侧会出现用量读数。");
        Say("  · 卸载：运行本 exe 加 --uninstall。");
        Say("");

        if (!noRestart)
        {
            Say("  正在启动 Qoder...");
            StartStatusBar();
            Say("  已启动（启动日志: " + Path.Combine(InstallDir, "logs", "launcher.log") + "）");
        }
        else
        {
            Say("  （--no-restart：未自动启动，请手动双击桌面 Qoder 快捷方式）");
        }
        return Finish(0);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Qoder 用量状态条 · 一键安装器 v" + PluginVersion);
        Console.WriteLine("");
        Console.WriteLine("用法: Install-Qoder-Status-TypeBar.exe [选项]");
        Console.WriteLine("");
        Console.WriteLine("  --qoder-dir <路径>   手动指定 Qoder 安装目录（默认自动定位）");
        Console.WriteLine("  --port <端口>        CDP 调试端口（默认 9222，被占用时自动换）");
        Console.WriteLine("  --no-restart         安装后不自动启动 Qoder");
        Console.WriteLine("  --offline <zip>      使用本地插件包，不联网下载");
        Console.WriteLine("  --dry-run            只做下载/校验/解压，不改动任何文件");
        Console.WriteLine("  --uninstall          卸载：恢复原始快捷方式并删除运行时");
        Console.WriteLine("");
        Console.WriteLine("以下选项主要用于开发和测试：");
        Console.WriteLine("  --install-dir <路径>  指定安装目录（默认 %USERPROFILE%\\.qoder\\qoder-statusbar）");
        Console.WriteLine("  --shortcut-dir <路径> 只处理指定目录里的快捷方式");
        Console.WriteLine("  --no-wait             不等待 Qoder 退出");
        Console.WriteLine("  --no-shortcut         不改快捷方式");
        Console.WriteLine("");
        Console.WriteLine("安装前请完全退出 Qoder（托盘图标也要退出）。");
    }

    /* ------------------------------------------------------------ 下载与解压 */

    private static bool FetchPackage(out string zipPath)
    {
        zipPath = Path.Combine(DataDir, "packages", "Qoder-Status-TypeBar-v" + PluginVersion + ".zip");
        try { Directory.CreateDirectory(Path.GetDirectoryName(zipPath)); }
        catch (Exception e)
        {
            Say("[x] 无法创建工作目录: " + e.Message);
            return false;
        }

        if (File.Exists(zipPath))
        {
            string have = Sha256File(zipPath);
            if (have == ZipSha256)
            {
                Say("      已有缓存，哈希一致");
                return true;
            }
            Say("      缓存哈希不符，重新下载");
            try { File.Delete(zipPath); } catch { }
        }

        Say("      下载 " + ZipUrl);
        if (!DownloadFile(ZipUrl, zipPath))
        {
            Say("[x] 下载失败。检查网络，或到 release 页面手动下载 zip 后用 --offline 安装。");
            return false;
        }
        if (!VerifyZip(zipPath)) return false;
        return true;
    }

    private static bool VerifyZip(string zipPath)
    {
        string got = Sha256File(zipPath);
        if (string.Equals(got, ZipSha256, StringComparison.OrdinalIgnoreCase))
        {
            Say("      校验通过 (" + new FileInfo(zipPath).Length + " 字节)");
            return true;
        }
        Say("[x] 校验失败：期望 " + ShortHash(ZipSha256) + "，实得 " + ShortHash(got));
        Say("    文件可能下载不完整或被篡改，已中止。");
        try { File.Delete(zipPath); } catch { }
        return false;
    }

    private static string ShortHash(string h)
    {
        if (string.IsNullOrEmpty(h)) return "(空)";
        return h.Length > 16 ? h.Substring(0, 16) + "…" : h;
    }

    private static bool DownloadFile(string url, string dest)
    {
        try
        {
            ServicePointManager.SecurityProtocol =
                (SecurityProtocolType)3072 | (SecurityProtocolType)12288;  /* TLS 1.2 / 1.3 */
        }
        catch { }

        var client = new WebClient();
        client.Headers.Add("User-Agent", "Qoder-Status-TypeBar-Installer/" + PluginVersion);
        try
        {
            client.DownloadFile(url, dest);
            return true;
        }
        catch (Exception e)
        {
            Say("      [x] " + e.Message);
            return false;
        }
        finally { client.Dispose(); }
    }

    private static string Sha256File(string path)
    {
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(path))
        {
            return BitConverter.ToString(sha.ComputeHash(fs))
                .Replace("-", "").ToLowerInvariant();
        }
    }

    private static bool ExtractZip(string zipPath, string destDir)
    {
        try
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            Directory.CreateDirectory(destDir);
            string root = Path.GetFullPath(destDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                root += Path.DirectorySeparatorChar;

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        Say("      [x] zip 内含越界路径，已中止: " + entry.FullName);
                        return false;
                    }
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
            return true;
        }
        catch (Exception e)
        {
            Say("      [x] 解压失败: " + e.Message);
            return false;
        }
    }

    /* zip 里可能有一层顶层目录（Qoder-Status-TypeBar/runtime/...），也可能直接是 runtime/ */
    private static string LocateRuntimeDir(string extractDir)
    {
        if (File.Exists(Path.Combine(extractDir, "runtime", "injector.mjs")))
            return Path.Combine(extractDir, "runtime");
        if (File.Exists(Path.Combine(extractDir, "injector.mjs")))
            return extractDir;
        try
        {
            foreach (var d in Directory.GetDirectories(extractDir))
            {
                if (File.Exists(Path.Combine(d, "runtime", "injector.mjs")))
                    return Path.Combine(d, "runtime");
                if (File.Exists(Path.Combine(d, "injector.mjs")))
                    return d;
            }
        }
        catch { }
        return null;
    }

    /* ------------------------------------------------------------ 安装运行时 */

    private static bool InstallRuntime(string runtimeDir, QoderInfo qoder)
    {
        try
        {
            Directory.CreateDirectory(InstallDir);

            foreach (var f in Directory.GetFiles(runtimeDir))
            {
                string name = Path.GetFileName(f);
                if (name == "injector.pid") continue;   /* 旧 pid 不能带过来 */
                File.Copy(f, Path.Combine(InstallDir, name), true);
            }
            foreach (var d in Directory.GetDirectories(runtimeDir))
            {
                string name = Path.GetFileName(d);
                CopyDirectory(d, Path.Combine(InstallDir, name));
            }

            /* 记录 Qoder 实际路径：别人的机器不一定装在默认目录 */
            File.WriteAllText(
                Path.Combine(InstallDir, "qoder-path.txt"),
                qoder.ExePath,
                new UTF8Encoding(false));

            /* CDP 端口写进 port.txt；被占用时启动器会自动换一个并回写 */
            File.WriteAllText(
                Path.Combine(InstallDir, "port.txt"),
                _port.ToString(),
                new UTF8Encoding(false));

            EnsureNodeRuntime(qoder);
            return true;
        }
        catch (Exception e)
        {
            Say("      [x] " + e.Message);
            Say("      提示：如果 Qoder 装在 Program Files 下，尝试用管理员身份运行本程序。");
            return false;
        }
    }

    /* 没有 Node 的机器：用 Qoder.exe 的硬链接做 node.exe。
     * Electron 二进制带 ELECTRON_RUN_AS_NODE=1 就是完整的 Node v24；
     * 只需要把 icudtl.dat 和 v8_context_snapshot.bin 放到它旁边。
     * 硬链接不占额外磁盘（同一个卷）；跨卷时退回复制。 */
    private static void EnsureNodeRuntime(QoderInfo qoder)
    {
        string nodeExe = Path.Combine(InstallDir, "node.exe");
        if (File.Exists(nodeExe)) return;

        bool linked = false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "fsutil.exe",
                Arguments = "hardlink create \"" + nodeExe + "\" \"" + qoder.ExePath + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                linked = p.ExitCode == 0 && File.Exists(nodeExe);
            }
        }
        catch { }

        if (!linked)
        {
            try
            {
                File.Copy(qoder.ExePath, nodeExe, true);
                linked = File.Exists(nodeExe);
            }
            catch { }
        }

        if (!linked)
        {
            Say("      [i] 没能创建 node.exe，将直接用 Qoder.exe 当 Node 运行时（可用）");
            return;
        }

        try
        {
            string dir = Path.GetDirectoryName(qoder.ExePath);
            foreach (var f in new[] { "icudtl.dat", "v8_context_snapshot.bin" })
            {
                string src = Path.Combine(dir, f);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(InstallDir, f), true);
            }
        }
        catch { }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /* ------------------------------------------------------------ 快捷方式 */

    private static void InstallShortcuts(QoderInfo qoder)
    {
        var targets = new List<string>();

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        string commonStart = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        string pinned = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

        if (!string.IsNullOrEmpty(_shortcutDirOverride))
        {
            CollectShortcuts(_shortcutDirOverride, qoder.ExePath, targets);
        }
        else
        {
            foreach (var dir in new[] { desktop, commonDesktop, startMenu, commonStart, pinned })
                CollectShortcuts(dir, qoder.ExePath, targets);
        }

        if (targets.Count == 0)
        {
            string lnk = Path.Combine(
                string.IsNullOrEmpty(_shortcutDirOverride) ? desktop : _shortcutDirOverride,
                "Qoder.lnk");
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            Say("      没有找到现成的 Qoder 快捷方式，新建: " + lnk);
            targets.Add(lnk);
        }

        foreach (var lnk in targets)
        {
            try
            {
                string backup = BackupPathFor(lnk);
                bool isOurs = PointsToStatusBar(lnk);
                if (!isOurs && !File.Exists(backup))
                {
                    File.Copy(lnk, backup, true);
                    Say("      已备份: " + backup);
                }
                WriteStatusBarShortcut(lnk, qoder.ExePath);
                Say("      已更新: " + lnk);
            }
            catch (Exception e)
            {
                Say("      [x] 处理快捷方式失败 " + lnk + ": " + e.Message);
            }
        }
    }

    private static void CollectShortcuts(string dir, string qoderExe, List<string> output)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                if (name.IndexOf("original launcher", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (name.IndexOf("原始启动", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string target = ReadShortcutTarget(f);
                if (string.IsNullOrEmpty(target)) continue;
                string exeName = Path.GetFileName(target);
                bool isQoder = string.Equals(target, qoderExe, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(exeName, "Qoder.exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(exeName, "Qoder Launcher.exe", StringComparison.OrdinalIgnoreCase);
                if (isQoder && !output.Contains(f))
                {
                    output.Add(f);
                }
            }
        }
        catch { }
    }

    private static bool PointsToStatusBar(string lnk)
    {
        string target = ReadShortcutTarget(lnk);
        if (string.IsNullOrEmpty(target)) return false;
        if (!target.EndsWith("wscript.exe", StringComparison.OrdinalIgnoreCase)) return false;
        string args = ReadShortcutArguments(lnk);
        return args != null && args.IndexOf("launch-hidden.vbs", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void WriteStatusBarShortcut(string lnk, string qoderExe)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new Exception("WScript.Shell 不可用");
        object shell = Activator.CreateInstance(shellType);
        try
        {
            object shortcut = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            if (shortcut == null) throw new Exception("无法创建快捷方式");
            try
            {
                Set(shortcut, "TargetPath", Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe"));
                Set(shortcut, "Arguments", "\"" + Path.Combine(InstallDir, "launch-hidden.vbs") + "\"");
                Set(shortcut, "WorkingDirectory", InstallDir);
                Set(shortcut, "IconLocation", qoderExe + ",0");
                Set(shortcut, "Description", "Qoder（带用量状态条）");
                shortcut.GetType().InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut); } catch { } }
        }
        finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { } }
    }

    private static void Set(object shortcut, string prop, string value)
    {
        shortcut.GetType().InvokeMember(prop,
            System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { value });
    }

    private static string ReadShortcutTarget(string lnk)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                if (shortcut == null) return null;
                try { return shortcutTypeInvoke(shortcut, "TargetPath"); }
                finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut); } catch { } }
            }
            finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { } }
        }
        catch { }
        return null;
    }

    private static string ReadShortcutArguments(string lnk)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                if (shortcut == null) return null;
                try { return shortcutTypeInvoke(shortcut, "Arguments"); }
                finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut); } catch { } }
            }
            finally { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { } }
        }
        catch { }
        return null;
    }

    private static string shortcutTypeInvoke(object shortcut, string prop)
    {
        try
        {
            return (string)shortcut.GetType().InvokeMember(prop,
                System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
        }
        catch { return null; }
    }

    /* 备份名沿用之前版本的中文名；同时兼容 ASCII 名 */
    private static string BackupPathFor(string lnk)
    {
        string dir = Path.GetDirectoryName(lnk);
        string cn = Path.Combine(dir, "Qoder（原始启动）.lnk");
        string en = Path.Combine(dir, "Qoder (original launcher).lnk");
        if (File.Exists(cn)) return cn;
        if (File.Exists(en)) return en;
        return cn;
    }

    private static void UninstallShortcuts(QoderInfo qoder)
    {
        var targets = new List<string>();
        if (!string.IsNullOrEmpty(_shortcutDirOverride))
        {
            CollectShortcuts(_shortcutDirOverride, qoder.ExePath, targets);
        }
        else
        {
            CollectShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), qoder.ExePath, targets);
            CollectShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), qoder.ExePath, targets);
            CollectShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), qoder.ExePath, targets);
            CollectShortcuts(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), qoder.ExePath, targets);
        }

        /* 我们的快捷方式现在是 wscript，不会被 CollectShortcuts 命中，单独找回来 */
        var more = new List<string>();
        if (!string.IsNullOrEmpty(_shortcutDirOverride))
        {
            AddIfExists(more, Path.Combine(_shortcutDirOverride, "Qoder.lnk"));
        }
        else
        {
            AddIfExists(more, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Qoder.lnk"));
            AddIfExists(more, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Qoder.lnk"));
        }
        foreach (var lnk in more) if (!targets.Contains(lnk) && PointsToStatusBar(lnk)) targets.Add(lnk);

        foreach (var lnk in targets)
        {
            try
            {
                string backup = BackupPathFor(lnk);
                if (File.Exists(backup))
                {
                    File.Copy(backup, lnk, true);
                    File.Delete(backup);
                    Say("      已恢复原始快捷方式: " + lnk);
                }
                else if (PointsToStatusBar(lnk))
                {
                    File.Delete(lnk);
                    Say("      已删除: " + lnk);
                }
            }
            catch (Exception e) { Say("      [x] " + lnk + ": " + e.Message); }
        }
    }

    private static void AddIfExists(List<string> list, string path)
    {
        if (File.Exists(path)) list.Add(path);
    }

    private static void StartStatusBar()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "wscript.exe"),
                Arguments = "\"" + Path.Combine(InstallDir, "launch-hidden.vbs") + "\"",
                WorkingDirectory = InstallDir,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            Say("      [x] 启动失败: " + e.Message);
            Say("      请手动双击桌面的 Qoder 快捷方式。");
        }
    }

    /* ------------------------------------------------------------ 进程 */

    private static bool IsQoderRunning()
    {
        try
        {
            var list = Process.GetProcessesByName("Qoder");
            foreach (var p in list)
            {
                try { p.Dispose(); } catch { }
            }
            return list.Length > 0;
        }
        catch { return false; }
    }

    private static bool WaitForQoderExit(int seconds)
    {
        var sw = Stopwatch.StartNew();
        int lastReport = -1;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (!IsQoderRunning()) return true;
            int left = (int)(seconds - sw.Elapsed.TotalSeconds);
            if (left % 15 == 0 && left != lastReport)
            {
                Say("      还剩 " + (left / 60) + " 分 " + (left % 60) + " 秒...");
                lastReport = left;
            }
            Thread.Sleep(PollIntervalMs);
        }
        return !IsQoderRunning();
    }

    /* 杀掉所有注入器（node.exe 或 Qoder.exe 形态都可能），再看 pidfile */
    private static void KillInjectors()
    {
        try
        {
            string pidFile = Path.Combine(InstallDir, "injector.pid");
            if (File.Exists(pidFile))
            {
                int pid;
                if (int.TryParse(File.ReadAllText(pidFile).Trim(), out pid))
                {
                    try { Process.GetProcessById(pid).Kill(); } catch { }
                }
                try { File.Delete(pidFile); } catch { }
            }
        }
        catch { }

        try
        {
            string script =
                "Get-CimInstance Win32_Process | " +
                "Where-Object { $_.CommandLine -like '*injector.mjs*' } | " +
                "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + enc,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
            }
        }
        catch { }
    }

    /* ------------------------------------------------------------ 定位 Qoder */

    private class QoderInfo
    {
        public string Dir;
        public string ExePath;
        public string Source;
    }

    private static QoderInfo FindQoder()
    {
        var results = new List<QoderInfo>();

        try
        {
            foreach (var p in Process.GetProcessesByName("Qoder"))
            {
                try
                {
                    var info = MakeQoderInfo(p.MainModule.FileName, "运行中的进程");
                    if (info != null) results.Add(info);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }

        foreach (var lnk in FindShortcutFiles())
        {
            string target = ReadShortcutTarget(lnk);
            if (target == null) continue;
            var info = MakeQoderInfo(target, "快捷方式 " + lnk);
            if (info != null) results.Add(info);
        }

        foreach (var path in FindRegistryPaths())
        {
            var info = MakeQoderInfo(path, "卸载注册表项");
            if (info != null) results.Add(info);
        }

        foreach (var dir in DefaultQoderDirs())
        {
            var info = MakeQoderInfo(Path.Combine(dir, "Qoder.exe"), "默认安装目录");
            if (info != null) results.Add(info);
        }

        foreach (var dir in ScanForQoder())
        {
            var info = MakeQoderInfo(Path.Combine(dir, "Qoder.exe"), "全盘扫描");
            if (info != null) results.Add(info);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            if (seen.Add(r.Dir)) return r;
        }
        return null;
    }

    private static QoderInfo MakeQoderInfo(string exePath, string source)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            string full = Path.GetFullPath(exePath);
            if (!File.Exists(full)) return null;
            if (!string.Equals(Path.GetFileName(full), "Qoder.exe", StringComparison.OrdinalIgnoreCase))
                return null;

            string dir = Path.GetDirectoryName(full);
            if (!File.Exists(Path.Combine(dir, "resources", "app.asar"))) return null;

            var info = new QoderInfo();
            info.Dir = dir;
            info.ExePath = full;
            info.Source = source;
            return info;
        }
        catch { return null; }
    }

    private static List<string> FindShortcutFiles()
    {
        var list = new List<string>();
        var dirs = new List<string>();
        try
        {
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu));
            dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
        }
        catch { }
        foreach (var d in dirs)
        {
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
            try
            {
                foreach (var f in Directory.GetFiles(d, "*.lnk", SearchOption.AllDirectories))
                    list.Add(f);
            }
            catch { }
        }
        return list;
    }

    private static List<string> FindRegistryPaths()
    {
        var paths = new List<string>();
        var roots = new[]
        {
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var root in roots)
            {
                try
                {
                    using (var key = hive.OpenSubKey(root))
                    {
                        if (key == null) continue;
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = key.OpenSubKey(sub))
                                {
                                    if (sk == null) continue;
                                    object name = sk.GetValue("DisplayName");
                                    if (name == null) continue;
                                    if (name.ToString().IndexOf("Qoder", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    object loc = sk.GetValue("InstallLocation");
                                    if (loc != null && loc.ToString().Trim().Length > 0)
                                        paths.Add(loc.ToString().Trim().Trim('"'));
                                    object icon = sk.GetValue("DisplayIcon");
                                    if (icon != null)
                                    {
                                        string s = icon.ToString();
                                        int comma = s.LastIndexOf(',');
                                        if (comma > 0) s = s.Substring(0, comma);
                                        s = s.Trim('"');
                                        if (s.Length > 0) paths.Add(Path.GetDirectoryName(s));
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }
        return paths;
    }

    private static List<string> DefaultQoderDirs()
    {
        var dirs = new List<string>();
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            dirs.Add(Path.Combine(local, @"Programs\Qoder"));
            dirs.Add(Path.Combine(local, @"Qoder"));
            dirs.Add(Path.Combine(pf, @"Qoder"));
            dirs.Add(Path.Combine(pf86, @"Qoder"));
        }
        catch { }
        return dirs;
    }

    private static List<string> ScanForQoder()
    {
        var found = new List<string>();
        try
        {
            foreach (var dr in DriveInfo.GetDrives())
            {
                if (!dr.IsReady) continue;
                string root = dr.RootDirectory.FullName;
                if (root.StartsWith(@"\\")) continue;
                try
                {
                    foreach (var d in ScanDirs(root, 0, 4))
                    {
                        string name = Path.GetFileName(d);
                        if (!name.Equals("Qoder", StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(Path.Combine(d, "resources", "app.asar")))
                        {
                            found.Add(d);
                            if (found.Count >= 3) return found;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return found;
    }

    private static List<string> ScanDirs(string dir, int depth, int maxDepth)
    {
        var result = new List<string>();
        if (depth > maxDepth) return result;
        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { return result; }
        foreach (var s in subs)
        {
            string name = Path.GetFileName(s);
            if (name.StartsWith("$", StringComparison.Ordinal)) continue;
            if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Windows.old", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
                || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(s);
            result.AddRange(ScanDirs(s, depth + 1, maxDepth));
        }
        return result;
    }

    /* ------------------------------------------------------------ 工具 */

    private static void Say(string s)
    {
        Console.WriteLine(s);
        Log.Add(s);
    }

    private static bool HasFlag(string[] args, string name)
    {
        return Array.IndexOf(args, name) >= 0;
    }

    private static string GetArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i].Substring(name.Length + 1);
        }
        return null;
    }

    private static int Finish(int code)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            string logPath = Path.Combine(DataDir,
                "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            File.WriteAllLines(logPath, Log.ToArray(), new UTF8Encoding(false));
            Say("");
            Say("日志: " + logPath);
        }
        catch { }

        try
        {
            Console.WriteLine("");
            Console.Write("按回车键关闭本窗口...");
            Console.ReadLine();
        }
        catch { }
        return code;
    }
}
