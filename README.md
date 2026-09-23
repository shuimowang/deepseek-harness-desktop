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
- 固定使用从官方标签 `dsh-v0.1.7-alpha.2` 构建并验证的 DeepSeek Harness。
- 完整便携包内置 Node.js 24.14.1、DSH 和 .NET 10 Desktop Runtime。
- 优先复用 `127.0.0.1:3080` 上已有的 Harness；端口被其他程序占用时自动选择空闲端口。
- 连续探测服务健康状态，异常退出后自动恢复一次；再次失败会停止自动重启。
- 启动日志持久保存；正常配置稳定运行 30 秒后保存一份小型恢复快照。
- 提供隔离安全模式和“恢复上次配置”，不修改 Harness 会话、API 凭据或工作目录。
- 仅关闭客户端自己启动的服务，不终止外部 Harness 进程。
- 跨域链接交给系统浏览器，WebView2 只保留本地 Harness 页面。
- 可选择工作目录、重新连接、刷新、查看启动日志或在浏览器中打开。
- WebView2 不可用时提供系统浏览器模式，不会自动启动外部程序。

## 给普通用户

### 在线轻量版（推荐分享）

`DeepSeekHarness-1.9.1-win-x64-online.zip` 小于 100 MB，适合 GitHub Release
和蓝奏云。

#### 运行环境

- Windows 10 或 Windows 11 x64。
- 首次启动时能访问 `nodejs.org` 和 npm 镜像站。如果使用代理、公司网络或防火墙，需要允许访问 Node.js 下载地址和 `registry.npmmirror.com`。
- 普通用户权限即可，不需要以管理员身份运行。
- 解压目录、`%LOCALAPPDATA%` 和“文档”目录需要有写入权限。
- 内嵌界面需要 Microsoft Edge WebView2 Runtime；Windows 10/11 通常已经包含。缺失时客户端仍可运行，并会提示用户点击后在系统默认浏览器中打开。
- 使用模型时，仍需按照 DeepSeek Harness 的要求配置可用的模型服务和 API 凭据；客户端不会附送模型额度或密钥。

在线轻量版**不需要预装** .NET Runtime、Node.js、npm、pnpm、DeepSeek Harness 或 Git。客户端为自包含发布，缺少的 Node.js 和 DeepSeek Harness 会在首次启动时自动准备。

本版本的生产运行时已包含所需的 Windows x64 原生组件，用户不需要安装 Python 或 Visual Studio C++ 编译工具。

#### 首次启动

客户端会自动：

1. 从 nodejs.org 下载并校验 Node.js 24.14.1；
2. 通过 Node.js 自带的 Corepack 从镜像获取固定的 pnpm 11.7.0，从压缩包内的官方标签 tarball 安装 `@deepseek-ai/dsh@0.1.7-alpha.2`，并按生产锁文件从 npm 镜像并行下载外部依赖；
3. 将运行时保存到 `%LOCALAPPDATA%\DeepSeekHarness\Runtime`，后续启动和客户端更新直接复用。

`v1.9.1` 不会从 npm 查找 Harness 包，也不会由每台电脑重新求解完整依赖树。首次安装和 Profile 初始化通常需要数分钟；在线安装会使用 `registry.npmmirror.com`，并限制网络重试时间，避免网络异常时无限等待。窗口会持续显示当前阶段与已用时长，请不要在仍有进度提示时重复启动。安装被正常关闭或文件被临时占用时，已下载的 pnpm 缓存会保留，下次启动继续利用，不会全部重新下载。客户端升级后，只要内置的运行时版本没有变化，就不需要重新下载。

安装诊断日志位于 `%LOCALAPPDATA%\DeepSeekHarness\Runtime\logs\install.log`。安装失败时，界面会同时显示保留目录和日志位置。

不要只按 ZIP 大小预留空间。首次准备的 Node.js、DeepSeek Harness 和 pnpm 内容寻址缓存会明显大于下载包，WebView2 用户数据还会随使用增长。建议磁盘至少预留 **1 GB** 可用空间。

### 离线完整版

从发布包中获取 `DeepSeekHarness-1.9.1-win-x64.zip`：

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

离线完整版已包含 Node.js、DeepSeek Harness 和客户端所需的 .NET 运行时，首次启动无需联网下载或安装这些组件，也不需要管理员权限。Windows 10/11 通常已包含 Microsoft Edge WebView2 Runtime；缺失时客户端会提示用户点击后在默认浏览器中打开 Harness。

“离线”只表示本地运行环境无需在线安装。实际调用云端模型时，仍然需要网络连接、可用的模型服务和对应的 API 凭据。

当前发布物未购买商业代码签名证书，Windows SmartScreen 可能提示“未知发布者”。发布者应同时提供
`.sha256` 文件，用户可用以下命令核对下载完整性：

```powershell
Get-FileHash .\DeepSeekHarness-1.9.1-win-x64.zip -Algorithm SHA256
```

## 数据位置

