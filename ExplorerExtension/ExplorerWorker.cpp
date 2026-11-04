#define WIN32_LEAN_AND_MEAN
#include "InjectionProtocol.h"
#include <cwchar>

// Eseguita esplicitamente dopo il caricamento; nessun lavoro da DllMain.
extern "C" __declspec(dllexport) DWORD WINAPI AcquireParentHandle(void* parameter) {
    auto request = static_cast<ParentHandleRequest*>(parameter);
    if (!request || request->magic != RequestMagic) return ERROR_INVALID_PARAMETER;
    request->handle = 0;
    request->error = ERROR_ACCESS_DENIED;
    request->phase = 10;
    if (request->explorerPid != GetCurrentProcessId()) return request->error;
    wchar_t image[32768];
    DWORD length = GetModuleFileNameW(nullptr, image, ARRAYSIZE(image));
    if (!length || length >= ARRAYSIZE(image)) return request->error;
    const wchar_t* name = std::wcsrchr(image, L'\\');
    if (!name || lstrcmpiW(name + 1, L"explorer.exe") != 0) return request->error;

    request->phase = 11;
    HANDLE receiver = OpenProcess(PROCESS_DUP_HANDLE, FALSE, request->receiverPid);
    if (!receiver) return request->error = GetLastError();
    request->phase = 12;
    HANDLE parent = OpenProcess(PROCESS_CREATE_PROCESS | PROCESS_QUERY_LIMITED_INFORMATION,
        FALSE, GetCurrentProcessId());
    if (!parent) {
        request->error = GetLastError();
        CloseHandle(receiver);
        return request->error;
    }
    HANDLE duplicate = nullptr;
    request->phase = 13;
    if (!DuplicateHandle(GetCurrentProcess(), parent, receiver, &duplicate,
        PROCESS_CREATE_PROCESS | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, 0))
        request->error = GetLastError();
    else {
        request->handle = reinterpret_cast<ULONG_PTR>(duplicate);
        request->error = ERROR_SUCCESS;
    }
    CloseHandle(parent);
    CloseHandle(receiver);
    return request->error;
}
