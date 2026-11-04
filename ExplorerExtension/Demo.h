#pragma once
#include <windows.h>

// Identificatore esclusivo di questa estensione COM.
inline constexpr CLSID CLSID_MemoryLab =
    {0xc9145681, 0xa183, 0x41ac, {0x86, 0x32, 0x29, 0xd7, 0xdf, 0xbd, 0x76, 0x51}};

struct DemoState {
    DWORD processId;
    LONG counter;
    ULONG_PTR counterAddress;
};

using GetDemoStateFunction = void (WINAPI*)(DemoState*);
