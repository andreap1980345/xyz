// Metadata only: no memory contents are copied to the observer.
static QueryMemory RealQueryMemory = nullptr;
static QuerySystem RealQuerySystem = nullptr;
static QueryProcess RealQueryProcess = nullptr;
static ReadMemory RealReadMemory = nullptr;
static WriteMemory RealWriteMemory = nullptr;

template<class T> static bool ReadOutput(const T* source, T& value) {
    if (!source) return false;
    __try { value = *source; return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static ObserverEvent NativeEvent(DWORD kind, HANDLE process, LONG status, PVOID address,
    PVOID buffer, SIZE_T length, ULONG infoClass) {
    auto event = MakeEvent(kind, process, status >= 0, 0);
    event.error = static_cast<DWORD>(status); // NTSTATUS, never GetLastError.
    event.address = reinterpret_cast<ULONG_PTR>(address);
    event.buffer = reinterpret_cast<ULONG_PTR>(buffer);
    event.requested = length;
    event.access = infoClass;
    event.disposition = sizeof(void*) * 8;
    return event;
}

static LONG NTAPI ObservedQueryMemory(HANDLE process, PVOID address, ULONG infoClass,
    PVOID buffer, SIZE_T length, PSIZE_T returned) {
    if (insideObserver) return RealQueryMemory(process, address, infoClass, buffer, length, returned);
    insideObserver = true;
    LONG status = RealQueryMemory(process, address, infoClass, buffer, length, returned);
    DWORD lastError = GetLastError();
    auto event = NativeEvent(8, process, status, address, buffer, length, infoClass);
    SIZE_T count = 0;
    if (ReadOutput(returned, count)) { event.transferred = count; event.flags |= 2; }
    MEMORY_BASIC_INFORMATION info{};
    if (status >= 0 && infoClass == 0 && length >= sizeof(info) && ReadOutput(static_cast<MEMORY_BASIC_INFORMATION*>(buffer), info))
        StringCchPrintfW(event.path, ARRAYSIZE(event.path),
            L"Base=0x%llX; AllocationBase=0x%llX; RegionSize=%llu; State=0x%lX; Protect=0x%lX; Type=0x%lX",
            static_cast<ULONGLONG>(reinterpret_cast<ULONG_PTR>(info.BaseAddress)),
            static_cast<ULONGLONG>(reinterpret_cast<ULONG_PTR>(info.AllocationBase)),
            static_cast<ULONGLONG>(info.RegionSize), info.State, info.Protect, info.Type);
    Enqueue(event); insideObserver = false; SetLastError(lastError); return status;
}

static LONG NTAPI ObservedQuerySystem(ULONG infoClass, PVOID buffer, ULONG length, PULONG returned) {
    if (insideObserver) return RealQuerySystem(infoClass, buffer, length, returned);
    insideObserver = true;
    LONG status = RealQuerySystem(infoClass, buffer, length, returned);
    DWORD lastError = GetLastError();
    auto event = NativeEvent(9, nullptr, status, nullptr, buffer, length, infoClass);
    ULONG count = 0;
    if (ReadOutput(returned, count)) { event.transferred = count; event.flags |= 2; }
    Enqueue(event); insideObserver = false; SetLastError(lastError); return status;
}

static LONG NTAPI ObservedQueryProcess(HANDLE process, ULONG infoClass, PVOID buffer, ULONG length, PULONG returned) {
    if (insideObserver) return RealQueryProcess(process, infoClass, buffer, length, returned);
    insideObserver = true;
    LONG status = RealQueryProcess(process, infoClass, buffer, length, returned);
    DWORD lastError = GetLastError();
    auto event = NativeEvent(10, process, status, nullptr, buffer, length, infoClass);
    ULONG count = 0;
    if (ReadOutput(returned, count)) { event.transferred = count; event.flags |= 2; }
    PROCESS_BASIC_INFORMATION info{};
    if (status >= 0 && infoClass == 0 && length >= sizeof(info) && ReadOutput(static_cast<PROCESS_BASIC_INFORMATION*>(buffer), info))
        StringCchPrintfW(event.path, ARRAYSIZE(event.path), L"PID=%llu; PEB=0x%llX",
            static_cast<ULONGLONG>(info.UniqueProcessId), static_cast<ULONGLONG>(reinterpret_cast<ULONG_PTR>(info.PebBaseAddress)));
    Enqueue(event); insideObserver = false; SetLastError(lastError); return status;
}

static LONG NTAPI ObservedReadMemory(HANDLE process, PVOID address, PVOID buffer, SIZE_T length, PSIZE_T returned) {
    if (insideObserver) return RealReadMemory(process, address, buffer, length, returned);
    insideObserver = true;
    LONG status = RealReadMemory(process, address, buffer, length, returned);
    DWORD lastError = GetLastError();
    auto event = NativeEvent(11, process, status, address, buffer, length, 0);
    SIZE_T count = 0;
    if (ReadOutput(returned, count)) { event.transferred = count; event.flags |= 2; }
    Enqueue(event); insideObserver = false; SetLastError(lastError); return status;
}

static LONG NTAPI ObservedWriteMemory(HANDLE process, PVOID address, const VOID* buffer, SIZE_T length, PSIZE_T returned) {
    if (insideObserver) return RealWriteMemory(process, address, buffer, length, returned);
    insideObserver = true;
    LONG status = RealWriteMemory(process, address, buffer, length, returned);
    DWORD lastError = GetLastError();
    auto event = NativeEvent(12, process, status, address, const_cast<PVOID>(buffer), length, 0);
    SIZE_T count = 0;
    if (ReadOutput(returned, count)) { event.transferred = count; event.flags |= 2; }
    Enqueue(event); insideObserver = false; SetLastError(lastError); return status;
}

static bool ResolveNativeObservers() {
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    RealQueryMemory = reinterpret_cast<QueryMemory>(GetProcAddress(ntdll, "NtQueryVirtualMemory"));
    RealQuerySystem = reinterpret_cast<QuerySystem>(GetProcAddress(ntdll, "NtQuerySystemInformation"));
    RealQueryProcess = reinterpret_cast<QueryProcess>(GetProcAddress(ntdll, "NtQueryInformationProcess"));
    RealReadMemory = reinterpret_cast<ReadMemory>(GetProcAddress(ntdll, "NtReadVirtualMemory"));
    RealWriteMemory = reinterpret_cast<WriteMemory>(GetProcAddress(ntdll, "NtWriteVirtualMemory"));
    return RealQueryMemory && RealQuerySystem && RealQueryProcess && RealReadMemory && RealWriteMemory;
}

static LONG AttachNativeObservers() {
    LONG status = DetourAttach(&(PVOID&)RealQueryMemory, ObservedQueryMemory);
    if (!status) status = DetourAttach(&(PVOID&)RealQuerySystem, ObservedQuerySystem);
    if (!status) status = DetourAttach(&(PVOID&)RealQueryProcess, ObservedQueryProcess);
    if (!status) status = DetourAttach(&(PVOID&)RealReadMemory, ObservedReadMemory);
    if (!status) status = DetourAttach(&(PVOID&)RealWriteMemory, ObservedWriteMemory);
    return status;
}
