# Qoder Status Bar · 一键安装器

给 **Qoder 桌面端** 输入框底栏加一条原生风格的用量读数：

```
速度 412 tok/s · 缓存 99.71% · 首字 3.23s
```

本仓库只负责 **下载 + 安装 + 卸载**。读写数逻辑在插件仓库：
[kybll/Qoder-StatusBar](https://cnb.cool/kybll/Qoder-StatusBar)

---

## 它做了什么

1. 找到本机的 Qoder 安装目录（运行中的进程 / 快捷方式 / 卸载注册表 / 默认目录 / 全盘特征扫描）。
2. 从 CNB Release 下载插件包，校验 **SHA256** 后解压。
3. 把运行时复制到 `%USERPROFILE%\.qoder\qoder-statusbar`。
4. 把桌面和开始菜单里的 Qoder 快捷方式改成经 `wscript.exe` 静默启动（**不弹 cmd 窗口**），
   原快捷方式重命名为 `Qoder（原始启动）.lnk` 备份。
5. 启动 Qoder 并注入底栏。

### 不会做的事

- 不修改 Qoder 的 `app.asar` 或任何程序文件（Electron 完整性校验会拦，也不必要）。
- 不需要管理员权限。
- 不需要预装 Node.js —— 没装 Node 时用 Qoder 自带的 Electron 运行时（硬链接 `node.exe`）。
- 不联网上报任何数据；只访问 CNB 的 Release 下载地址。

---

## 安装

1. 下载 **[Install-Qoder-Status-TypeBar.exe](https://cnb.cool/kybll/Qoder-Status-TypeBar-AutoInstaller/-/releases/download/v1.0.3/Install-Qoder-Status-TypeBar.exe)**
   （全部版本见 [Releases](https://cnb.cool/kybll/Qoder-Status-TypeBar-AutoInstaller/-/releases)）。
2. **完全退出 Qoder**（托盘图标也要退出）。
3. 双击运行，按提示回车即可。

> EXE 未做代码签名，Windows SmartScreen 可能提示「Windows 已保护你的电脑」。
> 点「更多信息」→「仍要运行」。源码就在本仓库，可自行编译核对。

安装完成后，以后照常点桌面上的 Qoder 图标启动即可，底栏会自动出现。

校验下载的文件（可选）：

```
SHA256  5cd22ea6cfdd63d2243a76d19abf4d60b6642fc781f3300b26a7e9fa81ff0862
大小    28672 字节
```

### 卸载

```bat
Install-Qoder-Status-TypeBar.exe --uninstall
```

会结束注入器、把快捷方式恢复成原始启动方式、删除 `%USERPROFILE%\.qoder\qoder-statusbar`。
Qoder 本身不会被动过。

原始备份（`Qoder（原始启动）.lnk`）被删掉时，卸载会把快捷方式改回**直接启动 Qoder.exe**，
不会删除快捷方式 —— 桌面上的启动入口永远保留。开始菜单里的快捷方式同样会被找回还原；
已经被你自己改回去的快捷方式不会被覆盖。

---

## 命令行选项

| 选项 | 说明 |
| --- | --- |
| `--help`, `-h` | 显示帮助 |
| `--uninstall` | 卸载并恢复快捷方式 |
| `--no-restart` | 安装后不自动启动 Qoder |
| `--offline <zip>` | 用本地插件包安装，不联网下载 |
| `--dry-run` | 只做下载 / 校验 / 解压，不改动任何文件 |
| `--qoder-dir <路径>` | 手动指定 Qoder 安装目录 |
| `--install-dir <路径>` | 指定安装目录（默认 `%USERPROFILE%\.qoder\qoder-statusbar`） |
| `--shortcut-dir <路径>` | 只处理指定目录里的快捷方式 |
| `--port <端口>` | CDP 调试端口（默认 9222，被占用时启动器会自动换） |
| `--no-wait` | 不等待 Qoder 退出（默认最多等 30 分钟） |
| `--no-shortcut` | 不改快捷方式 |

---

## 目录

| 路径 | 用途 |
| --- | --- |
| `%USERPROFILE%\.qoder\qoder-statusbar` | 插件运行时、日志、`injector.pid` |
| `%LOCALAPPDATA%\Qoder-Status-TypeBar-Installer` | 下载缓存、安装日志 |
| 桌面 / 开始菜单 `Qoder（原始启动）.lnk` | 原快捷方式备份，卸载时恢复 |

---

## 从源码构建

需要 Windows 自带的 .NET Framework 4.x 编译器（无需装 SDK）：

```bat
build.bat
```

产物：`Install-Qoder-Status-TypeBar.exe`（约 27 KB，单文件，无依赖）。

安装器内置的插件包版本、下载地址和 SHA256 在 `src/Program.cs` 顶部：

```csharp
private const string PluginVersion = "1.0.3";
private const string ZipUrl = "https://cnb.cool/kybll/Qoder-StatusBar/-/releases/download/v1.0.3/Qoder-Status-TypeBar-v1.0.3.zip";
private const string ZipSha256 = "aa75d326...";
```

发新版本时改这三处，重新编译，再发 Release。

---

## License

MIT
