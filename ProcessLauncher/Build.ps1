param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'app-exercise'))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '..\ExplorerExtension\Build.ps1') -LauncherOnly
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish (Join-Path $PSScriptRoot 'ProcessLauncher.csproj') -c Release --self-contained false -o $OutputDirectory
exit $LASTEXITCODE
