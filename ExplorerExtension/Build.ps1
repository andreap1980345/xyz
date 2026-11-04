param([switch]$LauncherOnly)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Installa Visual Studio con gli strumenti C++ x64.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'Strumenti C++ x64 non trovati.' }
$target = if ($LauncherOnly) { 'launcher' } else { 'all' }
& (Join-Path $PSScriptRoot 'NativeBuild.cmd') $installation $target
exit $LASTEXITCODE
