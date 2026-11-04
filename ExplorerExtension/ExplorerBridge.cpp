#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <shldisp.h>
#include <exdisp.h>
#include <wrl/client.h>
#include <strsafe.h>
#include "ParentLaunch.h"
using Microsoft::WRL::ComPtr;

struct TextVariant : VARIANT {
    explicit TextVariant(LPCWSTR value) {
        VariantInit(this);
        vt = VT_BSTR;
        bstrVal = SysAllocString(value);
    }
    ~TextVariant() { VariantClear(this); }
    TextVariant(const TextVariant&) = delete;
    TextVariant& operator=(const TextVariant&) = delete;
};

// Il chiamante inizializza COM in STA. Gli oggetti ottenuti appartengono
// all'Explorer che ospita il desktop, al quale viene delegata l'apertura.
extern "C" __declspec(dllexport) HRESULT WINAPI LaunchViaExplorerWithOptions(
    LPCWSTR executable, LPCWSTR arguments, LPCWSTR workingDirectory, BOOL elevated, DWORD* explorerPid) {
    if (!executable || !*executable || !explorerPid) return E_INVALIDARG;
    *explorerPid = 0;
    ComPtr<IShellWindows> windows;
    HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&windows));
    if (FAILED(hr)) return hr;
    VARIANT empty{};
    long hwnd = 0;
    ComPtr<IDispatch> desktop;
    hr = windows->FindWindowSW(&empty, &empty, SWC_DESKTOP, &hwnd, SWFO_NEEDDISPATCH, &desktop);
    if (hr != S_OK || !desktop) return FAILED(hr) ? hr : HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    GetWindowThreadProcessId(reinterpret_cast<HWND>(static_cast<LONG_PTR>(hwnd)), explorerPid);
    if (!*explorerPid) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    ComPtr<IServiceProvider> services;
    hr = desktop.As(&services);
    if (FAILED(hr)) return hr;
    ComPtr<IShellBrowser> browser;
    hr = services->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser));
    if (FAILED(hr)) return hr;
    ComPtr<IShellView> view;
    hr = browser->QueryActiveShellView(&view);
    if (FAILED(hr)) return hr;
    ComPtr<IDispatch> automation;
    hr = view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(&automation));
    if (FAILED(hr)) return hr;
    ComPtr<IShellFolderViewDual> folder;
    hr = automation.As(&folder);
    if (FAILED(hr)) return hr;
    ComPtr<IDispatch> application;
    hr = folder->get_Application(&application);
    if (FAILED(hr)) return hr;
    ComPtr<IShellDispatch2> shell;
    hr = application.As(&shell);
    if (FAILED(hr)) return hr;
    TextVariant file(executable), parameters(arguments ? arguments : L""), directory(workingDirectory ? workingDirectory : L""), verb(elevated ? L"runas" : L"open");
    if (!file.bstrVal || !parameters.bstrVal || !directory.bstrVal || !verb.bstrVal) return E_OUTOFMEMORY;
    VARIANT show{};
    show.vt = VT_I4;
    show.lVal = SW_SHOWNORMAL;
    return shell->ShellExecute(file.bstrVal, parameters, directory, verb, show);
}

extern "C" __declspec(dllexport) HRESULT WINAPI LaunchViaExplorerInDirectory(
    LPCWSTR executable, LPCWSTR arguments, LPCWSTR workingDirectory, DWORD* explorerPid) {
    return LaunchViaExplorerWithOptions(executable, arguments, workingDirectory, FALSE, explorerPid);
}

// In questo esercizio CreateProcessW e chiamata dalla GUI (X), mentre
// Windows registra Explorer come padre del nuovo processo (Y).
extern "C" __declspec(dllexport) HRESULT WINAPI CreateWithExplorerParentInDirectory(
    LPCWSTR executable, LPCWSTR arguments, LPCWSTR workingDirectory, DWORD* parentPid, DWORD* childPid) {
    if (!executable || !*executable || !parentPid || !childPid) return E_INVALIDARG;
    *parentPid = 0;
    *childPid = 0;
    HWND desktop = GetShellWindow();
    if (!desktop || !GetWindowThreadProcessId(desktop, parentPid))
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);

    PROCESS_INFORMATION child{};
    HRESULT result = CreateChildWithParent(executable, arguments, *parentPid, &child, workingDirectory);
    if (SUCCEEDED(result)) {
        *childPid = child.dwProcessId;
        CloseHandle(child.hThread);
        CloseHandle(child.hProcess);
    }
    return result;
}

// Mantiene disponibili gli ingressi usati dalle versioni precedenti.
extern "C" __declspec(dllexport) HRESULT WINAPI CreateSuspendedWithExplorerParent(
    LPCWSTR executable, LPCWSTR arguments, LPCWSTR workingDirectory,
    DWORD* parentPid, PROCESS_INFORMATION* child) {
    if (!parentPid || !child) return E_INVALIDARG;
    *parentPid = 0;
    *child = {};
    HWND desktop = GetShellWindow();
    if (!desktop || !GetWindowThreadProcessId(desktop, parentPid))
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    // Gli handle restano aperti: la GUI ne assume la proprieta e puo riprendere il thread.
    return CreateChildWithParent(executable, arguments, *parentPid, child,
        workingDirectory, CREATE_SUSPENDED);
}

extern "C" __declspec(dllexport) HRESULT WINAPI LaunchViaExplorer(
    LPCWSTR executable, LPCWSTR arguments, DWORD* explorerPid) {
    return LaunchViaExplorerInDirectory(executable, arguments, nullptr, explorerPid);
}

extern "C" __declspec(dllexport) HRESULT WINAPI CreateElevatedSuspendedWithExplorerParent(
    LPCWSTR executable, LPCWSTR arguments, LPCWSTR workingDirectory,
    DWORD* parentPid, PROCESS_INFORMATION* child) {
    if (!parentPid || !child) return E_INVALIDARG;
    *parentPid = 0;
    *child = {};
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY, &token))
        return HRESULT_FROM_WIN32(GetLastError());
    TOKEN_ELEVATION elevation{};
    DWORD returned = 0;
    if (!GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &returned)
        || !elevation.TokenIsElevated) {
        CloseHandle(token);
        return E_ACCESSDENIED;
    }
    HWND desktop = GetShellWindow();
    if (!desktop || !GetWindowThreadProcessId(desktop, parentPid)) {
        CloseHandle(token);
        return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }
    // Il token elevato e esplicito: non viene ereditato quello Medium di Explorer.
    HRESULT result = CreateChildWithParent(executable, arguments, *parentPid, child,
        workingDirectory, CREATE_SUSPENDED, token);
    CloseHandle(token);
    return result;
}

extern "C" __declspec(dllexport) HRESULT WINAPI CreateWithExplorerParent(
    LPCWSTR executable, LPCWSTR arguments, DWORD* parentPid, DWORD* childPid) {
    return CreateWithExplorerParentInDirectory(executable, arguments, nullptr, parentPid, childPid);
}
