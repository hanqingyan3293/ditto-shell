# Ditto Shell 发布脚本
# 用法：在 src 目录下执行  .\publish.ps1
# 产物：..\publish\fx（小包，需 .NET 8 桌面运行时） 与 ..\publish\sc（自包含单文件，约 70MB）

param(
    [switch]$FrameworkDependentOnly,
    [switch]$SelfContainedOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "DittoShell.App\DittoShell.App.csproj"
$outRoot = Join-Path $root "..\publish"

function Build($name, $args) {
    Write-Host "=== 发布 $name ===" -ForegroundColor Cyan
    $out = Join-Path $outRoot $name
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    & dotnet publish $proj -c Release -r win-x64 -o $out @args
    if ($LASTEXITCODE -ne 0) { throw "发布失败：$name" }

    # 清掉调试符号，减体积
    Get-ChildItem $out -Filter *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

    $size = (Get-ChildItem $out -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
    Write-Host ("  -> {0}  ({1:N1} MB)" -f $out, $size) -ForegroundColor Green
}

if (-not $SelfContainedOnly) {
    Build "fx" @("-p:PublishSingleFile=true", "--self-contained", "false")
}

if (-not $FrameworkDependentOnly) {
    Build "sc" @(
        "-p:PublishSingleFile=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "--self-contained", "true"
    )
}

# 复制许可与说明
foreach ($name in @("fx", "sc")) {
    $dir = Join-Path $outRoot $name
    if (-not (Test-Path $dir)) { continue }

    Copy-Item (Join-Path $root "..\LICENSE") $dir -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root "..\THIRD_PARTY_NOTICES.md") $dir -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root "..\README.md") $dir -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "完成。" -ForegroundColor Green
Write-Host "  fx : 约 3 MB，需要用户安装 .NET 8 桌面运行时"
Write-Host "  sc : 约 70 MB，解压即用（推荐给普通用户）"
