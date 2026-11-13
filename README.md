# qaqh-cs-app

QAQ-Harness 的**纯 WinUI 3** 桌面前端（C# + Windows App SDK + XAML，零 WebView、零原生依赖）。

与 Rust 版壳（`qaqh-winui-app`，windows-reactor 投影）平行并存；后端为
[`qaqh-backend`](../qaqh-backend) 的 `qaqh-daemon`（RingingV2：HTTP + SSE）。
前端工作以后端 main 为基线。

## 结构

```
src/Qaqh.App/      WinUI 3 应用（NavigationView 壳 / 会话侧栏 / 聊天 / 设置…）
src/Qaqh.Sdk/      纯 C# SDK：daemon 发现与拉起、租约续期、SSE 解码、命令信封、
                   服务面 RPC、content 上传下载、审批/配对
  Generated/       ← 契约类型（由 backend qaqh-contract-export 生成，勿手改）
tests/Qaqh.Sdk.Tests/   Sdk 单测
contract/          （4.0.0 从新后由 just sync-contract 重建）
scripts/sync-contract.ps1   同步契约产物
```

## SDK 生成链（rust → cs）

1. backend：`cargo build -p qaqh-contract-export`（已扩展 C# 导出，与 TS 共用 manifest 哈希）
2. 本仓库：`just sync-contract`（= `qaqh-contract-export export --out contract/` + 复制
   `cs/*.cs → src/Qaqh.Sdk/Generated`）
3. `dotnet test`：Sdk 单测。

## 环境

- .NET SDK 10（或 winapp new 模板要求的版本）
- winapp CLI 0.7+（`winget install Microsoft.WinAppCli`）、Developer Mode 开启
- dev 模式拉起 daemon：设 `QAQH_BACKEND_ROOT=<qaqh-backend 路径>`（应用按
  `<root>/target/debug/qaqh-daemon.exe` 候选序解析），或把 daemon 放到 exe 旁

## 命令

```
just sync-contract   # backend 导出 + 同步
just build           # dotnet build
just test            # SDK 契约测试
just run             # debug identity 散跑（免证书免安装；QAQH_BACKEND_ROOT 自动指向 ../qaqh-backend）
just run-clean       # 同 run，但先清应用 LocalState
just mock-llm        # 本地 mock LLM（node scripts/mock-llm.mjs，默认 8318）
just package         # Release MSIX 签名打包 → dist/Qaqh.App.msix（just arch=arm64 package 换架构）
just cert            # 生成与 manifest Publisher 匹配的开发证书（幂等）
just cert-install    # 一次性：信任开发证书（UAC）
just install         # 打包 + Add-AppxPackage 安装
just uninstall       # 卸载
```

## 打包策略：调试散跑，分发 MSIX

- **日常调试**用 `just run`：winapp 给构建产物注册 **debug identity** 后以包身份启动——
  不用证书、不用安装、秒级迭代，但保留包身份（AUMID / LocalState）。
- **安装与分发**用 `just install`：`winapp package` 产出自签 MSIX（devcert 匹配
  `CN=AppPublisher`），`Add-AppxPackage` 侧载。
- **不做**纯 unpackaged（`WindowsPackageType=None`）：manifest 声明了
  `systemai:Capability systemAIModels`（需要包身份），且 daemon 的
  CREATE_BREAKAWAY_FROM_JOB 逻辑按 packaged kill-on-close Job 设计。

## XAML 编译器坑实录（WMC9999 伪装成「资源缺失」）

以下写法会让 XAML 编译器**内崩**，报错却是无关的
`ErrorMessages.resources 找不到` ——看到该错误就当「某处解析失败」排查：

1. **x:Bind 调用页面自身类的静态函数**（`{x:Bind local:ThisPage.Fn(x)}`）。
   函数挪到独立静态类（如 `Helpers/ChatUi.cs`）即可。
2. **DataTemplate 里对附加属性做 x:Bind**（`ToolTipService.ToolTip="{x:Bind …}"`）。
   用静态文本或 Code-behind 设置。
3. **x:Bind 路径引用不存在的属性**（拼写错/属性名不符）——不报 WMC1111 而是内崩。

另外：`x:DataType` 指向的类型经 XamlTypeInfo 反射时**不能有 required/init-only 属性**，
跨层类型（SDK 契约）直接做 DataType 也会因此炸——用应用侧可写行 VM 包装。

