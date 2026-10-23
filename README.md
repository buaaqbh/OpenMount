# OpenMount

把 WebDAV / SFTP 挂载成 Windows 盘符的开源工具，基于 [rclone](https://rclone.org/) 与 [WinFsp](https://winfsp.dev/)，交互参考 RaiDrive Mount。

- 使用文档：[docs/使用指南.md](docs/使用指南.md)（安装、添加驱动器、状态含义、设置、便携模式、命令行、常见问题）
- 许可证：MIT（见 LICENSE）；第三方声明见 THIRD-PARTY-NOTICES.md

## 状态

早期开发（0.1.0）。已实现：领域模型与三维状态、rclone 参数/环境生成、RC 客户端、挂载引擎与会话、离线缓存扫描、未同步数据守卫、驱动器监督器/管理器、Windows 集成（凭据管理器、Job Object、挂载点身份校验、Shell、单实例/会话锁）、WPF 主程序（按 2026-10-23 设计稿实现的 Fluent/Windows 11 界面：驱动器卡片与三维状态徽标、新建/编辑/复制驱动器、详情、设置、各类对话框、托盘菜单、配置导入导出）、应用与托盘/驱动器图标（`src/OpenMount.App/Assets`，由 `tools/IconGen` 从设计几何渲染）、SSH.NET 主机密钥探测（TOFU）。

验证状态（2026-10-23，rclone 1.75.2 + WinFsp 2.1）：单元测试 140/140；集成测试 6/6（.NET 10），含 WebDAV/SFTP 真实挂载、预检失败不挂载与"断网写入 → 停止保留 → 重连续传"链路；主程序自动连接、断开、重连、退出与崩溃恢复后"此电脑"无残留。

## 构建

要求：.NET 10 SDK（LTS，支持至 2028-11）、Windows 10 1809+。

```powershell
dotnet build OpenMount.sln
dotnet test tests/OpenMount.Tests
```

## 运行

1. 安装 WinFsp 2.1：`winget install WinFsp.WinFsp`（或运行 `tools/fetch-deps.ps1` 下载 MSI 后手动安装）。
2. 获取 rclone 1.75.2：`powershell -File tools/fetch-deps.ps1`，脚本校验官方 SHA256 后放入 `deps/rclone/rclone.exe`，构建时自动复制到输出目录的 `bin\rclone.exe`。开发时也可设置环境变量 `OPENMOUNT_RCLONE` 指向任意 rclone.exe。 集成测试默认挑选最高的空闲盘符，设置 `OPENMOUNT_IT_LETTER=T` 之类可固定盘符。
3. `dotnet run --project src/OpenMount.App`

## 便携版（portable）

```powershell
powershell -File tools/make-portable.ps1
```

脚本发布自包含的 win-x64 版本到 `dist/OpenMount-<版本>-portable-win-x64/`，加入 `portable.txt`、`使用说明.txt`、LICENSE 与第三方声明，并生成同名 .zip（`dist/` 不入库）。
输出自带 .NET 10 运行时与 `bin\rclone.exe`，无需安装 .NET；WinFsp 仍需单独安装。目录里的 `portable.txt`
使配置、日志与缓存都写到旁边的 `data\`（否则写到 `%LOCALAPPDATA%\OpenMount`）；`OPENMOUNT_ROOT` 环境变量优先于两者。

## 集成测试

`tests/OpenMount.IntegrationTests` 需要 rclone 与 WinFsp；缺任一者时测试会输出 SKIPPED 并直接通过。

## 目录

```
src/OpenMount.Core      领域模型、状态、引擎契约、守卫、校验、仓库、监督器
src/OpenMount.Rclone    rclone 参数/环境、RC 客户端、进程、引擎、缓存扫描
src/OpenMount.Windows   凭据管理器、Job Object、盘符/挂载点、Shell、单实例、网络事件
src/OpenMount.App       WPF 主程序（Themes/Design.xaml 为设计令牌与共用样式，Assets/ 为图标）
docs/                   使用文档
tests/                  单元测试、集成测试
tools/fetch-deps.ps1    下载并校验 rclone / WinFsp
tools/IconGen           从设计几何渲染应用、托盘与资源管理器驱动器图标（dotnet run -c Release --project tools/IconGen）
tools/make-portable.ps1 生成便携版
```

## 致谢

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — https://github.com/winfsp/winfsp

rclone — https://github.com/rclone/rclone（MIT）
