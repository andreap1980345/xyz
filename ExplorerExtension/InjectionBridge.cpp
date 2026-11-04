#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <strsafe.h>
#include <cwchar>
#include "InjectionProtocol.h"
#include "ParentLaunch.h"

namespace {
thread_local DWORD lastStage = 0;
struct OwnedHandle {
    HANDLE value = nullptr;
    ~OwnedHandle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct OwnedModule {
    HMODULE value = nullptr;
    ~OwnedModule() { if (value) FreeLibrary(value); }
};
struct RemoteMemory {
    HANDLE process;
    void* address = nullptr;
    bool retain = false;
    ~RemoteMemory() { if (address && !retain) VirtualFreeEx(process, address, 0, MEM_RELEASE); }
};

ULONG_PTR ModuleBase(DWORD pid, LPCWSTR name, bool fullPath) {
    OwnedHandle snapshot;
    for (int attempt = 0; attempt < 8; ++attempt) {
        snapshot.value = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snapshot.value != INVALID_HANDLE_VALUE) break;
        if (GetLastError() != ERROR_BAD_LENGTH) return 0;
        Sleep(10);
    }
    if (snapshot.value == INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (!Module32FirstW(snapshot.value, &entry)) return 0;
    do {
        if (lstrcmpiW(fullPath ? entry.szExePath : entry.szModule, name) == 0)
            return reinterpret_cast<ULONG_PTR>(entry.modBaseAddr);
    } while (Module32NextW(snapshot.value, &entry));
    SetLastError(ERROR_MOD_NOT_FOUND);
    return 0;
}

LPTHREAD_START_ROUTINE SystemRoutine(DWORD pid, LPCSTR name) {
    auto function = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), name);
    if (!function) return nullptr;
    HMODULE owner = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
        GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(function), &owner)) return nullptr;
    wchar_t image[32768];
    DWORD length = GetModuleFileNameW(owner, image, ARRAYSIZE(image));
    if (!length || length >= ARRAYSIZE(image)) { SetLastError(ERROR_INSUFFICIENT_BUFFER); return nullptr; }
    const wchar_t* filename = std::wcsrchr(image, L'\\');
    ULONG_PTR remote = ModuleBase(pid, filename ? filename + 1 : image, false);
    if (!remote) return nullptr;
    return reinterpret_cast<LPTHREAD_START_ROUTINE>(remote +
        reinterpret_cast<ULONG_PTR>(function) - reinterpret_cast<ULONG_PTR>(owner));
}

HRESULT RunRemote(HANDLE process, LPTHREAD_START_ROUTINE routine, void* parameter,
    bool* completed, DWORD* exitCode) {
    *completed = false;
    OwnedHandle thread;
    thread.value = CreateRemoteThread(process, nullptr, 0, routine, parameter, 0, nullptr);
    if (!thread.value) { *completed = true; return HRESULT_FROM_WIN32(GetLastError()); }
    DWORD wait = WaitForSingleObject(thread.value, 15000);
    if (wait == WAIT_TIMEOUT) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    if (wait != WAIT_OBJECT_0) return HRESULT_FROM_WIN32(GetLastError());
    *completed = true;
    if (!GetExitCodeThread(thread.value, exitCode)) return HRESULT_FROM_WIN32(GetLastError());
    return S_OK;
}

