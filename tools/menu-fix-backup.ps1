param(
    [ValidateSet('Backup', 'Restore')][string]$Action = 'Backup',
    [string[]]$RelativePaths,
    [string]$Snapshot
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-ProjectPath([string]$relativePath) {
    $fullPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $relativePath))
    if (-not $fullPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Path is outside the project.'
    }
    if ($relativePath -match '^(reference|data)([\/]|$)' -or $relativePath -match 'Ditto.db$') {
        throw 'Reference files and Ditto data are excluded.'
    }
    return $fullPath
}

if ($Action -eq 'Backup') {
    if (-not $RelativePaths) { throw 'RelativePaths is required.' }
    $Snapshot = Join-Path $projectRoot ('backup/menu-ui-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $Snapshot | Out-Null
    $entries = foreach ($relativePath in $RelativePaths) {
        $source = Get-ProjectPath $relativePath
        $exists = Test-Path -LiteralPath $source -PathType Leaf
        if ($exists) {
            $destination = Join-Path $Snapshot $relativePath
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
        }
        [pscustomobject]@{ Path = $relativePath; Existed = $exists }
    }
    @($entries) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Snapshot 'manifest.json') -Encoding UTF8
    Write-Output $Snapshot
    exit 0
}

if (-not $Snapshot) { throw 'Snapshot is required for Restore.' }
$Snapshot = [IO.Path]::GetFullPath($Snapshot)
$backupRoot = Join-Path $projectRoot 'backup'
if (-not $Snapshot.StartsWith($backupRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Snapshot must be inside the project backup directory.'
}
$entries = Get-Content -LiteralPath (Join-Path $Snapshot 'manifest.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$appPath = Join-Path $projectRoot 'src/DittoShell.App/bin/Debug/net8.0-windows/DittoShell.exe'
Get-Process -Name DittoShell -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $appPath } | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    if (-not $_.WaitForExit(10000)) { throw 'DittoShell did not exit; no files were restored.' }
}
foreach ($entry in $entries) {
    $destination = Get-ProjectPath $entry.Path
    if ($entry.Existed) {
        $source = [IO.Path]::GetFullPath((Join-Path $Snapshot $entry.Path))
        if (-not $source.StartsWith($Snapshot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Invalid snapshot entry.'
        }
        Copy-Item -LiteralPath $source -Destination $destination -Force
    } elseif (Test-Path -LiteralPath $destination -PathType Leaf) {
        Remove-Item -LiteralPath $destination
    }
}
Start-Process -FilePath $appPath -WindowStyle Hidden
Write-Output 'RESTORED'
