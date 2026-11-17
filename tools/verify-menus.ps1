param([switch]$NativeAlignment)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appPath = Join-Path $projectRoot 'src/DittoShell.App/bin/Debug/net8.0-windows/DittoShell.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw 'Build DittoShell first.' }
$reportDir = Join-Path $projectRoot 'data/menu-verification'
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$reportPath = Join-Path $reportDir $(if ($NativeAlignment) { 'native.json' } else { 'fixed.json' })
if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }
$arguments = '--verify-menus "' + $reportPath + '"'
if ($NativeAlignment) { $arguments += ' --native-menu-alignment' }
$process = Start-Process -FilePath $appPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(55000)) {
    $process | Stop-Process
    throw 'Menu verification timed out after 55 seconds.'
}
$process.WaitForExit()
if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    throw ('No verification report. Exit code: ' + $process.ExitCode)
}
$report = Get-Content -LiteralPath $reportPath -Encoding UTF8 -Raw | ConvertFrom-Json
Write-Output ('Passed={0} Failed={1} NativeAlignment={2}' -f $report.Passed, $report.Failed, $report.NativeAlignment)
$report.Results | Where-Object { -not $_.Passed } | Select-Object -First 12 | ForEach-Object {
    Write-Output ('FAIL {0}: {1}' -f $_.Name, $_.Detail)
}
Write-Output ('Report: ' + $reportPath)
exit $process.ExitCode