HRESULT AcquireFromExplorer(DWORD pid, LPCWSTR workerPath, HANDLE* result) {
    *result = nullptr;
    OwnedHandle explorer;
    lastStage = 2;
    explorer.value = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
        PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!explorer.value) return HRESULT_FROM_WIN32(GetLastError());
    auto loader = SystemRoutine(pid, "LoadLibraryW");
    auto unloader = SystemRoutine(pid, "FreeLibrary");
    if (!loader || !unloader) return HRESULT_FROM_WIN32(GetLastError());

    // Il caricamento locale serve solo a ricavare l'offset dell'esportazione.
    // La funzione che produce l'handle viene chiamata esclusivamente in Explorer.
    OwnedModule local;
    lastStage = 3;
    local.value = LoadLibraryExW(workerPath, nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!local.value) return HRESULT_FROM_WIN32(GetLastError());
    auto exported = GetProcAddress(local.value, "AcquireParentHandle");
    if (!exported) return HRESULT_FROM_WIN32(GetLastError());
    SIZE_T bytes = (std::wcslen(workerPath) + 1) * sizeof(wchar_t);
    RemoteMemory path{ explorer.value };
    path.address = VirtualAllocEx(explorer.value, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!path.address) return HRESULT_FROM_WIN32(GetLastError());
    SIZE_T transferred = 0;
    if (!WriteProcessMemory(explorer.value, path.address, workerPath, bytes, &transferred)
        || transferred != bytes) return HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);
    bool completed = false;
    DWORD code = 0;
    lastStage = 4;
    HRESULT status = RunRemote(explorer.value, loader, path.address, &completed, &code);
    if (!completed) path.retain = true; // Un thread in corso potrebbe usare ancora il buffer.
    if (FAILED(status)) return status;
    if (!code) return HRESULT_FROM_WIN32(ERROR_DLL_INIT_FAILED);
    ULONG_PTR remoteModule = ModuleBase(pid, workerPath, true);
    if (!remoteModule) return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);

    bool mayUnload = true;
    {
        ParentHandleRequest request{ RequestMagic, GetCurrentProcessId(), pid, ERROR_GEN_FAILURE, 0, 0 };
        RemoteMemory data{ explorer.value };
        data.address = VirtualAllocEx(explorer.value, nullptr, sizeof(request), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!data.address) status = HRESULT_FROM_WIN32(GetLastError());
        else if (!WriteProcessMemory(explorer.value, data.address, &request, sizeof(request), &transferred)
            || transferred != sizeof(request)) status = HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);
        else {
            auto routine = reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteModule +
                reinterpret_cast<ULONG_PTR>(exported) - reinterpret_cast<ULONG_PTR>(local.value));
            lastStage = 5;
            status = RunRemote(explorer.value, routine, data.address, &completed, &code);
            if (!completed) { data.retain = true; mayUnload = false; }
            if (SUCCEEDED(status)) {
                if (!ReadProcessMemory(explorer.value, data.address, &request, sizeof(request), &transferred)
                    || transferred != sizeof(request)) status = HRESULT_FROM_WIN32(ERROR_READ_FAULT);
                else if (code != ERROR_SUCCESS || request.error != ERROR_SUCCESS || !request.handle) {
                    lastStage = 100 + request.phase;
                    status = HRESULT_FROM_WIN32(request.error ? request.error : code ? code : ERROR_INVALID_HANDLE);
                }
                else *result = reinterpret_cast<HANDLE>(request.handle);
            }
        }
    }
    if (mayUnload) {
        HRESULT unloadStatus = RunRemote(explorer.value, unloader,
            reinterpret_cast<void*>(remoteModule), &completed, &code);
        if (SUCCEEDED(status) && (FAILED(unloadStatus) || !code)) {
            lastStage = 6;
            status = FAILED(unloadStatus) ? unloadStatus : HRESULT_FROM_WIN32(ERROR_DLL_INIT_FAILED);
        }
    }
    if (FAILED(status) && *result) { CloseHandle(*result); *result = nullptr; }
    return status;
}
}

extern "C" __declspec(dllexport) DWORD WINAPI GetInjectionStage() { return lastStage; }

extern "C" __declspec(dllexport) HRESULT WINAPI CreateViaInjectedDll(
    LPCWSTR executable, LPCWSTR arguments, DWORD* parentPid, DWORD* childPid) {
    if (!executable || !*executable || !parentPid || !childPid) return E_INVALIDARG;
    *parentPid = 0;
    *childPid = 0;
    lastStage = 1;
    HWND shell = GetShellWindow();
    if (!shell || !GetWindowThreadProcessId(shell, parentPid)) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    static const int token = 0;
    HMODULE bridge = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
        GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&token), &bridge))
        return HRESULT_FROM_WIN32(GetLastError());
    wchar_t worker[32768];
    DWORD length = GetModuleFileNameW(bridge, worker, ARRAYSIZE(worker));
    if (!length || length >= ARRAYSIZE(worker)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    wchar_t* name = std::wcsrchr(worker, L'\\');
    if (!name) return E_FAIL;
    ++name;
    HRESULT status = StringCchCopyW(name, ARRAYSIZE(worker) - (name - worker), L"ExplorerWorker.dll");
    if (FAILED(status)) return status;
    OwnedHandle parent;
    status = AcquireFromExplorer(*parentPid, worker, &parent.value);
    if (FAILED(status)) return status;
    if (GetProcessId(parent.value) != *parentPid) return HRESULT_FROM_WIN32(ERROR_INVALID_HANDLE);
    PROCESS_INFORMATION child{};
    lastStage = 7;
    status = CreateChildWithParentHandle(executable, arguments, parent.value, &child);
    if (SUCCEEDED(status)) {
        *childPid = child.dwProcessId;
        CloseHandle(child.hThread);
        CloseHandle(child.hProcess);
        lastStage = 8;
    }
    return status;
}
