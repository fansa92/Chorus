# Chorus（合唱）

一个 Windows 原生的 [sing-box](https://sing-box.sagernet.org/) 图形客户端，使用 **WinUI 3 / C# / .NET 8** 构建。

> 名字由来：sing-box 的 **sing** → 一组节点像合唱团（chorus）一样协同工作。

核心思路：GUI 不实现代理协议，只做三件事 —— **生成配置、管理 sing-box.exe 进程、消费 Clash API**（流量、策略组切换、测速）。编译全部交给 GitHub Actions，本地只写代码和跑单元测试。

## 功能（当前里程碑 M1）

- 启动 / 停止 sing-box 核心（启动前自动 `sing-box check` 校验配置）
- 订阅导入：订阅 URL 或手动粘贴分享链接（`ss` / `vmess` / `vless` / `trojan` / `hysteria2`），也支持 sing-box 原生 JSON 订阅
- **策略组**：仪表盘默认生成「🚀 节点选择」（手动选择）与「♻️ 自动选择」（urltest）；用户可在 JSON 中自定义策略组，支持
  - 精确引用节点名
  - **嵌套引用其他策略组（在组里选择“节点组”）**
  - `"*"` 展开为全部节点
  - 通配符按节点名筛选（如 `*香港*`）
- 策略组面板：切换节点 / 节点组（通过 Clash API `PUT /proxies/{group}`）、单组测速、全组测速
- 系统代理一键开关（写注册表 + WinINET 广播立即生效）
- TUN 模式（配置开关，需管理员运行）
- 实时上/下行流量（Clash API WebSocket `/traffic`）
- 实时日志（核心 stdout，自动滚动，上限 2000 行）
- 托盘图标：关闭到托盘、托盘退出、单实例

## 目录结构

```
Chorus/
├─ .github/workflows/        # CI：单元测试 + 编译发布；Release：打 tag 出 Release
├─ src/
│  ├─ Chorus.Core/    # 纯 .NET 8 逻辑库（可跨平台单测）
│  │  ├─ Models/             # NodeEntry / ProxyGroup / AppSettings
│  │  ├─ Parsing/            # 分享链接与订阅解析
│  │  ├─ Configuration/      # ConfigGenerator：渲染 sing-box config.json
│  │  ├─ ClashApi/           # Clash API 客户端（HTTP + WebSocket）
│  │  ├─ Interop/            # Job Object（随宿主退出杀核心）
│  │  └─ ...                 # 进程管理、系统代理、订阅下载
│  └─ Chorus.App/     # WinUI 3 应用（仅 CI 编译）
├─ tests/Chorus.Core.Tests/   # xUnit 单元测试（本地/CI 都可跑）
└─ scripts/make-icon.ps1     # 重新生成占位图标
```

## 开发工作流（本地不编译应用）

1. **写代码**：逻辑放 `Chorus.Core`，UI 放 `Chorus.App`。
2. **本地跑测试**（可选，需要任意 ≥8 的 .NET SDK）：
   ```bash
   dotnet test tests/Chorus.Core.Tests/Chorus.Core.Tests.csproj -c Release
   ```
   本机只有 .NET 10 运行时、没有 .NET 8 运行时时，加环境变量：
   ```bash
   DOTNET_ROLL_FORWARD=LatestMajor dotnet test ...
   ```
3. **推送**，GitHub Actions 自动完成单元测试 + `dotnet publish`（win-x64 自包含）并在 Artifact 中给出 `Chorus-win-x64.zip`。
4. **发版**：推 tag 触发 Release 工作流：
   ```bash
   git tag v0.1.0 && git push origin v0.1.0
   ```

## 用户使用说明

1. 从 Releases 下载并解压 `Chorus-win-x64.zip`（**已内置官方 sing-box 核心**，位于 `core\sing-box.exe`，构建时自动打包最新稳定版）。
2. 启动应用 → 设置页导入订阅或粘贴分享链接 → 保存 → 仪表盘打开开关。
3. 设置与节点保存在 `%LOCALAPPDATA%\Chorus\settings.json`。
4. 想换核心版本：替换 `core\sing-box.exe`，或把新版放到 `%LOCALAPPDATA%\Chorus\core\`（优先读便携目录）。
   注意：应用要求 sing-box **1.12 或更高版本**（配置使用新版 DNS 服务器格式；CI 打包时自动下载最新稳定版）。

### 自定义策略组

编辑 `settings.json` 的 `Profile.Groups`（名称不要用两个内置组的名字）：

```json
"Profile": {
  "Nodes": [],
  "Groups": [
    { "Name": "🇭🇰 香港",  "Type": "selector", "Members": ["*香港*", "*HK*"] },
    { "Name": "🇺🇸 美国",  "Type": "urltest",  "Members": ["*US*"] },
    { "Name": "出口",      "Type": "selector", "Members": ["🇭🇰 香港", "🇺🇸 美国", "♻️ 自动选择"] }
  ]
}
```

- `Type`：`selector`（手动选择）/ `urltest`（按延迟自动）
- `Members` 里写 **精确节点名**、**组名**（组里可以再选组）、`"*"` 或 **通配符**
- 组之间不要互相循环引用（sing-box 校验会拒绝）；不要引用「🚀 节点选择」

### TUN 模式

TUN 需要管理员权限。当前版本请以管理员身份运行应用后启用；正规提权方案（helper 进程 / Windows 服务）在路线图中。

## 已知限制（M1）

- 应用异常退出时系统代理可能残留，可在 Windows 设置中手动关闭，或重启本应用再退出
- 路由规则目前是全局 final 指向选择组，无分流规则编辑器

## 路线图

- [ ] M2：核心自动下载/更新（GitHub Releases）；断网检测；路由规则集
- [ ] M3：连接管理页（Clash API `/connections`）
- [ ] M4：策略组 UI 编辑器（当前是 JSON）；订阅自动更新定时器
- [ ] M5：TUN 提权服务模式；MSIX 打包

## License

MIT
