#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <shellapi.h>
#include <strsafe.h>
#include <cwchar>
#include <cerrno>
#include <cstring>
#include "NativeObserverTypes.h"

static bool NativeObserverTest() {
    HMODULE module = GetModuleHandleW(L"ntdll.dll");
    auto queryMemory = reinterpret_cast<QueryMemory>(GetProcAddress(module, "NtQueryVirtualMemory"));
    auto querySystem = reinterpret_cast<QuerySystem>(GetProcAddress(module, "NtQuerySystemInformation"));
    auto queryProcess = reinterpret_cast<QueryProcess>(GetProcAddress(module, "NtQueryInformationProcess"));
    auto readMemory = reinterpret_cast<ReadMemory>(GetProcAddress(module, "NtReadVirtualMemory"));
    auto writeMemory = reinterpret_cast<WriteMemory>(GetProcAddress(module, "NtWriteVirtualMemory"));
    if (!queryMemory || !querySystem || !queryProcess || !readMemory || !writeMemory) return false;
    HANDLE self = GetCurrentProcess();
    MEMORY_BASIC_INFORMATION memory{};
    PROCESS_BASIC_INFORMATION process{};
    SIZE_T returned = 0;
    ULONG size = 0;
    int source = 12345, copy = 0, replacement = 67890;
    SetLastError(0x1234);
    if (queryMemory(self, &source, 0, &memory, sizeof(memory), &returned) < 0
        || returned != sizeof(memory) || GetLastError() != 0x1234) return false;
    if (queryProcess(self, 0, &process, sizeof(process), &size) < 0
        || process.UniqueProcessId != GetCurrentProcessId()) return false;
    if (static_cast<ULONG>(querySystem(5, nullptr, 0, &size)) != 0xC0000004 || size == 0) return false;
    ULONG capacity = size + 65536;
    void* system = HeapAlloc(GetProcessHeap(), 0, capacity);
    if (!system) return false;
    LONG systemStatus = querySystem(5, system, capacity, &size);
    HeapFree(GetProcessHeap(), 0, system);
    if (systemStatus < 0) return false;
    if (readMemory(self, &source, &copy, sizeof(source), &returned) < 0 || returned != sizeof(source) || copy != source) return false;
    if (writeMemory(self, &source, &replacement, sizeof(source), &returned) < 0 || returned != sizeof(source) || source != replacement) return false;
    return GetLastError() == 0x1234;
}

static HANDLE OpenTestFile(LPCWSTR path, DWORD access, DWORD share, DWORD disposition, bool ansi) {
    if (!ansi) return CreateFileW(path, access, share, nullptr, disposition, FILE_ATTRIBUTE_NORMAL, nullptr);
    char text[32768];
    if (!WideCharToMultiByte(CP_ACP, 0, path, -1, text, ARRAYSIZE(text), nullptr, nullptr)) return INVALID_HANDLE_VALUE;
    return CreateFileA(text, access, share, nullptr, disposition, FILE_ATTRIBUTE_NORMAL, nullptr);
}

static int ObserverTest(LPCWSTR directory, bool ansi) {
    wchar_t path[32768], missing[32768];
    if (FAILED(StringCchPrintfW(path, ARRAYSIZE(path), L"%s\\observer-data.tmp", directory))
        || FAILED(StringCchPrintfW(missing, ARRAYSIZE(missing), L"%s\\observer-missing.tmp", directory))) return 20;
    HANDLE absent = OpenTestFile(missing, GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING, ansi);
    if (absent != INVALID_HANDLE_VALUE || GetLastError() != ERROR_FILE_NOT_FOUND) return 21;
    HANDLE file = OpenTestFile(path, GENERIC_READ | GENERIC_WRITE, 0, CREATE_ALWAYS, ansi);
    if (file == INVALID_HANDLE_VALUE) return 22;
    const char message[] = "ciao!";
    DWORD written = 0, read = 0;
    char buffer[sizeof(message)]{};
    bool valid = WriteFile(file, message, 5, &written, nullptr) && written == 5;
    valid = valid && SetFilePointer(file, 0, nullptr, FILE_BEGIN) != INVALID_SET_FILE_POINTER;
    valid = valid && ReadFile(file, buffer, 5, &read, nullptr) && read == 5 && std::memcmp(buffer, message, 5) == 0;
    CloseHandle(file);
    file = OpenTestFile(path, GENERIC_READ, FILE_SHARE_READ, OPEN_ALWAYS, ansi);
    valid = valid && file != INVALID_HANDLE_VALUE && GetLastError() == ERROR_ALREADY_EXISTS;
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (ansi) {
        char text[32768];
        if (WideCharToMultiByte(CP_ACP, 0, path, -1, text, ARRAYSIZE(text), nullptr, nullptr))
            valid = DeleteFileA(text) && valid;
        else valid = false;
    } else valid = DeleteFileW(path) && valid;
    valid = NativeObserverTest() && valid;
    Sleep(2000); // Consente al canale asincrono di consegnare anche gli ultimi eventi.
    return valid ? 0 : 23;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
    DWORD parentPid = 0;
    wchar_t parentName[MAX_PATH] = L"[non disponibile]";
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 1;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot, &entry)) {
        do {
            if (entry.th32ProcessID == GetCurrentProcessId()) {
                parentPid = entry.th32ParentProcessID;
                break;
            }
        } while (Process32NextW(snapshot, &entry));
    }
    if (parentPid && Process32FirstW(snapshot, &entry)) {
        do {
            if (entry.th32ProcessID == parentPid) {
                StringCchCopyW(parentName, ARRAYSIZE(parentName), entry.szExeFile);
                break;
            }
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    if (!parentPid) return 1;

    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 1;
    if (argc > 1) {
        if (argc == 3 && (std::wcscmp(argv[1], L"--observer-test") == 0 || std::wcscmp(argv[1], L"--observer-test-ansi") == 0)) {
            int result = ObserverTest(argv[2], std::wcscmp(argv[1], L"--observer-test-ansi") == 0);
            LocalFree(argv);
            return result;
        }
        bool valid = argc == 3 && std::wcscmp(argv[1], L"--verify-parent") == 0;
        wchar_t* end = nullptr;
        errno = 0;
        unsigned long expected = valid ? std::wcstoul(argv[2], &end, 10) : 0;
        valid = valid && errno == 0 && end != argv[2] && *end == L'\0' && expected == parentPid;
        LocalFree(argv);
        return valid ? 0 : 1;
    }
    LocalFree(argv);

    wchar_t directory[32768]{};
    DWORD directoryLength = GetCurrentDirectoryW(ARRAYSIZE(directory), directory);
    const wchar_t* directoryText = directoryLength && directoryLength < ARRAYSIZE(directory)
        ? directory : L"[non disponibile]";
    wchar_t message[34000];
    StringCchPrintfW(message, ARRAYSIZE(message),
        L"Sono file.exe, il programma dimostrativo.\n\n"
        L"Il mio PID: %lu\nPID del padre: %lu\nNome del padre: %s\n\n"
        L"Cartella di lavoro effettiva:\n%s\n\n"
        L"Se mi hai avviato dalla nostra voce del menu di Explorer,\n"
        L"il padre dovrebbe essere explorer.exe.\n\n"
        L"Resto un processo separato, con la mia memoria.",
        GetCurrentProcessId(), parentPid, parentName, directoryText);
    MessageBoxW(nullptr, message, L"file.exe - Processo figlio", MB_OK | MB_ICONINFORMATION);
    return 0;
}
