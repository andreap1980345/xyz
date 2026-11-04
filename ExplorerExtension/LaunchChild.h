#pragma once
#include <windows.h>
#include <strsafe.h>
#include <cwchar>
#include "ParentLaunch.h"

// token deve puntare a dati della DLL, per ricavarne il percorso effettivo.
inline HRESULT StartDemoChild(const void* token, bool verifyOnly, bool explicitParent = false) {
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
        GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(token), &module))
        return HRESULT_FROM_WIN32(GetLastError());

    wchar_t executable[32768];
    DWORD length = GetModuleFileNameW(module, executable, ARRAYSIZE(executable));
    if (!length) return HRESULT_FROM_WIN32(GetLastError());
    if (length >= ARRAYSIZE(executable)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    wchar_t* filename = std::wcsrchr(executable, L'\\');
    if (!filename) return E_FAIL;
    ++filename;
    HRESULT status = StringCchCopyW(filename, ARRAYSIZE(executable) - (filename - executable), L"file.exe");
    if (FAILED(status)) return status;

    // Percorso assoluto esplicito: nessuna ricerca di file.exe nella PATH.
    wchar_t command[32768];
    status = verifyOnly
        ? StringCchPrintfW(command, ARRAYSIZE(command), L"\"%s\" --verify-parent %lu", executable, GetCurrentProcessId())
        : StringCchPrintfW(command, ARRAYSIZE(command), L"\"%s\"", executable);
    if (FAILED(status)) return status;

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    if (explicitParent) {
        wchar_t parameters[80]{};
        if (verifyOnly) StringCchPrintfW(parameters, ARRAYSIZE(parameters), L"--verify-parent %lu", GetCurrentProcessId());
        status = CreateChildWithParent(executable, parameters, GetCurrentProcessId(), &child);
        if (FAILED(status)) return status;
    } else if (!CreateProcessW(executable, command, nullptr, nullptr, FALSE, 0,
        nullptr, nullptr, &startup, &child)) return HRESULT_FROM_WIN32(GetLastError());

    CloseHandle(child.hThread);
    status = S_OK;
    // Solo la modalita di verifica senza finestre attende la risposta della cavia.
    // L'azione normale del menu ritorna subito, lasciando Explorer disponibile.
    if (verifyOnly) {
        DWORD wait = WaitForSingleObject(child.hProcess, 10000);
        if (wait == WAIT_TIMEOUT) status = HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        else if (wait == WAIT_FAILED) status = HRESULT_FROM_WIN32(GetLastError());
        else {
            DWORD exitCode = 1;
            if (!GetExitCodeProcess(child.hProcess, &exitCode))
                status = HRESULT_FROM_WIN32(GetLastError());
            else if (exitCode != 0) status = E_FAIL;
        }
    }
    CloseHandle(child.hProcess);
    return status;
}
