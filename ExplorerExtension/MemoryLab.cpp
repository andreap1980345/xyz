#define WIN32_LEAN_AND_MEAN
#include "Demo.h"
#include <shellapi.h>
#include <shlobj.h>
#include <strsafe.h>
#include <new>
#include <commdlg.h>
#include "LaunchChild.h"

static volatile LONG moduleReferences = 0;
static volatile LONG counter = 0;

static HRESULT ChooseAndLaunchFromHost(HWND owner) {
    wchar_t executable[32768]{};
    OPENFILENAMEW dialog{};
    dialog.lStructSize = sizeof(dialog);
    dialog.hwndOwner = owner;
    dialog.lpstrFilter = L"Programmi Windows (*.exe)\0*.exe\0\0";
    dialog.lpstrFile = executable;
    dialog.nMaxFile = ARRAYSIZE(executable);
    dialog.lpstrTitle = L"Scegli il programma da avviare dalla DLL";
    dialog.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER;
    if (!GetOpenFileNameW(&dialog)) {
        DWORD error = CommDlgExtendedError();
        return error ? HRESULT_FROM_WIN32(error) : S_FALSE;
    }
    const wchar_t* extension = std::wcsrchr(executable, L'.');
    if (!extension || lstrcmpiW(extension, L".exe") != 0) return E_INVALIDARG;
    PROCESS_INFORMATION child{};
    DWORD hostPid = GetCurrentProcessId();
    HRESULT status = CreateChildWithParent(executable, L"", hostPid, &child);
    if (FAILED(status)) return status;
    DWORD childPid = child.dwProcessId;
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    wchar_t hostPath[32768]{};
    DWORD hostLength = GetModuleFileNameW(nullptr, hostPath, ARRAYSIZE(hostPath));
    const wchar_t* hostName = L"[non disponibile]";
    if (hostLength && hostLength < ARRAYSIZE(hostPath)) {
        const wchar_t* separator = std::wcsrchr(hostPath, L'\\');
        hostName = separator ? separator + 1 : hostPath;
    }
    wchar_t text[1024];
    StringCchPrintfW(text, ARRAYSIZE(text),
        L"Avvio eseguito dalla DLL nel processo ospite: %s\n\n"
        L"PID creatore: %lu\nPID padre richiesto: %lu\nPID del nuovo processo: %lu\n\n"
        L"Il padre e stato impostato con PROC_THREAD_ATTRIBUTE_PARENT_PROCESS.\n"
        L"Usa Controlla nella nostra app per verificare il nuovo PID.", hostName, hostPid, hostPid, childPid);
    MessageBoxW(owner, text, L"Laboratorio memoria - Avvio dalla DLL", MB_OK | MB_ICONINFORMATION);
    return S_OK;
}

extern "C" __declspec(dllexport) void WINAPI GetDemoState(DemoState* state) {
    if (state) {
        state->processId = GetCurrentProcessId();
        state->counter = InterlockedCompareExchange(&counter, 0, 0);
        state->counterAddress = reinterpret_cast<ULONG_PTR>(&counter);
    }
}

