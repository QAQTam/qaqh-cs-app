# 同步契约产物：backend `just sdk-export` 输出 → 本仓库
#   contract/cs/*.cs   → src/Qaqh.Sdk/Generated/Contract/*.cs
#   contract/fixtures  → tests/Qaqh.Sdk.Tests/Fixtures/
#   contract/manifest.json（原样保留在 contract/ 供 check）
# 用法：
#   pwsh scripts/sync-contract.ps1                       # 先导出再同步
#   pwsh scripts/sync-contract.ps1 -SkipExport           # 只同步已存在的 contract/
param(
    [switch]$SkipExport,
    [string]$BackendRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\qaqh-backend")).Path
)

$ErrorActionPreference = "Stop"

$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$contractDir = Join-Path $repo "contract"

if (-not $SkipExport) {
    if (-not (Test-Path (Join-Path $BackendRoot "target\debug\qaqh-contract-export.exe"))) {
        Write-Host "building qaqh-contract-export..." -ForegroundColor Cyan
        Push-Location $BackendRoot
        cargo build -p qaqh-contract-export
        Pop-Location
    }
    if (Test-Path $contractDir) {
        Remove-Item -Recurse -Force $contractDir
    }
    Write-Host "exporting contract from backend..." -ForegroundColor Cyan
    & (Join-Path $BackendRoot "target\debug\qaqh-contract-export.exe") export --out $contractDir
}

if (-not (Test-Path (Join-Path $contractDir "cs"))) {
    throw "contract/cs 不存在：先运行 backend 的 sdk-export（需要含 C# 导出的 exporter）。"
}

# C# 类型 → Sdk
$generatedRoot = Join-Path $repo "src\Qaqh.Sdk\Generated"
if (Test-Path $generatedRoot) {
    Remove-Item -Recurse -Force $generatedRoot
}
New-Item -ItemType Directory -Path (Join-Path $generatedRoot "Contract") -Force | Out-Null
Copy-Item -Path (Join-Path $contractDir "cs\*.cs") -Destination (Join-Path $generatedRoot "Contract")
Write-Host "synced cs/ -> src/Qaqh.Sdk/Generated/Contract ($((Get-ChildItem (Join-Path $generatedRoot 'Contract')).Count) files)"

# fixtures → tests
$fixturesTarget = Join-Path $repo "tests\Qaqh.Sdk.Tests\Fixtures"
if (Test-Path (Join-Path $repo "tests\Qaqh.Sdk.Tests")) {
    if (Test-Path $fixturesTarget) {
        Remove-Item -Recurse -Force $fixturesTarget
    }
    New-Item -ItemType Directory -Path $fixturesTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $contractDir "fixtures\*") -Destination $fixturesTarget -Recurse
    Copy-Item -Path (Join-Path $contractDir "manifest.json") -Destination $fixturesTarget
    Write-Host "synced fixtures -> tests/Qaqh.Sdk.Tests/Fixtures"
}
