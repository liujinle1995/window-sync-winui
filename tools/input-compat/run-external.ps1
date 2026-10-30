#Requires -Version 7.0
<#
.SYNOPSIS
    用本机已有的第三方程序当「从窗口」，验证 PostMessage 注入能不能被它的输入管线吃下去。

.DESCRIPTION
    两种用法：
      1. 默认：起一个 Chromium 系浏览器打开 external/probe-page.html，页面里有计数器和
         一个 textarea —— textarea 里出现 "abc" 就是键盘消息被页面吃到了；计数器记
         keydown / mousedown / mousemove / mouseup / wheel。
      2. -ProcessName / -ProcessId：拿已经在跑的程序（Electron 客户端、游戏）的顶层窗口。
         这类程序没有计数器，只能靠「注入前后各截一张图」人工对比。

    跑完把从窗口客户端区域截图存到 <WorkDir>/external-<role>.png，并把窗口信息、
    前台采样写进 <WorkDir>/result-<role>.json。

.EXAMPLE
    pwsh -File tools/input-compat/run-external.ps1
    pwsh -File tools/input-compat/run-external.ps1 -ProcessName MyGame_x64r -NoDrag
#>
[CmdletBinding()]
param(
    [string]$Browser,
    [string]$WorkDir,
    [string]$DllPath,
    # 直接用已经跑着的程序当从窗口（不新起进程）：给游戏 / 桌面客户端用
    [string]$ProcessName,
    [int]$ProcessId,
    # 只跑键盘、不发鼠标点击：往正在运行的游戏里点一下可能让角色真的动起来
    [switch]$NoDrag
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $WorkDir) { $WorkDir = Join-Path $repo '.workbuddy/tmp/compat' }
if (-not $DllPath) { $DllPath = Join-Path $WorkDir 'dll/window_sync_core.dll' }
$exe = Join-Path $WorkDir 'bin/CompatTool.exe'

if (-not (Test-Path $exe)) { throw "先跑一次 run-compat.ps1 把 CompatTool 构建出来：$exe" }
if (-not (Test-Path $DllPath)) { Copy-Item (Join-Path $repo 'core/target/release/window_sync_core.dll') $DllPath -Force }

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    public static string Title(IntPtr h) {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
"@

$proc = $null
$role = $null
$hwnd = [IntPtr]::Zero

if ($ProcessName -or $ProcessId -ne 0) {
    # ── 分支 A：复用已经在运行的程序 ────────────────────────────────────
    $p = if ($ProcessId -ne 0) {
        Get-Process -Id $ProcessId -ErrorAction Stop
    } else {
        Get-Process -Name $ProcessName -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    }
    if (-not $p) { throw "找不到带顶层窗口的进程：$ProcessName$ProcessId" }

    for ($i = 0; $i -lt 20; $i++) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
        Start-Sleep -Milliseconds 250
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw "进程 $($p.ProcessName)($($p.Id)) 没有顶层窗口" }

    $role = ($p.ProcessName -replace '[^A-Za-z0-9]+', '-').ToLower()

    # 注入前留一张底片：第三方程序没有计数器，只能靠前后对比
    $before = Join-Path $WorkDir "external-$role-before.png"
    & $exe shot --hwnd $hwnd.ToInt64() --path $before
    Write-Host "窗口 0x$('{0:X}' -f [int64]$hwnd)  「$([Win]::Title($hwnd))」" -ForegroundColor Green
    Write-Host "注入前截图: $before"
}
else {
    # ── 分支 B：起一个 Chromium 系浏览器打开探针页 ─────────────────────
    if (-not $Browser) {
        $candidates = @(
            "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
            "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
            "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe",
            "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
            "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
        )
        $Browser = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $Browser) { throw "没找到 Chromium 系浏览器，用 -Browser 指定路径" }
    Write-Host "浏览器: $Browser" -ForegroundColor Cyan

    $profile = Join-Path $WorkDir 'chrome-profile'
    # 上一轮没关干净的探针实例要先清掉：同一个 --user-data-dir 上再起一个进程，
    # 它会把自己要打开的窗口交给旧实例，自己直接退出，于是我们永远等不到新窗口
    Get-CimInstance Win32_Process -Filter "Name='chrome.exe' OR Name='msedge.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like '*compat\chrome-profile*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 400

    $page = (Join-Path $PSScriptRoot 'external/probe-page.html') -replace '\\', '/'
    $url = "file:///$page"

    $proc = Start-Process -FilePath $Browser -ArgumentList @(
        "--user-data-dir=$profile",
        '--no-first-run',
        '--no-default-browser-check',
        '--disable-features=Translate,MediaRouter',
        '--window-size=1000,780',
        '--new-window', $url
    ) -PassThru

    # 注意：Chrome 的窗口标题是「页面标题 - Google Chrome」，拿完整标题 FindWindow 是找不到的，
    # 所以取浏览器进程自己的 MainWindowHandle，再核一下标题前缀。
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $proc.Refresh()
        if ($proc.HasExited) { break }
        $h = $proc.MainWindowHandle
        if ($h -ne [IntPtr]::Zero) {
            $t = [Win]::Title($h)
            if ($t -like 'COMPAT-EXTERNAL*') { $hwnd = $h; break }
        }
        # 兜底：有些 Chromium 分支的浏览器进程不持有窗口，按标题前缀找
        $fb = [Win]::FindWindowW($null, 'COMPAT-EXTERNAL - Google Chrome')
        if ($fb -ne [IntPtr]::Zero) { $hwnd = $fb; break }
    }
    if ($hwnd -eq [IntPtr]::Zero) {
        Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
        throw "等不到标题以 COMPAT-EXTERNAL 开头的窗口"
    }
    $role = 'chrome'
}

Write-Host ("从窗口 0x{0:X}" -f [int64]$hwnd) -ForegroundColor Green

try {
    $driverArgs = @(
        'external', '--role', $role, '--hwnd', $hwnd.ToInt64(),
        '--dll', $DllPath, '--out', $WorkDir
    )
    if ($NoDrag) { $driverArgs += @('--nodrag', 'true') }

    $driver = Start-Process -FilePath $exe -ArgumentList $driverArgs -PassThru
    $driver.WaitForExit(180000) | Out-Null
    Write-Host "驱动退出码: $($driver.ExitCode)"

    $shot = Join-Path $WorkDir "external-$role.png"
    Write-Host "注入后截图: $shot"
    $res = Join-Path $WorkDir "result-$role.json"
    if (Test-Path $res) { Write-Host "结果: $res" }
}
finally {
    if ($proc) { Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force }
}
