#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <detours.h>
#include <strsafe.h>
#include "ObserverProtocol.h"
#include "NativeObserverTypes.h"

// --- (x64 / x86 Min Structure definitions) ---
#pragma pack(push, 1)
typedef struct _UNICODE_STRING_MIN {
    USHORT Length;
    USHORT MaximumLength;
    PWSTR  Buffer;
} UNICODE_STRING_MIN;

typedef struct _LDR_DATA_TABLE_ENTRY_MIN {
    LIST_ENTRY       InLoadOrderLinks;
    LIST_ENTRY       InMemoryOrderLinks;
    LIST_ENTRY       InInitializationOrderLinks;
    PVOID            DllBase;
    PVOID            EntryPoint;
    ULONG            SizeOfImage;
    UNICODE_STRING_MIN FullDllName;
    UNICODE_STRING_MIN BaseDllName;
} LDR_DATA_TABLE_ENTRY_MIN, *PLDR_DATA_TABLE_ENTRY_MIN;

typedef struct _PEB_LDR_DATA_MIN {
    ULONG              Length;
    ULONG              Initialized;
    PVOID              SsHandle;
    LIST_ENTRY         InLoadOrderModuleList;
    LIST_ENTRY         InMemoryOrderModuleList;
    LIST_ENTRY         InInitializationOrderModuleList;
} PEB_LDR_DATA_MIN, *PPEB_LDR_DATA_MIN;
#pragma pack(pop)

static PPEB_LDR_DATA_MIN GetLdrData() {
#if defined(_M_X64)
    PPEB_LDR_DATA_MIN ldr = *(PPEB_LDR_DATA_MIN*)(
        *(ULONG_PTR*)__readgsqword(0x60) + 0x18);
#elif defined(_M_IX86)
    PPEB_LDR_DATA_MIN ldr = *(PPEB_LDR_DATA_MIN*)(
        *(ULONG_PTR*)__readfsdword(0x30) + 0x0C);
#endif
    return ldr;
}

// Funzione per rimuovere la DLL dalle liste della PEB (richiamata in DllMain per evitare il warning C4505)
static void UnlinkSelf(HMODULE ownModule) {
    PPEB_LDR_DATA_MIN ldr = GetLdrData();
    if (!ldr || !ownModule) return;

    PLIST_ENTRY heads[] = {
        &ldr->InLoadOrderModuleList,
        &ldr->InMemoryOrderModuleList,
        &ldr->InInitializationOrderModuleList
    };

    for (int i = 0; i < 3; ++i) {
        PLIST_ENTRY current = heads[i]->Flink;
        while (current != heads[i]) {
            PLDR_DATA_TABLE_ENTRY_MIN entry = CONTAINING_RECORD(
                current, LDR_DATA_TABLE_ENTRY_MIN, InLoadOrderLinks);

            if (entry->DllBase == ownModule) {
                current->Blink->Flink = current->Flink;
                current->Flink->Blink = current->Blink;
                break;
            }
            current = current->Flink;
        }
    }
}

// Puntatori originali delle API
static decltype(&CreateFileW) RealCreateFile = CreateFileW;
static decltype(&CreateFileA) RealCreateFileA = CreateFileA;
static decltype(&ReadFile) RealReadFile = ReadFile;
static decltype(&WriteFile) RealWriteFile = WriteFile;
static decltype(&CloseHandle) RealCloseHandle = CloseHandle;
static decltype(&DeleteFileW) RealDeleteFile = DeleteFileW;
static decltype(&DeleteFileA) RealDeleteFileA = DeleteFileA;

// Sincronizzazione e Stato
static thread_local bool insideObserver = false;
class ObserverGuard {
    bool& flag;
public:
    ObserverGuard(bool& f) : flag(f) { flag = true; }
    ~ObserverGuard() { flag = false; }
};

static SRWLOCK queueLock = SRWLOCK_INIT;
static ObserverEvent events[256];
static unsigned head = 0, tail = 0;
static volatile LONG dropped = 0;
static __declspec(align(8)) volatile LONG64 sequence = 0;
static HANDLE wake = nullptr;
static ObserverConfig configuration{};
static HMODULE ownModule = nullptr;
static volatile LONG workerStarted = 0;

