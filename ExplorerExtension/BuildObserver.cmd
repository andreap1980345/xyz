@echo off
setlocal
call "%~1\Common7\Tools\VsDevCmd.bat" -no_logo -arch=%~2 -host_arch=x64
if errorlevel 1 exit /b 1
pushd "%~dp0..\third_party\Detours\src"
if /i "%~2"=="x86" (set "OBSERVER_CPU=X86") else (set "OBSERVER_CPU=X64")
nmake /nologo DETOURS_TARGET_PROCESSOR=%OBSERVER_CPU%
if errorlevel 1 goto failed
popd
pushd "%~dp0"
if /i "%~2"=="x86" (set "OBSERVER_BITS=32") else (set "OBSERVER_BITS=64")
cl /nologo /std:c++17 /W4 /WX /EHsc /MT /O2 /I..\third_party\Detours\src /LD ApiObserver.cpp /Fo:out\ApiObserver%OBSERVER_BITS%.obj /link /DEF:ApiObserver.def ..\third_party\Detours\lib.%OBSERVER_CPU%\detours.lib /OUT:out\ApiObserver%OBSERVER_BITS%.dll /IMPLIB:out\ApiObserver%OBSERVER_BITS%.lib
if errorlevel 1 goto failed
if /i "%~2"=="x86" (
  cl /nologo /std:c++17 /W4 /WX /EHsc /MT /O2 Child.cpp /Fo:out\Child32.obj /link /SUBSYSTEM:WINDOWS /OUT:out\file32.exe user32.lib shell32.lib
  if errorlevel 1 goto failed
  cl /nologo /std:c++17 /W4 /WX /EHsc /MT /O2 /DOBSERVER_SETUP_EXE /I..\third_party\Detours\src PrepareObserver.cpp /Fo:out\ObserverSetup32.obj /link ..\third_party\Detours\lib.X86\detours.lib /SUBSYSTEM:WINDOWS /OUT:out\ObserverSetup32.exe shell32.lib
  if errorlevel 1 goto failed
)
popd
exit /b 0
:failed
popd
exit /b 1