class MemoryMenu final : public IShellExtInit, public IContextMenu {
    volatile LONG references = 1;
public:
    MemoryMenu() { InterlockedIncrement(&moduleReferences); }
    ~MemoryMenu() { InterlockedDecrement(&moduleReferences); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IShellExtInit)
            *result = static_cast<IShellExtInit*>(this);
        else if (iid == IID_IContextMenu)
            *result = static_cast<IContextMenu*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG remaining = InterlockedDecrement(&references);
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE Initialize(PCIDLIST_ABSOLUTE, IDataObject*, HKEY) override {
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE QueryContextMenu(HMENU menu, UINT position,
        UINT firstId, UINT lastId, UINT flags) override {
        if ((flags & CMF_DEFAULTONLY) || firstId > lastId || lastId - firstId < 2)
            return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        if (!InsertMenuW(menu, position, MF_BYPOSITION | MF_STRING,
            firstId, L"Laboratorio memoria"))
            return HRESULT_FROM_WIN32(GetLastError());
        if (!InsertMenuW(menu, position + 1, MF_BYPOSITION | MF_STRING,
            firstId + 1, L"Avvia file.exe (mostra padre e figlio)")) {
            DWORD error = GetLastError();
            DeleteMenu(menu, position, MF_BYPOSITION);
            return HRESULT_FROM_WIN32(error);
        }
        if (!InsertMenuW(menu, position + 2, MF_BYPOSITION | MF_STRING,
            firstId + 2, L"Scegli exe e avvia dalla DLL (padre esplicito)")) {
            DWORD error = GetLastError();
            DeleteMenu(menu, position + 1, MF_BYPOSITION);
            DeleteMenu(menu, position, MF_BYPOSITION);
            return HRESULT_FROM_WIN32(error);
        }
        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 3);
    }
    HRESULT STDMETHODCALLTYPE InvokeCommand(CMINVOKECOMMANDINFO* info) override {
        if (!info || info->cbSize < sizeof(CMINVOKECOMMANDINFO)) return E_INVALIDARG;
        int action = -1;
        if (HIWORD(reinterpret_cast<ULONG_PTR>(info->lpVerb)) == 0)
            action = LOWORD(reinterpret_cast<ULONG_PTR>(info->lpVerb));
        else if (info->cbSize >= sizeof(CMINVOKECOMMANDINFOEX)
            && (info->fMask & CMIC_MASK_UNICODE)) {
            auto extended = reinterpret_cast<CMINVOKECOMMANDINFOEX*>(info);
            if (extended->lpVerbW) {
                if (lstrcmpiW(extended->lpVerbW, L"memorylab") == 0) action = 0;
                if (lstrcmpiW(extended->lpVerbW, L"memorylab.launch") == 0) action = 1;
                if (lstrcmpiW(extended->lpVerbW, L"memorylab.choose") == 0) action = 2;
            }
        } else {
            if (lstrcmpiA(info->lpVerb, "memorylab") == 0) action = 0;
            if (lstrcmpiA(info->lpVerb, "memorylab.launch") == 0) action = 1;
            if (lstrcmpiA(info->lpVerb, "memorylab.choose") == 0) action = 2;
        }
        if (action < 0 || action > 2) return E_INVALIDARG;
        if (action == 2) {
            bool noUi = (info->fMask & CMIC_MASK_FLAG_NO_UI) != 0;
            HRESULT status = noUi ? StartDemoChild(const_cast<const LONG*>(&counter), true, true)
                : ChooseAndLaunchFromHost(info->hwnd);
            if (FAILED(status) && !noUi) {
                wchar_t error[128];
                StringCchPrintfW(error, ARRAYSIZE(error), L"Avvio non riuscito (HRESULT 0x%08lX).", static_cast<unsigned long>(status));
                MessageBoxW(info->hwnd, error, L"Laboratorio memoria", MB_OK | MB_ICONERROR);
            }
            return status;
        }
        if (action == 1) {
            HRESULT status = StartDemoChild(const_cast<const LONG*>(&counter),
                (info->fMask & CMIC_MASK_FLAG_NO_UI) != 0);
            if (FAILED(status) && !(info->fMask & CMIC_MASK_FLAG_NO_UI)) {
                wchar_t text[256];
                StringCchPrintfW(text, ARRAYSIZE(text),
                    L"Impossibile avviare file.exe (HRESULT 0x%08lX).\n"
                    L"Il programma deve trovarsi nella stessa cartella della DLL.",
                    static_cast<unsigned long>(status));
                MessageBoxW(info->hwnd, text, L"Laboratorio memoria", MB_OK | MB_ICONERROR);
            }
            return status;
        }

        LONG value = InterlockedIncrement(&counter);
        // Il test automatico richiede esplicitamente l'esecuzione senza interfaccia.
        if (!(info->fMask & CMIC_MASK_FLAG_NO_UI)) {
            wchar_t text[512];
            StringCchPrintfW(text, ARRAYSIZE(text),
                L"DLL caricata nel processo PID %lu\n\n"
                L"Contatore della DLL: %ld\nIndirizzo del contatore: 0x%016llX\n\n"
                L"Ogni clic incrementa questo valore nella memoria del processo.\n"
                L"Puoi cercare la regione che contiene questo indirizzo nel nostro esploratore.",
                GetCurrentProcessId(), value,
                static_cast<unsigned long long>(reinterpret_cast<ULONG_PTR>(&counter)));
            MessageBoxW(info->hwnd, text, L"Laboratorio memoria - DLL", MB_OK | MB_ICONINFORMATION);
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetCommandString(UINT_PTR id, UINT flags, UINT*,
        LPSTR buffer, UINT capacity) override {
        if (id > 2) return E_INVALIDARG;
        const char* verbs[] = { "memorylab", "memorylab.launch", "memorylab.choose" };
        const wchar_t* wideVerbs[] = { L"memorylab", L"memorylab.launch", L"memorylab.choose" };
        const char* help[] = { "Mostra PID e contatore della DLL", "Avvia il programma figlio dimostrativo", "Scegli un exe e crealo dalla DLL con padre esplicito" };
        const wchar_t* wideHelp[] = { L"Mostra PID e contatore della DLL", L"Avvia il programma figlio dimostrativo", L"Scegli un exe e crealo dalla DLL con padre esplicito" };
        switch (flags) {
        case GCS_VERBA: return StringCchCopyA(buffer, capacity, verbs[id]);
        case GCS_VERBW: return StringCchCopyW(reinterpret_cast<LPWSTR>(buffer), capacity, wideVerbs[id]);
        case GCS_HELPTEXTA: return StringCchCopyA(buffer, capacity, help[id]);
        case GCS_HELPTEXTW: return StringCchCopyW(reinterpret_cast<LPWSTR>(buffer), capacity, wideHelp[id]);
        case GCS_VALIDATEA:
        case GCS_VALIDATEW: return S_OK;
        default: return E_NOTIMPL;
        }
    }
};

class Factory final : public IClassFactory {
    volatile LONG references = 1;
public:
    Factory() { InterlockedIncrement(&moduleReferences); }
    ~Factory() { InterlockedDecrement(&moduleReferences); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE;
        *result = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG remaining = InterlockedDecrement(&references);
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto menu = new (std::nothrow) MemoryMenu();
        if (!menu) return E_OUTOFMEMORY;
        HRESULT status = menu->QueryInterface(iid, result);
        menu->Release();
        return status;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock) InterlockedIncrement(&moduleReferences);
        else InterlockedDecrement(&moduleReferences);
        return S_OK;
    }
};

STDAPI DllGetClassObject(REFCLSID clsid,
    REFIID iid, void** result) {
    if (!result) return E_POINTER;
    *result = nullptr;
    if (clsid != CLSID_MemoryLab) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    HRESULT status = factory->QueryInterface(iid, result);
    factory->Release();
    return status;
}

STDAPI DllCanUnloadNow() {
    return InterlockedCompareExchange(&moduleReferences, 0, 0) == 0 ? S_OK : S_FALSE;
}
