// SPDX-License-Identifier: MIT
// 独立接入入口。用户先正常启动 PCL，本程序只接入已运行且版本匹配的 PCL。
// 不启动 PCL、不改 PCL 文件、不提升权限，不把插件加载到 Minecraft。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using PclDownloadSwitch;

[assembly: AssemblyTitle("PCL 下载开关 · Real Knife — The Origin of 404")]
[assembly: AssemblyDescription("接入已运行的 PCL；原程序文件保持不变。")]
[assembly: AssemblyVersion("1.3.2.0")]

internal static class Launcher
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        string stage = "ATTACH_FILES";
        DiagnosticsLog.Write("ATTACH_START", "插件 " + ReleaseInfo.Version + " 启动；不启动 PCL。");
        try
        {
            ValidateFiles();
            if (args.Length == 1 && args[0] == "--watch")
            {
                if (!SettingsStore.Load().AutoAttach) return 0;
                bool first;
                using (var mutex = new Mutex(true, @"Local\PclDownloadSwitch.Watch." + AutoController.Identity, out first))
                {
                    if (!first) return 0;
                    try { using (var watcher = new AutoWatcher()) Application.Run(watcher); }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            if (args.Length != 0) throw new ArgumentException("不支持的启动参数，请直接双击插件。");
            stage = "ATTACH_FIND";
            var candidates = new List<Process>();
            int session = Process.GetCurrentProcess().SessionId;
            foreach (var process in Process.GetProcesses())
            {
                bool keep = false;
                try
                {
                    keep = process.SessionId == session &&
                        AssemblyName.GetAssemblyName(process.MainModule.FileName).Name == "Plain Craft Launcher 2";
                }
                catch { }
                if (keep) candidates.Add(process);
                else process.Dispose();
            }
            try
            {
                if (candidates.Count == 0)
                    throw new InvalidOperationException("请先像平常一样打开 PCL，等主界面出现后，再打开本插件。");
                if (candidates.Count != 1)
                    throw new InvalidOperationException("发现多个 PCL 进程。请只保留你要使用的那个 PCL，再打开插件。");
                stage = "ATTACH_CHECK";
                var result = AttachTarget(candidates[0], false);
                if (result != AttachOutcome.Declined && SettingsStore.Load().AutoAttach) AutoController.StartWatcher();
                return 0;
            }
            finally { foreach (var process in candidates) process.Dispose(); }
        }
        catch (Exception error)
        {
            DiagnosticsLog.Write(stage, "接入失败。", error);
            string logInfo = DiagnosticsLog.LastWriteFailure.Length == 0 ?
                "日志：" + DiagnosticsLog.LogPath : "日志写入失败：" + DiagnosticsLog.LastWriteFailure;
            MessageBox.Show(error.GetBaseException().Message + "\n\n错误编号：" + stage + "\n" + logInfo,
                "PCL 下载开关 · 未接入", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
    }

    internal enum AttachOutcome { Declined, Existing, Attached, Cancelled }

    internal static void ValidateFiles()
    {
        if (!Environment.Is64BitProcess) throw new InvalidOperationException("本插件需要 64 位 Windows。");
        foreach (string file in new[] { "PCL.DownloadSwitch.dll", "PCL.DownloadSwitch.Native.dll", "Mono.Cecil.dll" })
            if (!File.Exists(Path.Combine(ReleaseInfo.Folder, file)))
                throw new InvalidOperationException("插件文件不完整，缺少 " + file + "。\n请完整解压整个 DownloadSwitchPlugin 文件夹。");
    }

    internal static AttachOutcome AttachTarget(Process target, bool automatic, Action<AutoAttachStage> progress = null,
        CancellationToken cancellation = default(CancellationToken))
    {
        bool first;
        using (var gate = new Mutex(true, @"Local\PclDownloadSwitch.Attach." + target.Id, out first))
        {
            if (!first) return AttachOutcome.Existing;
            try
            {
                if (progress != null) progress(AutoAttachStage.Checking);
                ValidateFiles();
                target.Refresh();
                if (target.MainWindowHandle == IntPtr.Zero)
                    throw new InvalidOperationException("PCL 主窗口还没准备好，请等它启动完毕再试。");
                string bridge = Path.Combine(ReleaseInfo.Folder, "PCL.DownloadSwitch.Native.dll");
                foreach (ProcessModule module in target.Modules)
                {
                    if (!String.Equals(module.ModuleName, "PCL.DownloadSwitch.Native.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    // 自动模式不重新接管用户已手动关闭开关的进程，也不混装两个版本。
                    if (automatic) return AttachOutcome.Existing;
                    if (!String.Equals(module.FileName, bridge, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("这个 PCL 已加载另一个目录中的插件。更新插件后请先正常退出并重新打开 PCL。");
                }
                var check = Compatibility.Inspect(target.MainModule.FileName);
                DiagnosticsLog.Write("COMPATIBILITY", check.Summary + "\nSHA-256: " + check.Hash);
                if (!check.Supported)
                {
                    if (!automatic && File.Exists(Path.Combine(ReleaseInfo.Folder, "PCL备用下载.exe")) &&
                        MessageBox.Show("当前版本未通过兼容检查，已停止接入。\n" + check.Summary +
                            "\n\n是否打开独立的“备用下载”模块？它可以选择游戏目录并安装，不需要 PCL 的下载模块。",
                            "PCL 下载开关 · 备用入口", MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                            MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo(Path.Combine(ReleaseInfo.Folder, "PCL备用下载.exe")) {
                            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                        return AttachOutcome.Declined;
                    }
                    throw new InvalidOperationException("当前版本未通过兼容检查，已停止接入。\n" + check.Summary);
                }
                if (check.Capability == DownloadCapability.Native)
                {
                    if (progress != null) progress(AutoAttachStage.Confirmation);
                    if (MessageBox.Show("检测到 PCL " + check.Version +
                        " 本身能下载 MC。\n\n能自动下载，没必要启用这个插件。\n\n是否仍继续启动该插件？" +
                        "\n继续会打开辅助面板；不会给原本可用的下载页加上限制。",
                        "PCL 下载开关 · 无需恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes) return AttachOutcome.Declined;
                }
                else if (!automatic && !check.VerifiedBuild)
                {
                    if (MessageBox.Show("PCL " + check.Version + " 的下载页接口通过结构检查，但此构建尚未经实机验证。" +
                        "\n\n仍可能存在兼容性问题。建议先在副本中手动测试。\n是否继续接入？",
                        "PCL 下载开关 · 新版本", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes) return AttachOutcome.Declined;
                }
                // 请求与 PID、启动时间、MVID 绑定；运行时在执行任何页面操作前再次核对。
                if (automatic)
                {
                    var latest = SettingsStore.Load();
                    if (cancellation.IsCancellationRequested || !latest.AutoAttach ||
                        !String.Equals(latest.TargetPath, check.Path, StringComparison.OrdinalIgnoreCase))
                        return AttachOutcome.Cancelled;
                }
                if (progress != null) progress(AutoAttachStage.Attaching);
                var request = new AttachRequest {
                    Pid = target.Id, Started = target.StartTime.ToUniversalTime().Ticks,
                    Mvid = check.Mvid, HostVersion = check.Version, HostPath = check.Path,
                    Automatic = automatic, NativeAvailable = check.Capability == DownloadCapability.Native,
                    CanSwitch = check.CanSwitch, VerifiedBuild = check.VerifiedBuild
                };
                request.Save();
                try { NativeAttach.Run(target, bridge, Path.Combine(ReleaseInfo.Folder, "PCL.DownloadSwitch.dll")); }
                finally { if (File.Exists(AttachRequest.RequestPath(target.Id))) File.Delete(AttachRequest.RequestPath(target.Id)); }
                DiagnosticsLog.Write("ATTACH_OK", (automatic ? "自动" : "手动") + "接入 PCL，pid=" + target.Id + "。");
                return AttachOutcome.Attached;
            }
            finally { gate.ReleaseMutex(); }
        }
    }
}

internal static class NativeAttach
{
    // 仅申请接入所需的进程权限；不会请求调试特权或调整目标/系统的权限设置。
    private const uint Access = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
    private const uint MemCommitReserve = 0x3000, MemRelease = 0x8000, PageReadWrite = 4;

    internal static void Run(Process target, string bridge, string plugin)
    {
        IntPtr process = OpenProcess(Access, false, target.Id);
        if (process == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "无法接入 PCL。请让 PCL 和插件都以普通用户方式运行；插件不会自动请求管理员权限。");
        try
        {
            bool wow;
            if (!IsWow64Process(process, out wow)) throw new Win32Exception();
            if (wow) throw new InvalidOperationException("仅支持 64 位 PCL。");
            ProcessModule remoteBridge = FindModule(target, bridge, true);
            if (remoteBridge == null)
            {
                IntPtr loadLibrary = RemoteSystemFunction(target, "LoadLibraryW");
                uint loadResult = CallWithText(process, loadLibrary, bridge);
                remoteBridge = FindModule(target, bridge, true);
                if (remoteBridge == null)
                    throw new InvalidOperationException("无法加载插件桥接文件，返回值 0x" + loadResult.ToString("X8") + "。");
            }

            // 只映射本机自有桥接 DLL 来读取导出函数偏移，不执行其初始化代码。
            IntPtr mapped = LoadLibraryEx(bridge, IntPtr.Zero, 1);
            if (mapped == IntPtr.Zero) throw new Win32Exception();
            long offset;
            try
            {
                IntPtr export = GetProcAddress(mapped, "AttachPlugin");
                if (export == IntPtr.Zero) throw new InvalidOperationException("桥接文件缺少 AttachPlugin 入口。");
                offset = export.ToInt64() - mapped.ToInt64();
            }
            finally { FreeLibrary(mapped); }
            IntPtr entry = new IntPtr(checked(remoteBridge.BaseAddress.ToInt64() + offset));
            uint result = CallWithText(process, entry, plugin);
            if (result != 0)
                throw new InvalidOperationException("插件接入未完成，代码 0x" + result.ToString("X8") +
                    "。\n可查看插件目录 data\\plugin.log；原 PCL 文件没有被改写。");
        }
        finally { CloseHandle(process); }
    }

    // 系统导出函数可能转发到 KernelBase。按所属模块的相对偏移定位，
    // 不假定两个进程中的系统 DLL 基址相同，不硬编码远程地址。
    private static IntPtr RemoteSystemFunction(Process target, string name)
    {
        IntPtr pointer = GetProcAddress(GetModuleHandle("kernel32.dll"), name);
        if (pointer == IntPtr.Zero) throw new Win32Exception();
        using (var self = Process.GetCurrentProcess())
        {
            foreach (ProcessModule module in self.Modules)
            {
                long address = pointer.ToInt64(), start = module.BaseAddress.ToInt64();
                if (address < start || address >= start + module.ModuleMemorySize) continue;
                var remote = FindModule(target, module.ModuleName, false);
                if (remote == null) throw new InvalidOperationException("PCL 中没有对应的系统模块。");
                return new IntPtr(remote.BaseAddress.ToInt64() + address - start);
            }
        }
        throw new InvalidOperationException("无法定位系统加载函数。");
    }

    private static ProcessModule FindModule(Process target, string name, bool fullPath)
    {
        target.Refresh();
        foreach (ProcessModule module in target.Modules)
            if (string.Equals(fullPath ? module.FileName : module.ModuleName, name, StringComparison.OrdinalIgnoreCase))
                return module;
        return null;
    }

    private static uint CallWithText(IntPtr process, IntPtr function, string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
        IntPtr buffer = VirtualAllocEx(process, IntPtr.Zero, new UIntPtr((uint)bytes.Length), MemCommitReserve, PageReadWrite);
        if (buffer == IntPtr.Zero) throw new Win32Exception();
        bool mayStillRun = false;
        IntPtr thread = IntPtr.Zero;
        try
        {
            UIntPtr written;
            if (!WriteProcessMemory(process, buffer, bytes, new UIntPtr((uint)bytes.Length), out written) ||
                written.ToUInt64() != (ulong)bytes.Length) throw new Win32Exception();
            thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, function, buffer, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero) throw new Win32Exception();
            uint wait = WaitForSingleObject(thread, 20000);
            if (wait != 0)
            {
                // 超时时不终止 PCL/线程，也不释放仍可能被它使用的少量路径内存。
                // 这些内存在 PCL 退出后随进程回收。
                mayStillRun = true;
                throw new TimeoutException("PCL 暂时没有完成插件接入，请等待主界面响应后再试。没有终止 PCL。");
            }
            uint result;
            if (!GetExitCodeThread(thread, out result)) throw new Win32Exception();
            return result;
        }
        finally
        {
            if (thread != IntPtr.Zero) CloseHandle(thread);
            if (!mayStillRun) VirtualFreeEx(process, buffer, UIntPtr.Zero, MemRelease);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(IntPtr process, out bool wow);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, IntPtr threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeThread(IntPtr thread, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string module);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
}
