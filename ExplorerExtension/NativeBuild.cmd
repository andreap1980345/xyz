@echo off
setlocal
if "%~1"=="" exit /b 1
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
call "%~1\Common7\Tools\VsDevCmd.bat" -no_logo -arch=x64 -host_arch=x64
if errorlevel 1 exit /b 1
pushd "%~dp0"
if not exist out mkdir out
call BuildObserver.cmd "%~1" x86
if errorlevel 1 goto failed
call BuildObserver.cmd "%~1" x64
if errorlevel 1 goto failed
cl /nologo /std:c++17 /W4 /WX /EHsc /MT /O2 /I..\third_party\Detours\src /c PrepareObserver.cpp /Fo:out\PrepareObserver.obj
if errorlevel 1 goto failed
cl /nologo /std:c++17 /W4 /WX /EHsc /MT /O2 /LD ExplorerBridge.cpp /Fo:out\ExplorerBridge.obj /link out\PrepareObserver.obj ..\third_party\Detours\lib.X64\detours.lib /OUT:out\ExplorerBridge.dll /IMPLIB:out\ExplorerBridge.lib user32.lib shell32.lib ole32.lib oleaut32.lib uuid.lib advapi32.lib
if errorlevel 1 goto failed
cl /nologo /std:c++17 /W4 /WX /EHsc /MT /O2 Child.cpp /Fo:out\Child.obj /link /SUBSYSTEM:WINDOWS /OUT:out\file.exe user32.lib shell32.lib
if errorlevel 1 goto failed
if /i "%~2"=="launcher" goto success
cl /nologo /std:c++17 /W4 /WX /EHsc /MT /O2 /LD MemoryLab.cpp /Fo:out\MemoryLab.obj /link /DEF:MemoryLab.def /OUT:out\MemoryLab.dll /IMPLIB:out\MemoryLab.lib user32.lib ole32.lib uuid.lib comdlg32.lib advapi32.lib
if errorlevel 1 goto failed
cl /nologo /std:c++17 /W4 /WX /EHsc /MT /O2 TestHost.cpp /Fo:out\TestHost.obj /link /OUT:out\TestHost.exe user32.lib ole32.lib uuid.lib
if errorlevel 1 goto failed
out\TestHost.exe "%CD%\out\MemoryLab.dll"
if errorlevel 1 goto failed
:success
popd
exit /b 0
:failed
popd
exit /b 1
