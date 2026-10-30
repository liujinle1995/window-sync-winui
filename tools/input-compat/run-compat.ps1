#Requires -Version 7.0
<#
.SYNOPSIS
    输入兼容性矩阵：一键构建 + 逐目标跑测试 + 汇总。

.DESCRIPTION
    对每个目标类型（win32 / winforms / wpf / rawinput）单独跑一轮：
      1. 从 core/target/release 复制一份 DLL 到工作目录（避免占着 cargo 的构建产物）
      2. 起目标进程 + 主窗口（COMPAT-MASTER），调 wsync_start_sync 挂上从窗口
      3. 用 SendInput 生成真实键鼠事件（只有真实输入才会被低级钩子看见）
      4. 把目标端在场景时间窗内实际收到的消息数出来，写 result-<role>.json
    最后合并成 results.json 并把关键数字打到屏幕上。

    DLL 不写回 core/target，也不跑 cargo build —— 手里有一份能加载的 DLL 就够。

.PARAMETER Roles
    要跑的目标，默认四类全跑。

.PARAMETER DllPath
    被测 DLL，默认 core/target/release/window_sync_core.dll；会被复制到 <WorkDir>/dll/。

.PARAMETER WorkDir
    临时产物目录：日志、结果、DLL 副本。默认 .workbuddy/tmp/compat

.PARAMETER NoBuild
    跳过 dotnet build，直接用已有的 bin。

.EXAMPLE
    pwsh -File tools/input-compat/run-compat.ps1
    pwsh -File tools/input-compat/run-compat.ps1 -Roles winforms -NoBuild
#>
[CmdletBinding()]
param(
    [string[]]$Roles = @('win32', 'winforms', 'wpf', 'rawinput'),
    [string]$DllPath,
    [string]$WorkDir,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $DllPath) { $DllPath = Join-Path $repo 'core/target/release/window_sync_core.dll' }
if (-not $WorkDir) { $WorkDir = Join-Path $repo '.workbuddy/tmp/compat' }

$binDir = Join-Path $WorkDir 'bin'
$dllDir = Join-Path $WorkDir 'dll'
$exe    = Join-Path $binDir 'CompatTool.exe'

New-Item -ItemType Directory -Force -Path $WorkDir, $dllDir | Out-Null

# ── 1. DLL 副本 ──────────────────────────────────────────────────────────
if (-not (Test-Path $DllPath)) { throw "找不到 DLL: $DllPath（先确认 core/target/release 下有构建产物）" }

$dllCopy = Join-Path $dllDir 'window_sync_core.dll'
Copy-Item $DllPath $dllCopy -Force
$hash = (Get-FileHash $dllCopy -Algorithm SHA256).Hash
$ver  = (Get-Item $dllCopy).VersionInfo.FileVersion
Write-Host "DLL: $dllCopy" -ForegroundColor Cyan
Write-Host "SHA256: $hash"

# 记录被测 DLL 的来源，文档里的哈希以这里为准
[pscustomobject]@{
    source    = (Resolve-Path $DllPath).Path
    copy      = $dllCopy
    sha256    = $hash
    sizeBytes = (Get-Item $dllCopy).Length
    fileVersion = $ver
    takenAt   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
} | ConvertTo-Json | Set-Content (Join-Path $WorkDir 'dll-info.json') -Encoding utf8

# ── 2. 构建 ──────────────────────────────────────────────────────────────
# 先把残留进程清掉：CompatTool.exe 被占着时 dotnet build 会静默失败，
# 于是跑的还是上一版二进制，结果会莫名其妙地对不上（踩过这个坑）。
Get-Process -Name CompatTool -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

if (-not $NoBuild) {
    Write-Host "构建 CompatTool..." -ForegroundColor Cyan
    dotnet build (Join-Path $PSScriptRoot 'CompatTool/CompatTool.csproj') -c Release -o $binDir --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败" }
}
if (-not (Test-Path $exe)) { throw "找不到 $exe" }

# ── 3. 逐目标跑 ──────────────────────────────────────────────────────────
# 这台机器上有人正常用键鼠，真实的输入会混进场景窗口（人手划一下鼠标 → 场景窗口里
# 多出几十条 WM_MOUSEMOVE、postonly 窗口里冒出真实 WM_INPUT、甚至前台被抢走）。
# 判定方式不看「前台有没有被抢过」——那太严，一帧的抖动就会让整轮作废；
# 直接看**这一轮的数据形状对不对**：该到的条数到了没、该有的顺序对不对。
function Test-Interfered($r) {
    $kb = $r.scenarios.keyboard
    if (-not $kb -or $kb.counts.WM_KEYDOWN -ne 3) {
        return "键盘只收到 $($kb.counts.WM_KEYDOWN) 条 WM_KEYDOWN（应为 3）"
    }
    if (($kb.keydownVk -join ',') -ne '65,66,67') {
        return "键盘 vk 序列是 [$($kb.keydownVk -join ',')]（应为 65,66,67）"
    }
    if ($r.scenarios.mousemove.attributed -ne 3) {
        return "移动场景里只有 $($r.scenarios.mousemove.attributed)/3 条能对上我方注入时刻"
    }
    if ($r.scenarios.click.counts.WM_LBUTTONDOWN -ne 1 -or $r.scenarios.click.counts.WM_LBUTTONUP -ne 1) {
        return "点击场景 down/up 条数不对（$($r.scenarios.click.counts.WM_LBUTTONDOWN)/$($r.scenarios.click.counts.WM_LBUTTONUP)）"
    }
    if ($r.scenarios.wheel.count -ne 2) {
        return "滚轮场景收到 $($r.scenarios.wheel.count) 条 WM_MOUSEWHEEL（应为 2）"
    }
    if ($r.scenarios.drag.downUpOrdered -ne $true -or $r.scenarios.drag.movesBetweenDownUp -lt 3) {
        return "拖动场景顺序不对（DOWN→MOVE×n→UP 之间只有 $($r.scenarios.drag.movesBetweenDownUp) 条移动）"
    }
    $po = $r.scenarios.postonly
    if ($null -ne $po.rawKbdA -and ([int]$po.rawKbdA -gt 0 -or [int]$po.rawMouseButtons -gt 0)) {
        return "postonly 窗口内出现真实键/鼠标按键（rawKbdA=$($po.rawKbdA) rawBtn=$($po.rawMouseButtons)）"
    }
    return $null
}

