# qaqh-cs-app：QAQ-Harness 纯 WinUI3 前端（C#）
# 依赖：.NET 10 SDK、winapp CLI 0.7+、pwsh、（mock-llm 需要 node）
# 契约同步：需要兄弟目录 ../qaqh-backend（含 C# 导出的 contract-export；main 基线）
#
# 两条主线：
#   调试：just run        → debug identity 散跑，免证书免安装，改完即跑
#   安装：just cert-install（一次性）&& just install → 签名 MSIX 并注册
# 在 Git Bash 里运行（配方假定 sh 语义）。

# dotnet 未收录进本机 PATH 时回退标准安装位置；DOTNET_ROOT 可覆盖
dotnet_dir := env("DOTNET_ROOT", "C:/Program Files/dotnet")
dotnet := if path_exists(dotnet_dir + "/dotnet.exe") == "true" { dotnet_dir + "/dotnet.exe" } else { "dotnet" }
# winapp 靠 PATH 搜索 dotnet（不认 DOTNET_ROOT），且 msys 形式条目才生效 → winapp 配方前置
dotnet_path := replace(dotnet_dir, "C:/", "/c/")

# dev 模式 daemon 根目录（应用按 <root>/target/debug/qaqh-daemon.exe 解析）；基线 = 后端主仓
backend_root := env("QAQH_BACKEND_ROOT", absolute_path("../qaqh-backend"))

arch := "x64"          # x64 | arm64 | x86；用 just arch=arm64 <recipe> 覆盖
dist := "dist"
cert := "devcert.pfx"
msix := dist + "/Qaqh.App.msix"
# Package.appxmanifest 的 Identity.Name（uninstall 匹配用）
pkg_name := "28709BA2-2F36-4748-85D1-361AAC858CFF"

default:
    @just --list

# ── 契约同步 ────────────────────────────────────────────────────────────────

# backend 导出契约 → 同步 C# 类型与 fixtures 进本仓库
sync-contract:
    pwsh -NoProfile -File scripts/sync-contract.ps1

# 只同步（backend 已导出）
sync-contract-only:
    pwsh -NoProfile -File scripts/sync-contract.ps1 -SkipExport

# ── 构建 / 测试 ─────────────────────────────────────────────────────────────

# 构建全部
build:
    "{{dotnet}}" build Qaqh.CsApp.slnx

# SDK 单测
test:
    "{{dotnet}}" test tests/Qaqh.Sdk.Tests/Qaqh.Sdk.Tests.csproj

# 清理构建产物与打包输出
clean:
    "{{dotnet}}" clean Qaqh.CsApp.slnx
    rm -rf {{dist}}

# ── 调试（散跑 + debug identity，免证书免安装）──────────────────────────────

# 构建并以包身份运行（winapp 注册 debug identity，自动继承 QAQH_BACKEND_ROOT）
run:
    PATH="{{dotnet_path}}:$PATH" QAQH_BACKEND_ROOT="{{backend_root}}" winapp run src/Qaqh.App -c Debug --detach

# 同 run，但先清掉应用 LocalState（设置/缓存排障用）
run-clean:
    PATH="{{dotnet_path}}:$PATH" QAQH_BACKEND_ROOT="{{backend_root}}" winapp run src/Qaqh.App -c Debug --detach --clean

# 本地 mock LLM：OpenAI 兼容 SSE 流（端到端验收用，默认 8318）
mock-llm port="8318":
    node scripts/mock-llm.mjs {{port}}

# ── 打包（MSIX）─────────────────────────────────────────────────────────────

# 生成与 manifest Publisher（CN=AppPublisher）匹配的开发证书（幂等，已存在则跳过）
cert:
    winapp cert generate --manifest src/Qaqh.App/Package.appxmanifest --if-exists skip

# 信任开发证书到本机（一次性；弹 UAC，证书须先 just cert 生成）
cert-install:
    pwsh -NoProfile -Command "Start-Process -Verb RunAs -Wait winapp -ArgumentList 'cert','install','{{justfile_directory()}}/{{cert}}'"

# Release 构建 + 打包 + 签名 → dist/Qaqh.App.msix
package:
    # 必须显式传 .csproj：目录输入会被当作 layout 文件夹，--arch/-c 会被拒
    mkdir -p {{dist}}
    PATH="{{dotnet_path}}:$PATH" winapp package src/Qaqh.App/Qaqh.App.csproj -c Release --arch {{arch}} --cert {{cert}} --output {{msix}}

# 打包并安装（需先 just cert-install 信任证书）
install: (package)
    pwsh -NoProfile -Command "Add-AppxPackage -Path '{{justfile_directory()}}/{{msix}}'"

# 卸载已安装的应用包
uninstall:
    pwsh -NoProfile -Command "Get-AppxPackage {{pkg_name}} | Remove-AppxPackage"
