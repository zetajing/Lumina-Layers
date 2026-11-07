param(
    [Parameter(Mandatory = $true)][int]$ParentId,
    [Parameter(Mandatory = $true)][string]$StagedDirectory,
    [Parameter(Mandatory = $true)][string]$TargetDirectory,
    [switch]$ResumeServices
)

$ErrorActionPreference = 'Stop'
$stage = [IO.Path]::GetFullPath($StagedDirectory)
$target = [IO.Path]::GetFullPath($TargetDirectory)
$executable = Join-Path $target 'LuminaLauncher.exe'
$replacement = Join-Path $stage 'LuminaLauncher.exe'
$rollback = Join-Path $stage 'previous-launcher'
$resultFile = Join-Path $target 'launcher-update-result.json'
if (-not (Test-Path -LiteralPath $replacement -PathType Leaf)) { throw 'The staged launcher is missing.' }
$parent = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
if ($parent -and -not $parent.WaitForExit(60000)) { throw 'The launcher did not exit in time.' }
$names = @('LuminaLauncher.exe', 'LuminaLauncher.exe.config', 'ApplyLauncherUpdate.ps1')
$success = $false
try {
    $null = New-Item -ItemType Directory -Path $rollback -Force
    foreach ($name in $names) {
        $existing = Join-Path $target $name
        if (Test-Path -LiteralPath $existing -PathType Leaf) {
            Copy-Item -LiteralPath $existing -Destination (Join-Path $rollback $name) -Force
        }
    }
    foreach ($name in $names) {
        $source = Join-Path $stage $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $target $name) -Force
        }
    }
    $success = $true
    @{ success = $true; message = '启动器已更新。'; updated_at = (Get-Date).ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $resultFile -Encoding UTF8
} catch {
    $failure = $_.Exception.Message
    foreach ($name in $names) {
        $saved = Join-Path $rollback $name
        if (Test-Path -LiteralPath $saved -PathType Leaf) {
            try { Copy-Item -LiteralPath $saved -Destination (Join-Path $target $name) -Force }
            catch { $failure += "; 恢复 $name 时发生错误：$($_.Exception.Message)" }
        }
    }
    @{ success = $false; message = "启动器替换失败，已尝试恢复上一版：$failure" } |
        ConvertTo-Json | Set-Content -LiteralPath $resultFile -Encoding UTF8
}
$arguments = @()
if ($ResumeServices) { $arguments = @('--resume-services') }
# This is the interactive application the user is updating, so show its window.
$start = @{ FilePath = $executable; WorkingDirectory = $target; WindowStyle = 'Normal' }
if ($arguments.Count) { $start.ArgumentList = $arguments }
Start-Process @start
if (-not $success) { exit 1 }
