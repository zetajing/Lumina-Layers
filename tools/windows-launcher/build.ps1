param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'),
    [switch]$IncludeSmokeTests
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The Windows .NET Framework C# compiler is unavailable. Install .NET Framework 4.8.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $output -Force
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$references = @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Net.Http.dll', '/r:System.Management.dll', '/r:System.Web.Extensions.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll')
$common = @('/nologo', '/optimize+', '/platform:x64', '/utf8output', '/warnaserror+') + $references
$executable = Join-Path $output 'LuminaLauncher.exe'
& $compiler @common '/target:winexe' ('/out:' + $executable) ('/win32icon:' + (Join-Path $projectRoot 'icon.ico')) (Join-Path $PSScriptRoot 'Program.cs') (Join-Path $PSScriptRoot 'MainForm.cs') (Join-Path $PSScriptRoot 'LauncherController.cs') (Join-Path $PSScriptRoot 'UpdateController.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LuminaLauncher.exe.config') -Destination ($executable + '.config') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ApplyLauncherUpdate.ps1') -Destination (Join-Path $output 'ApplyLauncherUpdate.ps1') -Force
if ($IncludeSmokeTests) {
    & $compiler @common '/target:exe' ('/out:' + (Join-Path $output 'LauncherSmokeTests.exe')) (Join-Path $PSScriptRoot 'LauncherController.cs') (Join-Path $PSScriptRoot 'tests\ControllerSmokeTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Smoke test compilation failed.' }
    & $compiler @common '/target:exe' ('/out:' + (Join-Path $output 'UpdaterIntegrationTests.exe')) (Join-Path $PSScriptRoot 'LauncherController.cs') (Join-Path $PSScriptRoot 'UpdateController.cs') (Join-Path $PSScriptRoot 'tests\UpdaterIntegrationTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Updater test compilation failed.' }
}
Get-Item -LiteralPath $executable | Select-Object FullName, Length
