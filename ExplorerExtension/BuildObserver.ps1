$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Installa Visual Studio Build Tools con Sviluppo di applicazioni desktop con C++ e Windows SDK.'
}

$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $installation) {
    throw 'Strumenti C++ x86/x64 non trovati. Installali tramite Visual Studio Installer.'
}

$outputDirectory = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

foreach ($architecture in @('x86', 'x64')) {
    Write-Host "Compilazione ApiObserver: $architecture"
    & (Join-Path $PSScriptRoot 'BuildObserver.cmd') $installation $architecture
    if ($LASTEXITCODE -ne 0) {
        throw "Compilazione $architecture fallita (codice $LASTEXITCODE)."
    }
}

Write-Host 'Compilazione completata. DLL disponibili in:'
Write-Host (Join-Path $outputDirectory 'ApiObserver32.dll')
Write-Host (Join-Path $outputDirectory 'ApiObserver64.dll')
