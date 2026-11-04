#pragma once
#include <windows.h>
#include <strsafe.h>

// Restituisce gli handle del nuovo processo al chiamante, che li deve chiudere.
inline HRESULT CreateChildWithParentHandle(LPCWSTR executable, LPCWSTR arguments,
    HANDLE parent, PROCESS_INFORMATION* child, LPCWSTR workingDirectory = nullptr, DWORD additionalFlags = 0,
    HANDLE userToken = nullptr) {
    if (!executable || !*executable || !parent || !child) return E_INVALIDARG;
    *child = {};
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    if (!size) {
        DWORD error = GetLastError();
        return HRESULT_FROM_WIN32(error);
    }
    auto attributes = static_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(HeapAlloc(GetProcessHeap(), 0, size));
    if (!attributes) {
        return E_OUTOFMEMORY;
    }
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &size)) {
        DWORD error = GetLastError();
        HeapFree(GetProcessHeap(), 0, attributes);
        return HRESULT_FROM_WIN32(error);
    }
    HRESULT result = S_OK;
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
        &parent, sizeof(parent), nullptr, nullptr))
        result = HRESULT_FROM_WIN32(GetLastError());
    else {
        wchar_t command[32768];
        result = StringCchPrintfW(command, ARRAYSIZE(command), L"\"%s\" %s",
            executable, arguments ? arguments : L"");
        if (SUCCEEDED(result)) {
            STARTUPINFOEXW startup{};
            startup.StartupInfo.cb = sizeof(startup);
            startup.lpAttributeList = attributes;
            BOOL created = userToken
                ? CreateProcessAsUserW(userToken, executable, command, nullptr, nullptr, FALSE,
                    EXTENDED_STARTUPINFO_PRESENT | additionalFlags, nullptr, workingDirectory, &startup.StartupInfo, child)
                : CreateProcessW(executable, command, nullptr, nullptr, FALSE,
                    EXTENDED_STARTUPINFO_PRESENT | additionalFlags, nullptr, workingDirectory, &startup.StartupInfo, child);
            if (!created)
                result = HRESULT_FROM_WIN32(GetLastError());
        }
    }
    DeleteProcThreadAttributeList(attributes);
    HeapFree(GetProcessHeap(), 0, attributes);
    return result;
}

inline HRESULT CreateChildWithParent(LPCWSTR executable, LPCWSTR arguments,
    DWORD parentPid, PROCESS_INFORMATION* child, LPCWSTR workingDirectory = nullptr, DWORD additionalFlags = 0,
    HANDLE userToken = nullptr) {
    if (!parentPid || !child) return E_INVALIDARG;
    HANDLE parent = OpenProcess(PROCESS_CREATE_PROCESS, FALSE, parentPid);
    if (!parent) return HRESULT_FROM_WIN32(GetLastError());
    HRESULT result = CreateChildWithParentHandle(executable, arguments, parent, child, workingDirectory, additionalFlags, userToken);
    CloseHandle(parent);
    return result;
}