extern "C" void WINAPI ObserverAnchor() { }

static void CopyPath(wchar_t* destination, LPCWSTR source) {
    if (!source) return;
    __try {
        for (unsigned i = 0; i < 511; ++i) {
            destination[i] = source[i];
            if (!source[i]) return;
        }
        destination[511] = 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        StringCchCopyW(destination, 512, L"[percorso non leggibile]");
    }
}

static DWORD ByteCount(const DWORD* value) {
    if (!value) return 0;
    __try { return *value; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

static void CopyAnsiPath(wchar_t* destination, LPCSTR source) {
    if (!source) return;
    char text[2048]{};
    __try {
        for (unsigned i = 0; i < ARRAYSIZE(text) - 1; ++i) {
            text[i] = source[i];
            if (!source[i]) break;
        }
        if (!MultiByteToWideChar(CP_ACP, 0, text, -1, destination, 512))
            StringCchCopyW(destination, 512, L"[percorso ANSI non disponibile]");
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        StringCchCopyW(destination, 512, L"[percorso non leggibile]");
    }
}

static ObserverEvent MakeEvent(DWORD kind, HANDLE handle, BOOL success, DWORD error) {
    ObserverEvent event{};
    event.magic = ObserverMagic;
    event.size = sizeof(event);
    event.kind = kind;
    event.pid = GetCurrentProcessId();
    event.tid = GetCurrentThreadId();
    event.success = success != FALSE;
    event.error = success ? 0 : error;
    event.handle = reinterpret_cast<ULONG_PTR>(handle);
    event.sequence = static_cast<ULONGLONG>(InterlockedIncrement64(&sequence));
    FILETIME now{};
    GetSystemTimePreciseAsFileTime(&now);
    event.fileTime = (static_cast<ULONGLONG>(now.dwHighDateTime) << 32) | now.dwLowDateTime;
    return event;
}

static void PathForHandle(HANDLE handle, wchar_t* destination) {
    if (handle && handle != INVALID_HANDLE_VALUE)
        if (GetFinalPathNameByHandleW(handle, destination, 512, FILE_NAME_NORMALIZED) >= 512)
            StringCchCopyW(destination, 512, L"[percorso oltre 511 caratteri]");
}

static void Enqueue(ObserverEvent& event) {
    if (!TryAcquireSRWLockExclusive(&queueLock)) { InterlockedIncrement(&dropped); return; }
    unsigned next = (head + 1) % ARRAYSIZE(events);
    if (next == tail) InterlockedIncrement(&dropped);
    else {
        event.dropped = static_cast<DWORD>(InterlockedCompareExchange(&dropped, 0, 0));
        events[head] = event;
        head = next;
    }
    ReleaseSRWLockExclusive(&queueLock);
    if (wake) SetEvent(wake);
}

// Forward declaration del Worker Thread
static DWORD WINAPI SendEvents(void*);

static void EnsureWorkerStarted() {
    if (InterlockedCompareExchange(&workerStarted, 1, 0) == 0) {
        HANDLE worker = CreateThread(nullptr, 0, SendEvents, nullptr, 0, nullptr);
        if (worker) RealCloseHandle(worker);
    }
}

// --- Wrapper Hook delle API ---
static HANDLE WINAPI ObservedCreateFile(LPCWSTR path, DWORD access, DWORD share,
    LPSECURITY_ATTRIBUTES security, DWORD disposition, DWORD attributes, HANDLE templateFile) {
    EnsureWorkerStarted();
    if (insideObserver) return RealCreateFile(path, access, share, security, disposition, attributes, templateFile);
    ObserverGuard guard(insideObserver);
    
    HANDLE result = RealCreateFile(path, access, share, security, disposition, attributes, templateFile);
    DWORD error = GetLastError();
    auto event = MakeEvent(1, result, result != INVALID_HANDLE_VALUE, error);
    event.access = access;
    event.disposition = disposition;
    if (result != INVALID_HANDLE_VALUE && (disposition == OPEN_ALWAYS || disposition == CREATE_ALWAYS))
        event.error = error;
    CopyPath(event.path, path);
    Enqueue(event);
    SetLastError(error);
    return result;
}

static BOOL WINAPI ObservedReadFile(HANDLE file, LPVOID buffer, DWORD requested,
    LPDWORD transferred, LPOVERLAPPED overlapped) {
    if (insideObserver) return RealReadFile(file, buffer, requested, transferred, overlapped);
    ObserverGuard guard(insideObserver);

    BOOL result = RealReadFile(file, buffer, requested, transferred, overlapped);
    DWORD error = GetLastError();
    auto event = MakeEvent(2, file, result, error);
    event.requested = requested;
    event.flags = overlapped ? 1u : result && transferred ? 2u : 0u;
    if (result && !overlapped) event.transferred = ByteCount(transferred);
    PathForHandle(file, event.path);
    Enqueue(event);
    SetLastError(error);
    return result;
}

static HANDLE WINAPI ObservedCreateFileA(LPCSTR path, DWORD access, DWORD share,
    LPSECURITY_ATTRIBUTES security, DWORD disposition, DWORD attributes, HANDLE templateFile) {
    EnsureWorkerStarted();
    if (insideObserver) return RealCreateFileA(path, access, share, security, disposition, attributes, templateFile);
    ObserverGuard guard(insideObserver);

    HANDLE result = RealCreateFileA(path, access, share, security, disposition, attributes, templateFile);
    DWORD error = GetLastError();
    auto event = MakeEvent(6, result, result != INVALID_HANDLE_VALUE, error);
    event.access = access;
    event.disposition = disposition;
    if (result != INVALID_HANDLE_VALUE && (disposition == OPEN_ALWAYS || disposition == CREATE_ALWAYS)) event.error = error;
    CopyAnsiPath(event.path, path);
    Enqueue(event);
    SetLastError(error);
    return result;
}

static BOOL WINAPI ObservedWriteFile(HANDLE file, LPCVOID buffer, DWORD requested,
    LPDWORD transferred, LPOVERLAPPED overlapped) {
    if (insideObserver) return RealWriteFile(file, buffer, requested, transferred, overlapped);
    ObserverGuard guard(insideObserver);

    BOOL result = RealWriteFile(file, buffer, requested, transferred, overlapped);
    DWORD error = GetLastError();
    auto event = MakeEvent(3, file, result, error);
    event.requested = requested;
    event.flags = overlapped ? 1u : result && transferred ? 2u : 0u;
    if (result && !overlapped) event.transferred = ByteCount(transferred);
    PathForHandle(file, event.path);
    Enqueue(event);
    SetLastError(error);
    return result;
}

static BOOL WINAPI ObservedCloseHandle(HANDLE handle) {
    if (insideObserver) return RealCloseHandle(handle);
    ObserverGuard guard(insideObserver);

    DWORD previousError = GetLastError();
    bool disk = GetFileType(handle) == FILE_TYPE_DISK;
    ObserverEvent event{};
    if (disk) { event = MakeEvent(4, handle, TRUE, 0); PathForHandle(handle, event.path); }
    SetLastError(previousError);
    BOOL result = RealCloseHandle(handle);
    DWORD error = GetLastError();
    if (disk) { event.success = result != FALSE; event.error = result ? 0 : error; Enqueue(event); }
    SetLastError(error);
    return result;
}

static BOOL WINAPI ObservedDeleteFile(LPCWSTR path) {
    if (insideObserver) return RealDeleteFile(path);
    ObserverGuard guard(insideObserver);

    BOOL result = RealDeleteFile(path);
    DWORD error = GetLastError();
    auto event = MakeEvent(5, nullptr, result, error);
    CopyPath(event.path, path);
    Enqueue(event);
    SetLastError(error);
    return result;
}

static BOOL WINAPI ObservedDeleteFileA(LPCSTR path) {
    if (insideObserver) return RealDeleteFileA(path);
    ObserverGuard guard(insideObserver);

    BOOL result = RealDeleteFileA(path);
    DWORD error = GetLastError();
    auto event = MakeEvent(7, nullptr, result, error);
    CopyAnsiPath(event.path, path);
    Enqueue(event);
    SetLastError(error);
    return result;
}

// Worker Thread sicuro per la Pipe
static DWORD WINAPI SendEvents(void*) {
    HMODULE pinned = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(ownModule), &pinned);

    wchar_t pipePath[256];
    StringCchPrintfW(pipePath, ARRAYSIZE(pipePath), L"\\\\.\\pipe\\%s", configuration.pipeName);
    
    HANDLE pipe = INVALID_HANDLE_VALUE;
    for (int attempt = 0; attempt < 40; ++attempt) {
        pipe = RealCreateFile(pipePath, GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) break;
        Sleep(100);
    }
    if (pipe == INVALID_HANDLE_VALUE) return 1;

    ULONG serverPid = 0;
    if (!GetNamedPipeServerProcessId(pipe, &serverPid) || serverPid != configuration.receiverPid) {
        RealCloseHandle(pipe);
        return 1;
    }

    for (;;) {
        WaitForSingleObject(wake, 250);
        for (;;) {
            ObserverEvent event{};
            AcquireSRWLockExclusive(&queueLock);
            bool available = tail != head;
            if (available) { event = events[tail]; tail = (tail + 1) % ARRAYSIZE(events); }
            ReleaseSRWLockExclusive(&queueLock);
            if (!available) break;

            event.dropped = static_cast<DWORD>(InterlockedCompareExchange(&dropped, 0, 0));
            DWORD written = 0;
            if (!RealWriteFile(pipe, &event, sizeof(event), &written, nullptr) || written != sizeof(event)) {
                RealCloseHandle(pipe);
                return 1;
            }
        }
    }
}

#include "NativeObserver.inl"

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (DetourIsHelperProcess()) return TRUE;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    
    ownModule = instance;
    
    // Esempio di utilizzo della funzione UnlinkSelf per evitare il warning C4505
    UnlinkSelf(ownModule);

    DetourRestoreAfterWith();

    DWORD size = 0;
    auto config = static_cast<ObserverConfig*>(DetourFindPayloadEx(ObserverConfigId, &size));
    if (!config || size != sizeof(ObserverConfig) || config->magic != ObserverMagic) return FALSE;
    configuration = *config;
    configuration.pipeName[ARRAYSIZE(configuration.pipeName) - 1] = 0;

    wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!wake) return FALSE;

    if (configuration.features != 1 && configuration.features != 3) return FALSE;
    if ((configuration.features & 2) && !ResolveNativeObservers()) return FALSE;

    LONG status = DetourTransactionBegin();
    if (status == NO_ERROR) status = DetourUpdateThread(GetCurrentThread());
    if (status == NO_ERROR) status = DetourAttach(&(PVOID&)RealCreateFile, ObservedCreateFile);
    if (status == NO_ERROR) status = DetourAttach(&(PVOID&)RealCreateFileA, ObservedCreateFileA);
    if (status == NO_ERROR) status = DetourAttach(&(PVOID&)RealReadFile, ObservedReadFile);
    if (status == NO_ERROR) status = DetourAttach(&(PVOID&)RealWriteFile, ObservedWriteFile);
    if (status == NO_ERROR) status = DetourAttach(&(PVOID&)RealCloseHandle, ObservedCloseHandle);
    if (status == NO_ERROR) status = DetourAttach(&(PVOID&)RealDeleteFile, ObservedDeleteFile);
    if (status == NO_ERROR) status = DetourAttach(&(PVOID&)RealDeleteFileA, ObservedDeleteFileA);
    if (status == NO_ERROR && (configuration.features & 2)) status = AttachNativeObservers();
    
    if (status == NO_ERROR) status = DetourTransactionCommit();
    else DetourTransactionAbort();

    if (status != NO_ERROR) return FALSE;

    auto ready = MakeEvent(0, nullptr, TRUE, 0);
    ready.disposition = sizeof(void*) * 8;
    ready.access = configuration.features;
    StringCchCopyW(ready.path, ARRAYSIZE(ready.path), (configuration.features & 2) ? L"Hook file e Native API attivi" : L"Hook solo file attivi");
    Enqueue(ready);

    return TRUE;
}