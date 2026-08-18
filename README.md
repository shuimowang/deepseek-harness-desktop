# DeepSeek Harness Desktop

[![Build](https://github.com/shuimowang/deepseek-harness-desktop/actions/workflows/build.yml/badge.svg)](https://github.com/shuimowang/deepseek-harness-desktop/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/shuimowang/deepseek-harness-desktop)](https://github.com/shuimowang/deepseek-harness-desktop/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

面向 Windows x64 的轻量原生桌面客户端，用于启动和承载
[DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 官方 Web UI。

> 本项目是非官方社区封装，与 DeepSeek AI 没有隶属或背书关系。DeepSeek 与
> DeepSeek Harness 名称及相关品牌资产归其权利人所有。

<p align="center">
  <img src="assets/dsh-whale-icon.png" width="96" alt="DeepSeek Harness Desktop 黑色鲸鱼图标">
</p>

## 下载

- [GitHub 最新版](https://github.com/shuimowang/deepseek-harness-desktop/releases/latest)

推荐普通用户下载文件名带 `-online` 的在线轻量版。每个发布包都同时提供
`.sha256` 校验文件。

## 功能

- 原生 WPF 窗口和 Edge WebView2，不额外携带 Chromium。
- 单实例运行；重复打开会唤起现有窗口。
- 固定使用经过验证的 `@deepseek-ai/dsh@0.1.0-rc.7`。
- 完整便携包内置 Node.js 24.14.1、DSH 和 .NET 10 Desktop Runtime。
- 优先复用 `127.0.0.1:3080` 上已有的 Harness；端口被其他程序占用时自动选择空闲端口。
- 连续探测服务健康状态，异常退出后自动恢复一次。
- 仅关闭客户端自己启动的服务，不终止外部 Harness 进程。
- 跨域链接交给系统浏览器，WebView2 只保留本地 Harness 页面。
- 可选择工作目录、重新连接、刷新、查看启动日志或在浏览器中打开。
- WebView2 不可用时自动降级到系统浏览器。

## 给普通用户

### 在线轻量版（推荐分享）

`DeepSeekHarness-1.1.0-win-x64-online.zip` 小于 100 MB，适合 GitHub Release
和蓝奏云。首次运行会自动：

1. 从 nodejs.org 下载并校验 Node.js 24.14.1；
2. 从 npm 安装固定版 `@deepseek-ai/dsh@0.1.0-rc.7`；
3. 将运行时保存到 `%LOCALAPPDATA%\DeepSeekHarness\Runtime`，后续启动和客户端更新直接复用。

首次安装需要联网，受网络和代理速度影响可能需要数分钟。不要在安装过程中强制结束进程；正常关闭窗口会安全取消安装。

### 离线完整版

从发布包中获取 `DeepSeekHarness-1.1.0-win-x64.zip`：

1. 将 ZIP 完整解压到一个普通文件夹，不要直接在压缩包里运行。
2. 双击 `DeepSeekHarness.exe`。
3. 如需桌面和开始菜单入口，右键运行 `Install-DesktopShortcut.ps1`，或在 PowerShell 中执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-DesktopShortcut.ps1 -Launch
```

移除快捷方式：

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-DesktopShortcut.ps1 -Remove
```

便携包无需单独安装 Node.js 或 .NET。Windows 10/11 通常已包含 Microsoft Edge
WebView2 Runtime；缺失时客户端会在默认浏览器中打开 Harness。

当前发布物未购买商业代码签名证书，Windows SmartScreen 可能提示“未知发布者”。发布者应同时提供
`.sha256` 文件，用户可用以下命令核对下载完整性：

```powershell
Get-FileHash .\DeepSeekHarness-1.1.0-win-x64.zip -Algorithm SHA256
```

## 数据位置

- 客户端设置：`%LOCALAPPDATA%\DeepSeekHarness\desktop-settings.json`
- WebView2 数据：`%LOCALAPPDATA%\DeepSeekHarness\WebView2`
- 默认工作目录：`文档\DeepSeek Harness Workspace`
- Harness 自身的会话、模型和插件数据仍遵循官方 DSH 的数据目录规则。

客户端不会在安装目录保存用户会话。升级时可以直接替换程序文件夹。

## 从源码运行

开发模式需要：

- Windows x64
- .NET 10 SDK
- Node.js `^22.19.0` 或 `>=24.0.0`
- Microsoft Edge WebView2 Runtime

```powershell
dotnet restore .\DshDesktop.csproj
dotnet run --project .\DshDesktop.csproj
```

开发模式没有内置运行时，会通过系统 `npx` 启动固定版本的 DSH。

## 构建可分享便携包

在线轻量版：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 -OnlineLite
```

离线完整版：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1
```

脚本执行以下工作：

- 自包含发布 Windows x64 WPF 客户端；
- 从 nodejs.org 下载 Node.js，并按官方 `SHASUMS256.txt` 校验；
- 安装固定版本的 DeepSeek Harness；
- 写入运行时清单和第三方声明；
- 在 `artifacts` 目录生成 ZIP 与 SHA-256 文件。

只构建不带 Node/DSH 的小型客户端：

```powershell
.\scripts\Build-Release.ps1 -SkipRuntimeBundle
```

## 项目边界

客户端不修改或复刻 Harness UI。模型、会话、工具、插件和 Agent 能力均由官方
`@deepseek-ai/dsh` 提供；本项目只负责 Windows 窗口、运行时准备、进程生命周期、健康检查与发布。

本项目源码使用 [MIT License](LICENSE)。第三方组件和图标来源见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
