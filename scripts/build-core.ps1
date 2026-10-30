# 单独构建 Rust 同步核心（window_sync_core.dll）
# 常规情况下直接 `dotnet build` 就会自动调用本脚本的等价命令；
# 这里保留独立入口，方便只改 Rust 代码时快速验证。

$ErrorActionPreference = "Stop"

$coreDir = Join-Path $PSScriptRoot "..\core"
$dll     = Join-Path $coreDir "target\release\window_sync_core.dll"

Push-Location $coreDir
try {
    cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "cargo build 失败" }
}
finally { Pop-Location }

if (-not (Test-Path $dll)) { throw "未生成 $dll" }

$size = (Get-Item $dll).Length
Write-Host "OK: $dll ($([math]::Round($size / 1KB)) KB)" -ForegroundColor Green
