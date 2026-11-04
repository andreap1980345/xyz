#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <detours.h>
#include <strsafe.h>
#include <cwchar>
#include <cstdlib>
#include "ObserverProtocol.h"

static HRESULT SiblingPath(LPCWSTR name, wchar_t* path, size_t capacity) {
    static const int token = 0;
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
        GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&token), &module))
        return HRESULT_FROM_WIN32(GetLastError());
    DWORD length = GetModuleFileNameW(module, path, static_cast<DWORD>(capacity));
    if (!length || length >= capacity) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    wchar_t* filename = std::wcsrchr(path, L'\\');
    if (!filename) return E_FAIL;
    ++filename;
    return StringCchCopyW(filename, capacity - (filename - path), name);
}

static HRESULT PrepareMatchingArchitecture(HANDLE process, LPCWSTR pipeName, DWORD receiverPid, DWORD features) {
    wchar_t filename[32768];
#ifdef _WIN64
    HRESULT status = SiblingPath(L"ApiObserver64.dll", filename, ARRAYSIZE(filename));
#else
    HRESULT status = SiblingPath(L"ApiObserver32.dll", filename, ARRAYSIZE(filename));
#endif
    if (FAILED(status)) return status;
    char ansi[32768];
    BOOL substituted = FALSE;
    if (!WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, filename, -1, ansi, ARRAYSIZE(ansi), nullptr, &substituted)
        || substituted) return HRESULT_FROM_WIN32(ERROR_NO_UNICODE_TRANSLATION);
    if (GetFileAttributesW(filename) == INVALID_FILE_ATTRIBUTES) return HRESULT_FROM_WIN32(GetLastError());
    ObserverConfig configuration{};
    configuration.magic = ObserverMagic;
    configuration.receiverPid = receiverPid;
    configuration.features = features;
    status = StringCchCopyW(configuration.pipeName, ARRAYSIZE(configuration.pipeName), pipeName);
    if (FAILED(status)) return status;
    if (!DetourCopyPayloadToProcess(process, ObserverConfigId, &configuration, sizeof(configuration)))
        return HRESULT_FROM_WIN32(GetLastError());
    LPCSTR dll = ansi;
    if (!DetourUpdateProcessWithDll(process, &dll, 1)) return HRESULT_FROM_WIN32(GetLastError());
    return S_OK;
}

#ifdef OBSERVER_SETUP_EXE
int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc != 5) { if (argv) LocalFree(argv); return static_cast<int>(E_INVALIDARG); }
    wchar_t* handleEnd = nullptr;
    wchar_t* pidEnd = nullptr;
    unsigned long long number = std::wcstoull(argv[1], &handleEnd, 10);
    unsigned long pid = std::wcstoul(argv[3], &pidEnd, 10);
    wchar_t* featuresEnd = nullptr;
    unsigned long features = std::wcstoul(argv[4], &featuresEnd, 10);
    HRESULT status = E_INVALIDARG;
    if (number && pid && handleEnd != argv[1] && !*handleEnd && pidEnd != argv[3] && !*pidEnd
        && featuresEnd != argv[4] && !*featuresEnd && (features == 1 || features == 3)) {
        HANDLE process = reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(number));
        status = PrepareMatchingArchitecture(process, argv[2], pid, features);
        CloseHandle(process);
    }
    LocalFree(argv);
    return static_cast<int>(status);
}
#else
static HRESULT Prepare32Bit(HANDLE process, LPCWSTR pipeName, DWORD receiverPid, DWORD features) {
    wchar_t helper[32768], command[32768];
    HRESULT status = SiblingPath(L"ObserverSetup32.exe", helper, ARRAYSIZE(helper));
    if (FAILED(status)) return status;
    HANDLE inherited = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), process, GetCurrentProcess(), &inherited, 0, TRUE, DUPLICATE_SAME_ACCESS))
        return HRESULT_FROM_WIN32(GetLastError());
    status = StringCchPrintfW(command, ARRAYSIZE(command), L"\"%s\" %llu \"%s\" %lu %lu",
        helper, static_cast<unsigned long long>(reinterpret_cast<ULONG_PTR>(inherited)), pipeName, receiverPid, features);
    if (FAILED(status)) { CloseHandle(inherited); return status; }
    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
    auto attributes = static_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(HeapAlloc(GetProcessHeap(), 0, bytes));
    if (!attributes) { CloseHandle(inherited); return E_OUTOFMEMORY; }
    bool initialized = InitializeProcThreadAttributeList(attributes, 1, 0, &bytes) != FALSE;
    PROCESS_INFORMATION helperProcess{};
    if (!initialized) status = HRESULT_FROM_WIN32(GetLastError());
    else if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
        &inherited, sizeof(inherited), nullptr, nullptr)) status = HRESULT_FROM_WIN32(GetLastError());
    else {
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.lpAttributeList = attributes;
        if (!CreateProcessW(helper, command, nullptr, nullptr, TRUE,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW, nullptr, nullptr, &startup.StartupInfo, &helperProcess))
            status = HRESULT_FROM_WIN32(GetLastError());
    }
    if (initialized) DeleteProcThreadAttributeList(attributes);
    HeapFree(GetProcessHeap(), 0, attributes);
    CloseHandle(inherited);
    if (FAILED(status)) return status;
    CloseHandle(helperProcess.hThread);
    DWORD wait = WaitForSingleObject(helperProcess.hProcess, 10000);
    if (wait == WAIT_TIMEOUT) {
        TerminateProcess(helperProcess.hProcess, ERROR_TIMEOUT);
        WaitForSingleObject(helperProcess.hProcess, 1000);
        status = HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    } else if (wait != WAIT_OBJECT_0) status = HRESULT_FROM_WIN32(GetLastError());
    else {
        DWORD exitCode = 0;
        if (!GetExitCodeProcess(helperProcess.hProcess, &exitCode)) status = HRESULT_FROM_WIN32(GetLastError());
        else status = static_cast<HRESULT>(exitCode);
    }
    CloseHandle(helperProcess.hProcess);
    return status;
}

extern "C" __declspec(dllexport) HRESULT WINAPI PrepareFileObserver(HANDLE process, LPCWSTR pipeName, DWORD receiverPid, DWORD features) {
    if (!process || !pipeName || !*pipeName || !receiverPid || (features != 1 && features != 3)) return E_INVALIDARG;
    USHORT machine = 0, nativeMachine = 0;
    if (!IsWow64Process2(process, &machine, &nativeMachine)) return HRESULT_FROM_WIN32(GetLastError());
    if (machine == IMAGE_FILE_MACHINE_I386) return Prepare32Bit(process, pipeName, receiverPid, features);
    if (machine != IMAGE_FILE_MACHINE_UNKNOWN || nativeMachine != IMAGE_FILE_MACHINE_AMD64)
        return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
    return PrepareMatchingArchitecture(process, pipeName, receiverPid, features);
}
#endif
