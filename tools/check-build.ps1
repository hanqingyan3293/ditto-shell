# Check if exe is newer than the latest source file.
# Exit 1 if stale (build likely failed or exe locked).
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$SrcDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    Write-Host "[FAIL] exe not found: $ExePath" -ForegroundColor Red
    exit 1
}

$exe = Get-Item $ExePath

$src = Get-ChildItem -Path $SrcDir -Recurse -Include *.cs, *.xaml -ErrorAction SilentlyContinue |
       Where-Object {
           $_.FullName -notlike '*\obj\*' -and
           $_.FullName -notlike '*\bin\*'
       } |
       Sort-Object LastWriteTime -Descending |
       Select-Object -First 1

if ($null -eq $src) {
    Write-Host "[WARN] no source files found, skip check" -ForegroundColor Yellow
    exit 0
}

if ($exe.LastWriteTime -lt $src.LastWriteTime) {
    Write-Host "[FAIL] exe is OLDER than source - build did not take effect" -ForegroundColor Red
    Write-Host "  exe  : $($exe.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor Red
    Write-Host "  source: $($src.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))  $($src.Name)" -ForegroundColor Red
    Write-Host "  The exe may be locked. Close DittoShell and retry." -ForegroundColor Yellow
    exit 1
}

Write-Host "  OK  exe=$($exe.LastWriteTime.ToString('HH:mm:ss'))  src=$($src.LastWriteTime.ToString('HH:mm:ss'))" -ForegroundColor Green
exit 0
