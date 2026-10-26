# Changelog

## v1.0.0 — 2026-10-26

首个公开版本。

- 自动定位 Qoder 桌面端安装目录（进程 / 快捷方式 / 注册表 / 默认目录 / 特征扫描）。
- 从 CNB Release 下载插件包并校验 SHA256。
- 安装运行时到 `%USERPROFILE%\.qoder\qoder-statusbar`。
- 快捷方式改为 `wscript.exe` 静默启动，原快捷方式备份为 `Qoder（原始启动）.lnk`。
- 无 Node.js 环境时自动用 Qoder 自带的 Electron 运行时兜底（硬链接 `node.exe`）。
- 支持 `--uninstall` 完整卸载并恢复快捷方式。
- 支持 `--dry-run`、`--offline`、`--qoder-dir`、`--install-dir`、`--no-wait`、`--no-shortcut`。
