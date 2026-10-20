// SPDX-License-Identifier: MIT
// 仅负责把托管插件加载到已运行的 .NET Framework PCL 默认 AppDomain。
// 不做代码字节补丁，不在 DllMain/加载器锁中启动 CLR，不访问网络或游戏数据。
#include <windows.h>
#include <metahost.h>

#pragma comment(lib, "mscoree.lib")

extern "C" __declspec(dllexport) DWORD WINAPI AttachPlugin(LPVOID parameter)
{
    if (parameter == nullptr) return static_cast<DWORD>(E_INVALIDARG);
    ICLRMetaHost* meta = nullptr;
    ICLRRuntimeInfo* runtime = nullptr;
    ICLRRuntimeHost* host = nullptr;
    DWORD result = 0;

    HRESULT hr = CLRCreateInstance(CLSID_CLRMetaHost, IID_PPV_ARGS(&meta));
    if (SUCCEEDED(hr)) hr = meta->GetRuntime(L"v4.0.30319", IID_PPV_ARGS(&runtime));
    if (SUCCEEDED(hr)) hr = runtime->GetInterface(CLSID_CLRRuntimeHost, IID_PPV_ARGS(&host));
    // PCL 的 CLR 已经在运行；这里既不启动另一个 CLR，也不停止已有 CLR。
    if (SUCCEEDED(hr))
        hr = host->ExecuteInDefaultAppDomain(
            static_cast<LPCWSTR>(parameter), L"PclDownloadSwitch.PluginEntry",
            L"Attach", L"", &result);

    if (host) host->Release();
    if (runtime) runtime->Release();
    if (meta) meta->Release();
    return FAILED(hr) ? static_cast<DWORD>(hr) : result;
}