$summary = @()
foreach ($role in $Roles) {
    Write-Host "`n=== 目标: $role ===" -ForegroundColor Yellow

    $resultFile = Join-Path $WorkDir "result-$role.json"
    $r = $null
    $why = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        # 清掉上一轮可能残留的目标进程，避免窗口叠在一起
        Get-Process -Name CompatTool -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

        # 注意：不用 Start-Process -Wait —— 它会把驱动自己拉起的子进程（目标窗口）一起等，
        # 而目标窗口本来就该活到驱动结束之后才被 kill。
        $p = Start-Process -FilePath $exe `
            -ArgumentList @('driver', '--role', $role, '--dll', $dllCopy, '--out', $WorkDir) `
            -PassThru
        $p.WaitForExit()

        if (-not (Test-Path $resultFile)) { Write-Warning "第 $attempt 轮没有产出 $resultFile"; continue }

        $r = Get-Content $resultFile -Raw | ConvertFrom-Json
        $why = Test-Interfered $r
        if (-not $why) { Write-Host "  第 $attempt 轮：数据干净" -ForegroundColor Green; break }

        Write-Warning "  第 $attempt 轮被干扰：$why —— 重跑"
        $r = $null
    }

    if (-not $r) { Write-Warning "  三轮都被干扰，这一轮的数据不可用"; continue }
    if ($why) { Write-Warning "  最终采用的一轮仍有干扰：$why" }

    $summary += $r
    Write-Host ("  键盘: keydown={0} char={1} 文本+='{2}'" -f `
        $r.scenarios.keyboard.counts.WM_KEYDOWN, $r.scenarios.keyboard.counts.WM_CHAR, $r.scenarios.keyboard.addedText)
    Write-Host ("  移动: 我方 {0}/3 条, 窗口内共 {1} 条, 落在客户区内={2}, MK 值={3}, 注入点=[{4}]" -f `
        $r.scenarios.mousemove.attributed, $r.scenarios.mousemove.count, $r.scenarios.mousemove.allInsideClient, `
        ($r.scenarios.mousemove.mkValues -join '/'), ($r.scenarios.mousemove.attributedPoints -join ' '))
    Write-Host ("  点击: {0}" -f (($r.scenarios.click.counts.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '))
    Write-Host ("  滚轮: {0} 条 delta={1} 屏幕点在从窗口内={2}" -f `
        $r.scenarios.wheel.count, ($r.scenarios.wheel.deltas -join ','), $r.scenarios.wheel.pointInsideSlaveClient)
    Write-Host ("  拖动: {0}（DOWN→UP 之间 {1} 条移动，带 MK_LBUTTON {2} 条）" -f `
        $r.scenarios.drag.sequenceText, $r.scenarios.drag.movesBetweenDownUp, $r.scenarios.drag.moveWithLeftButton)
    Write-Host ("  中文: 文本+='{0}' 切换前台 {1} 次共 {2}ms  剪贴板就绪={3} 核心日志有剪贴板中转={4}" -f `
        $r.scenarios.chinese.addedText, $r.scenarios.chinese.foreground.leftMasterCount, $r.scenarios.chinese.foreground.totalAwayMs, `
        $r.scenarios.chinese.clipboardReady, $r.scenarios.chinese.coreLoggedClipboard)
    Write-Host ("  镜像: 文本+='{0}' 收到字符 {1} 条" -f `
        $r.scenarios.mirror.addedText, $r.scenarios.mirror.charsReceived)
    $cm = $r.scenarios.childmsg
    Write-Host ("  子控件直投: 子窗口={0} 类名={1} 子控件收到=[{2}] 子控件文本='{3}'" -f `
        $cm.childHwnd, $cm.childClass, ($cm.messagesAtChild -join ','), $cm.childTextAfter)
    Write-Host ("  直投对照: {0} / RawInput kbdA={1} 按键={2} 纯移动={3}" -f `
        (($r.scenarios.postonly.counts.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '), `
        $r.scenarios.postonly.rawKbdA, $r.scenarios.postonly.rawMouseButtons, $r.scenarios.postonly.rawMouseMoves)
}

# ── 4. 汇总 ──────────────────────────────────────────────────────────────
$all = [pscustomobject]@{
    generatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    dll         = (Get-Content (Join-Path $WorkDir 'dll-info.json') -Raw | ConvertFrom-Json)
    results     = $summary
}
$all | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $WorkDir 'results.json') -Encoding utf8
Write-Host "`n汇总: $(Join-Path $WorkDir 'results.json')" -ForegroundColor Green

# 目标进程异常退出的话清一下，避免残留窗口影响下一轮
Get-Process -Name CompatTool -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
