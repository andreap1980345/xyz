#pragma once
#include <windows.h>
#include <winternl.h>

using QueryMemory = LONG (NTAPI*)(HANDLE, PVOID, ULONG, PVOID, SIZE_T, PSIZE_T);
using QuerySystem = LONG (NTAPI*)(ULONG, PVOID, ULONG, PULONG);
using QueryProcess = LONG (NTAPI*)(HANDLE, ULONG, PVOID, ULONG, PULONG);
using ReadMemory = LONG (NTAPI*)(HANDLE, PVOID, PVOID, SIZE_T, PSIZE_T);
using WriteMemory = LONG (NTAPI*)(HANDLE, PVOID, const VOID*, SIZE_T, PSIZE_T);
