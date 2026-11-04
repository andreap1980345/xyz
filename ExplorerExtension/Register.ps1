param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Esegui lo script da PowerShell a 64 bit.' }
$classId = '{C9145681-A183-41AC-8632-29D7DFBD7651}'
$classKey = "Software\Classes\CLSID\$classId"
$handlerKey = 'Software\Classes\Directory\Background\shellex\ContextMenuHandlers\MemoryLabDemo'

if ($Uninstall) {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($handlerKey, $false)
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($classKey, $false)
    Write-Output 'Estensione rimossa per questo utente. La DLL potrebbe restare caricata fino alla chiusura del processo ospite.'
} else {
    $library = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'out\MemoryLab.dll')).Path
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$classKey\InprocServer32")
    try {
        $key.SetValue('', $library)
        $key.SetValue('ThreadingModel', 'Apartment')
    } finally { $key.Dispose() }
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($handlerKey)
    try { $key.SetValue('', $classId) } finally { $key.Dispose() }
    Write-Output 'Estensione registrata per questo utente. Clic destro sullo sfondo di una cartella > Mostra altre opzioni > Scegli exe e avvia dalla DLL (padre esplicito).'
}

# Notifica la modifica alla Shell senza interrompere Explorer.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MemoryLabShellNotify {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
'@
[MemoryLabShellNotify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