- 客户端设置：`%LOCALAPPDATA%\DeepSeekHarness\desktop-settings.json`
- 客户端启动日志：`%LOCALAPPDATA%\DeepSeekHarness\logs`
- 配置恢复快照：`%LOCALAPPDATA%\DeepSeekHarness\Recovery`
- WebView2 数据：`%LOCALAPPDATA%\DeepSeekHarness\WebView2`
- 默认工作目录：`文档\DeepSeek Harness Workspace`
- Harness 自身的会话、模型和插件数据仍遵循官方 DSH 的数据目录规则。

客户端不会在安装目录保存用户会话。升级时可以直接替换程序文件夹。

`v1.9.1` 内置 Harness `0.1.7-alpha.2`，Session 日志升级到 V4。升级前请退出旧客户端，并备份 Harness 数据目录（默认 `%USERPROFILE%\.dsh`，设置了 `DSH_HOME` 时以该目录为准）。不要让新旧版本同时使用同一份数据；保留旧日志不代表旧版能读取新版产生的内容。

本次上游调整了设置和插件接口：设置保存到当前 Profile 的插件配置，旧 `settings.yaml` 只尝试导入一次；旧目录形式的 Agent 预设需迁移到插件组合包。官方 DeepSeek 适配器仅使用 Messages API，旧自定义配置需移除 `protocol` 并使用兼容地址。自定义 `spill-policy` 的 `maxInlineBytes` 需改为 `maxInlineTokens`。普通用户无需手动修改未使用的选项，第三方插件需由作者适配。

## 故障恢复

如果插件或用户配置导致 Harness 连续启动失败，客户端会停止自动重启，并在错误页提供以下操作：

- **安全模式**：使用独立的临时 `DSH_HOME`，只加载官方基础 Bundle 和 Web Bundle。安全模式用于确认 Harness 本体能够启动，不是另一套日常数据环境。
- **恢复上次配置**：恢复最近一次稳定运行 30 秒后保存的 Profile 清单、Patch 和锁文件。恢复前会把当前文件保存在 `Recovery\before-restore` 中。
- **插件目录**和**日志目录**：直接打开对应位置，便于手动排查或提交故障信息。

恢复功能不会编辑或删除 `.dsh\sessions`、Harness 凭据文件和用户工作目录。首次成功稳定运行前没有“上次配置”快照，此时仍可使用安全模式并打开插件目录手动处理。

从包含 DSH `0.1.0-rc.7` 的旧版客户端升级前，建议先备份 Harness 数据目录和默认工作目录。DSH `0.1.0-rc.8` 起调整了 SQLite 存储格式，不建议让新旧版本交替打开同一份数据。客户端会自动更新 DSH 管理的旧版依赖链接，不会删除链接目标或用户会话数据。

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

可分享构建使用仓库内固定的官方标签 tarball 和锁文件，不受 npm 发布状态或依赖漂移影响。系统 `npx` 回退模式仅适合开发调试，正式发布包不依赖它。

构建后可运行恢复逻辑回归测试：

```powershell
pwsh -NoProfile -File .\scripts\Test-Recovery.ps1
```

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
- 使用提交到仓库的官方标签 tarball 和生产锁文件安装固定版本的 DeepSeek Harness；
- 写入运行时清单和第三方声明；
- 在 `artifacts` 目录生成 ZIP 与 SHA-256 文件。

只构建不带 Node/DSH 的小型客户端：

```powershell
.\scripts\Build-Release.ps1 -SkipRuntimeBundle
```

更新固定的 Harness 版本时，先在官方源码的准确标签上按其 `release:verify --family dsh`、
`build:official`、`release:pack --family ...` 和 `release:verify-packed-install --family dsh` 流程生成
`dist/npm`、`dist/npm-vendor`、`dist/npm-landlock`，再导入本仓库：

```powershell
.\scripts\Import-DshRuntime.ps1 `
  -SourceRoot D:\path\to\deepseek-harness `
  -UpstreamTag dsh-v0.1.7-alpha.2 `
  -UpstreamCommit 00102833dfaee1da9f48a3a8eae9d34005a75218 `
  -HarnessVersion 0.1.7-alpha.2
```

导入脚本会核对标签和提交、复制官方 tarball，并生成生产锁文件。客户端不会在运行时跟随上游分支自动升级；每个发布版始终对应可追溯、已验证的固定 Harness 版本。

导入步骤需在 Windows x64、Node.js 24.14.1 下运行。若未来上游重新引入需要现场编译的原生依赖，导入脚本会按固定 Node ABI 生成预编译包并记录二进制 SHA-256；发布构建会拒绝与预编译目标不同的 Node.js 版本。

## 项目边界

客户端不修改或复刻 Harness UI。模型、会话、工具、插件和 Agent 能力均由官方
`@deepseek-ai/dsh` 提供；本项目只负责 Windows 窗口、运行时准备、进程生命周期、健康检查与发布。

本项目源码使用 [MIT License](LICENSE)。第三方组件和图标来源见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

当前 Harness 来源为官方标签 [`dsh-v0.1.7-alpha.2`](https://github.com/deepseek-ai/deepseek-harness/releases/tag/dsh-v0.1.7-alpha.2)，提交 `00102833dfaee1da9f48a3a8eae9d34005a75218`。该版本仍是 Alpha，尚未完成安全审计，可能包含破坏性兼容变更；沙箱、审批和权限控制也不能保证完全隔离。本项目不修改 Harness UI 或功能实现。
