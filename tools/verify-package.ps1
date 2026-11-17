# 发布包自检（PowerShell 5.1 兼容，无函数定义避免作用域坑）
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify-package.ps1 -Path publish\sc

param(
    [string]$Path = "publish\sc"
)

$ErrorActionPreference = "Continue"
$fail = 0
$warn = 0

$root = Resolve-Path (Join-Path $PSScriptRoot "..\$Path") -ErrorAction SilentlyContinue
if (-not $root) {
    Write-Host "找不到发布目录：$Path"
    exit 1
}

Write-Host ""
Write-Host "检查目录：$root" -ForegroundColor Cyan
Write-Host ""

# ── 1. 必需文件 ──
Write-Host "[1/4] 必需文件"
$exePath = Join-Path $root "DittoShell.exe"
if (Test-Path $exePath) { Write-Host "  OK   DittoShell.exe" -ForegroundColor Green }
else { Write-Host "  FAIL DittoShell.exe" -ForegroundColor Red; $fail = $fail + 1 }

foreach ($f in @("LICENSE", "THIRD_PARTY_NOTICES.md")) {
    $fp = Join-Path $root $f
    if (Test-Path $fp) { Write-Host "  OK   $f" -ForegroundColor Green }
    else { Write-Host "  WARN $f 缺失（发布前从仓库根复制）" -ForegroundColor Yellow; $warn = $warn + 1 }
}

# ── 2. 资源与体积 ──
Write-Host "[2/4] 资源与体积"
$res = Join-Path $root "Resources"
if (Test-Path $res) {
    $jsons = @(Get-ChildItem $res -Filter "*.json" -ErrorAction SilentlyContinue)
    Write-Host ("  OK   Resources ({0} 个 json)" -f $jsons.Count) -ForegroundColor Green
} else {
    Write-Host "  FAIL Resources 目录缺失" -ForegroundColor Red
    $fail = $fail + 1
}

if (Test-Path $exePath) {
    $mb = (Get-Item $exePath).Length / 1MB
    Write-Host ("  DittoShell.exe = {0:N1} MB" -f $mb)
    if ($mb -gt 120) { Write-Host "  WARN 体积偏大" -ForegroundColor Yellow; $warn = $warn + 1 }
}

# ── 3. 首次运行 ──
Write-Host "[3/4] 首次运行"
$tmpData = Join-Path $root "data"
$tmpBackup = Join-Path $root "backup"
if (Test-Path $tmpData) { Remove-Item $tmpData -Recurse -Force }
if (Test-Path $tmpBackup) { Remove-Item $tmpBackup -Recurse -Force }

$shot = Join-Path $env:TEMP ("dittoshell_verify.png")
if (Test-Path $shot) { Remove-Item $shot -Force }

$proc = Start-Process -FilePath $exePath -ArgumentList @("--shot", $shot, "main") -PassThru -WindowStyle Hidden
$ok = $proc.WaitForExit(40000)

if ($ok) { Write-Host "  OK   进程启动并退出" -ForegroundColor Green }
else { Write-Host "  FAIL 进程 40 秒未退出" -ForegroundColor Red; $fail = $fail + 1 }

if (Test-Path $shot) {
    Write-Host "  OK   界面渲染成功" -ForegroundColor Green
    Remove-Item $shot -Force
} else {
    Write-Host "  FAIL 截图未生成（界面渲染失败）" -ForegroundColor Red
    $fail = $fail + 1
}

if (Test-Path $tmpData) { Write-Host "  OK   自动创建 data/" -ForegroundColor Green }
else { Write-Host "  FAIL 未创建 data/" -ForegroundColor Red; $fail = $fail + 1 }

if (Test-Path (Join-Path $tmpData "sidecar.db")) { Write-Host "  OK   旁路库 sidecar.db" -ForegroundColor Green }
else { Write-Host "  FAIL 旁路库未创建" -ForegroundColor Red; $fail = $fail + 1 }

# ── 4. Ditto 数据库 ──
Write-Host "[4/4] Ditto 数据库"
$found = $null
$candidates = @(
    "D:\PortableGreen-APP\Ditto",
    "$env:LOCALAPPDATA\Ditto",
    "$env:APPDATA\Ditto",
    "$env:ProgramFiles\Ditto"
)
foreach ($c in $candidates) {
    if (Test-Path $c) {
        $f = Get-ChildItem $c -Filter "Ditto.db" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($f) { $found = $f; break }
    }
}

if ($found) {
    Write-Host ("  找到：{0}" -f $found.FullName)
    Write-Host ("  大小：{0:N0} MB（程序只读打开，不改动）" -f ($found.Length / 1MB))
} else {
    Write-Host "  WARN 未找到 Ditto.db（换机器后请确认 Ditto 已安装）" -ForegroundColor Yellow
    $warn = $warn + 1
}

# ── 结果 ──
Write-Host ""
if ($fail -eq 0) {
    Write-Host ("自检通过（{0} 个警告）" -f $warn) -ForegroundColor Green
    exit 0
} else {
    Write-Host ("自检失败：{0} 项" -f $fail) -ForegroundColor Red
    exit 1
}
