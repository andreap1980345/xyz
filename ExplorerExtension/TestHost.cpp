#define WIN32_LEAN_AND_MEAN
#include "Demo.h"
#include <shellapi.h>
#include <shlobj.h>
#include <cstdio>

static void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ExitProcess(1);
    }
}

int wmain(int argc, wchar_t** argv) {
    Check(argc == 2, "Specificare il percorso assoluto della DLL.");
    HMODULE library = LoadLibraryExW(argv[1], nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    Check(library != nullptr, "Caricamento DLL");
    auto getClass = reinterpret_cast<HRESULT (WINAPI*)(REFCLSID, REFIID, void**)>(
        GetProcAddress(library, "DllGetClassObject"));
    auto canUnload = reinterpret_cast<HRESULT (WINAPI*)()>(GetProcAddress(library, "DllCanUnloadNow"));
    auto getState = reinterpret_cast<GetDemoStateFunction>(GetProcAddress(library, "GetDemoState"));
    Check(getClass && canUnload && getState, "Esportazioni");
    Check(canUnload() == S_OK, "DLL inizialmente scaricabile");
    IClassFactory* factory = nullptr;
    Check(SUCCEEDED(getClass(CLSID_MemoryLab, IID_PPV_ARGS(&factory))), "Class factory");
    Check(canUnload() == S_FALSE, "Factory mantiene la DLL caricata");
    IContextMenu* handler = nullptr;
    Check(SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&handler))), "Creazione handler");
    factory->Release();
    IShellExtInit* initializer = nullptr;
    Check(SUCCEEDED(handler->QueryInterface(IID_PPV_ARGS(&initializer))), "IShellExtInit");
    Check(SUCCEEDED(initializer->Initialize(nullptr, nullptr, nullptr)), "Initialize");
    initializer->Release();
    HMENU menu = CreatePopupMenu();
    Check(menu != nullptr, "Creazione menu");
    Check(handler->QueryContextMenu(menu, 0, 100, 200, CMF_DEFAULTONLY) == S_OK,
        "Nessuna voce quando richiesta solo azione predefinita");
    Check(GetMenuItemCount(menu) == 0, "Menu predefinito vuoto");
    HRESULT status = handler->QueryContextMenu(menu, 0, 100, 200, CMF_NORMAL);
    Check(SUCCEEDED(status) && HRESULT_CODE(status) == 3 && GetMenuItemID(menu, 0) == 100
        && GetMenuItemID(menu, 1) == 101 && GetMenuItemID(menu, 2) == 102,
        "Inserimento voce nel menu");
    wchar_t verb[32];
    Check(SUCCEEDED(handler->GetCommandString(0, GCS_VERBW, nullptr,
        reinterpret_cast<LPSTR>(verb), ARRAYSIZE(verb))) && lstrcmpW(verb, L"memorylab") == 0,
        "Verbo Unicode");
    DemoState before{}, after{};
    getState(&before);
    CMINVOKECOMMANDINFO command{};
    command.cbSize = sizeof(command);
    command.fMask = CMIC_MASK_FLAG_NO_UI;
    command.lpVerb = MAKEINTRESOURCEA(0);
    Check(SUCCEEDED(handler->InvokeCommand(&command)), "Invocazione comando numerico");
    command.lpVerb = "memorylab";
    Check(SUCCEEDED(handler->InvokeCommand(&command)), "Invocazione verbo testuale");
    command.lpVerb = MAKEINTRESOURCEA(1);
    Check(SUCCEEDED(handler->InvokeCommand(&command)), "Avvio file.exe e verifica del PID padre nel figlio");
    CMINVOKECOMMANDINFOEX unicodeCommand{};
    unicodeCommand.cbSize = sizeof(unicodeCommand);
    unicodeCommand.fMask = CMIC_MASK_FLAG_NO_UI | CMIC_MASK_UNICODE;
    unicodeCommand.lpVerb = "memorylab.launch";
    unicodeCommand.lpVerbW = L"memorylab.launch";
    Check(SUCCEEDED(handler->InvokeCommand(reinterpret_cast<CMINVOKECOMMANDINFO*>(&unicodeCommand))),
        "Avvio tramite verbo Unicode e verifica del PID padre");
    command.lpVerb = MAKEINTRESOURCEA(2);
    Check(SUCCEEDED(handler->InvokeCommand(&command)), "Avvio dalla DLL con attributo padre e verifica nel figlio");
    command.lpVerb = "memorylab.choose";
    Check(SUCCEEDED(handler->InvokeCommand(&command)), "Verbo del nuovo comando");
    command.lpVerb = MAKEINTRESOURCEA(3);
    Check(handler->InvokeCommand(&command) == E_INVALIDARG, "Rifiuto comando sconosciuto");
    getState(&after);
    Check(after.counter == before.counter + 2 && after.processId == GetCurrentProcessId(),
        "Contatore e PID del processo ospite");
    MEMORY_BASIC_INFORMATION region{};
    Check(VirtualQuery(reinterpret_cast<void*>(after.counterAddress), &region, sizeof(region)) != 0
        && region.State == MEM_COMMIT && region.Type == MEM_IMAGE,
        "Contatore contenuto in una regione immagine allocata");
    Check(*reinterpret_cast<volatile LONG*>(after.counterAddress) == after.counter,
        "Lettura diretta del contatore condiviso nello spazio del processo");
    handler->Release();
    DestroyMenu(menu);
    Check(canUnload() == S_OK, "Rilascio finale dei riferimenti COM");
    FreeLibrary(library);
    std::puts("OK: DLL, COM, tre voci menu, contatore, avvio normale e con padre esplicito, verifica nel figlio e rilascio.");
    return 0;
}
