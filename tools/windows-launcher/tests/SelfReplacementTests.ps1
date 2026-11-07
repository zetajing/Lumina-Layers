param(
    [Parameter(Mandatory = $true)][string]$HelperPath,
    [Parameter(Mandatory = $true)][string]$TestDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($TestDirectory)
if ((Test-Path -LiteralPath $root) -and (Get-ChildItem -LiteralPath $root -Force)) { throw 'Use an empty test directory.' }
$null = New-Item -ItemType Directory -Path $root -Force
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$program = @'
using System;
using System.IO;
class ReplacementProbe {
    static void Main(string[] args) {
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "relaunch.txt"), "VERSION|" + string.Join("|", args));
    }
}
'@
foreach ($scenario in @('success', 'locked')) {
    $folder = Join-Path $root $scenario
    $stage = Join-Path $folder 'stage'
    $target = Join-Path $folder 'target'
    $null = New-Item -ItemType Directory -Path $stage,$target -Force
    foreach ($version in @('old', 'new')) {
        $sourceFile = Join-Path $folder ($version + '.cs')
        $program.Replace('VERSION', $version) | Set-Content -LiteralPath $sourceFile -Encoding UTF8
        $destination = if ($version -eq 'old') { $target } else { $stage }
        & $compiler /nologo /target:winexe /platform:x64 ('/out:' + (Join-Path $destination 'LuminaLauncher.exe')) $sourceFile
        if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed.' }
    }
    $parent = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile -Command "Start-Sleep -Milliseconds 300"' -WindowStyle Hidden -PassThru
    $held = $null
    if ($scenario -eq 'locked') {
        $held = [IO.File]::Open((Join-Path $target 'LuminaLauncher.exe'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    }
    try {
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + ([IO.Path]::GetFullPath($HelperPath)) + '" -ParentId ' + $parent.Id + ' -StagedDirectory "' + $stage + '" -TargetDirectory "' + $target + '" -ResumeServices'
        $worker = Start-Process -FilePath powershell.exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $folder 'stdout.log') -RedirectStandardError (Join-Path $folder 'stderr.log')
        if (-not $worker.WaitForExit(30000)) { throw 'Replacement helper timed out.' }
        $marker = Join-Path $target 'relaunch.txt'
        for ($attempt = 0; $attempt -lt 50 -and -not (Test-Path -LiteralPath $marker); $attempt++) { Start-Sleep -Milliseconds 100 }
        if (-not (Test-Path -LiteralPath $marker)) { throw "The launcher did not restart in $scenario." }
        $text = Get-Content -LiteralPath $marker -Raw
        $expected = if ($scenario -eq 'success') { 'new|--resume-services' } else { 'old|--resume-services' }
        if ($text -ne $expected) { throw "Unexpected replacement result: $text" }
        $result = Get-Content -LiteralPath (Join-Path $target 'launcher-update-result.json') -Raw | ConvertFrom-Json
        if ([bool]$result.success -ne ($scenario -eq 'success')) { throw 'Incorrect replacement status.' }
        Write-Output "PASS: $scenario replacement, launcher restart, and service-resume argument."
    } finally {
        if ($held) { $held.Dispose() }
    }
}
