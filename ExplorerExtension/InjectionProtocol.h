#pragma once
#include <windows.h>

inline constexpr DWORD RequestMagic = 0x4D4C4231;
struct ParentHandleRequest {
    DWORD magic;
    DWORD receiverPid;
    DWORD explorerPid;
    DWORD error;
    DWORD phase;
    ULONG_PTR handle;
};
