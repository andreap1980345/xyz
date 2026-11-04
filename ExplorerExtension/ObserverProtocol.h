#pragma once
#include <windows.h>

inline constexpr GUID ObserverConfigId =
    {0x72b4d912, 0x4c11, 0x4a32, {0x95, 0xb2, 0xcd, 0x67, 0x89, 0x2e, 0x0d, 0xb8}};
inline constexpr DWORD ObserverMagic = 0x414F4253;
struct ObserverConfig {
    DWORD magic;
    DWORD receiverPid;
    DWORD features; // 1 = file, 2 = Native API
    wchar_t pipeName[160];
};

#pragma pack(push, 1)
struct ObserverEvent {
    DWORD magic, size, kind, pid, tid, success, error, access, disposition, flags;
    ULONGLONG sequence, fileTime, handle, requested, transferred, dropped;
    ULONGLONG address, buffer;
    wchar_t path[512];
};
#pragma pack(pop)
static_assert(sizeof(ObserverEvent) == 1128);
