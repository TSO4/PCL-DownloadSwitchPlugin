// SPDX-License-Identifier: MIT
// 仅保存插件自身设置。自动模式、Windows 登录启动均默认关闭。
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PclDownloadSwitch
{
    internal static class ReleaseInfo
    {
        internal const string Version = "1.3.2";
        internal static readonly string Folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        internal const string AutomaticWarning =
            "开启后，后台助手会等待指定目录中的 PCL 启动，在主界面就绪后自动接入并启用下载功能。\n\n" +
            "助手会显示托盘图标。未来版本即使通过结构检查，也可能出现兼容性问题、无响应或崩溃，建议先手动验证。接入错误或短时间退出只暂停本次；重新启动同一路径的 PCL 后会重新检查并恢复。主动关闭自动模式则保持关闭。\n\n" +
            "默认只在本次 Windows 会话中运行；“登录 Windows 时启动后台助手”需要另行开启。\n\n是否启用自动模式？";
    }
    internal sealed class PluginSettings
    {
        internal bool AutoAttach;
        internal bool StartWithWindows;
        internal string TargetPath = "";
    }
    internal static class SettingsStore
    {
        internal static readonly string PathName = Path.Combine(DiagnosticsLog.Folder, "settings.xml");
        internal static PluginSettings Load()
        {
            if (!File.Exists(PathName)) return new PluginSettings();
            try
            {
                var root = ReadXml(PathName).Root;
                if (root == null || root.Name != "DownloadSwitchSettings") throw new InvalidDataException("设置文件格式错误。");
                var settings = new PluginSettings
                {
                    AutoAttach = (string)root.Element("AutoAttach") == "true",
                    StartWithWindows = (string)root.Element("StartWithWindows") == "true",
                    TargetPath = (string)root.Element("TargetPath") ?? ""
                };
                if (settings.AutoAttach && !Path.IsPathRooted(settings.TargetPath))
                    throw new InvalidDataException("自动接入目标目录无效。");
                if (!settings.AutoAttach) settings.StartWithWindows = false;
                return settings;
            }
            catch (Exception error)
            {
                DiagnosticsLog.Write("SETTINGS_READ", "读取设置失败，自动模式按关闭处理。", error);
                return new PluginSettings();
            }
        }
        internal static void Save(PluginSettings settings)
        {
            if (settings.AutoAttach && !Path.IsPathRooted(settings.TargetPath))
                throw new InvalidDataException("请先在目标 PCL 中开启自动模式。");
            if (!settings.AutoAttach) settings.StartWithWindows = false;
            AtomicWrite(PathName, new XDocument(new XElement("DownloadSwitchSettings",
                new XAttribute("format", "1"),
                new XElement("AutoAttach", settings.AutoAttach ? "true" : "false"),
                new XElement("StartWithWindows", settings.StartWithWindows ? "true" : "false"),
                new XElement("TargetPath", settings.TargetPath))));
        }
        internal static XDocument ReadXml(string path)
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("设置文件过大。");
            using (var reader = XmlReader.Create(path, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 }))
                return XDocument.Load(reader);
        }
        internal static void AtomicWrite(string path, XDocument document)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings
                    { Encoding = new UTF8Encoding(false), Indent = true }))
                    document.Save(writer);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    internal static class AutoController
    {
        // 多个安装目录使用不同的托盘助手互斥名、登录快捷方式，不覆盖其他软件的入口。
        internal static string Identity
        {
            get
            {
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                        ReleaseInfo.Folder.ToUpperInvariant()))).Replace("-", "").Substring(0, 16);
            }
        }
        internal static string StartupLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "PCL下载开关-" + Identity + ".lnk"); }
        }
        internal static void StartWatcher()
        {
            Process.Start(new ProcessStartInfo(Path.Combine(ReleaseInfo.Folder, "PCL下载开关.exe"), "--watch")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
        internal static void SetLoginStartup(bool enable)
        {
            string link = StartupLink;
            if (!enable) { if (File.Exists(link)) File.Delete(link); return; }
            CreateStartupShortcut(link, Path.Combine(ReleaseInfo.Folder, "PCL下载开关.exe"));
        }
        internal static void CreateStartupShortcut(string link, string executable)
        {
            object shell = null, shortcut = null;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell", true);
                shell = Activator.CreateInstance(type);
                shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { executable });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "--watch" });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(executable) });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "PCL 下载开关：等待指定启动器并自动接入" });
                shortcutType.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, shortcut, new object[] { 7 });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
        internal static void Disable(string reason)
        {
            var settings = SettingsStore.Load();
            settings.AutoAttach = false;
            settings.StartWithWindows = false;
            SettingsStore.Save(settings);
            SetLoginStartup(false);
            DiagnosticsLog.Write("AUTO_DISABLED", reason);
        }
    }

    // 一次性的接入请求绑定到具体进程及其程序集 MVID，不能沿用另一进程的检测结果。
    internal sealed class AttachRequest
    {
        internal int Pid;
        internal long Started;
        internal string Mvid, HostVersion, HostPath;
        internal bool Automatic, NativeAvailable, CanSwitch, VerifiedBuild;
        internal static string RequestPath(int pid) { return Path.Combine(DiagnosticsLog.Folder, "attach-" + pid + ".xml"); }
        internal void Save()
        {
            SettingsStore.AtomicWrite(RequestPath(Pid), new XDocument(new XElement("Attach",
                new XAttribute("plugin", ReleaseInfo.Version),
                new XElement("Pid", Pid), new XElement("Started", Started), new XElement("Mvid", Mvid),
                new XElement("HostVersion", HostVersion), new XElement("HostPath", HostPath),
                new XElement("Automatic", Automatic), new XElement("NativeAvailable", NativeAvailable),
                new XElement("CanSwitch", CanSwitch), new XElement("VerifiedBuild", VerifiedBuild))));
        }
        internal static AttachRequest ReadForCurrentProcess(Assembly host)
        {
            using (var process = Process.GetCurrentProcess())
            {
                var root = SettingsStore.ReadXml(RequestPath(process.Id)).Root;
                if (root == null || (string)root.Attribute("plugin") != ReleaseInfo.Version ||
                    (int)root.Element("Pid") != process.Id || (long)root.Element("Started") != process.StartTime.ToUniversalTime().Ticks ||
                    (string)root.Element("Mvid") != host.ManifestModule.ModuleVersionId.ToString("D") ||
                    !String.Equals((string)root.Element("HostPath"), process.MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("接入请求与当前 PCL 进程不一致，请重新打开插件。");
                return new AttachRequest
                {
                    Pid = process.Id, Started = process.StartTime.ToUniversalTime().Ticks,
                    Mvid = (string)root.Element("Mvid"), HostVersion = (string)root.Element("HostVersion"),
                    HostPath = (string)root.Element("HostPath"), Automatic = (bool)root.Element("Automatic"),
                    NativeAvailable = (bool)root.Element("NativeAvailable"), CanSwitch = (bool)root.Element("CanSwitch"),
                    VerifiedBuild = (bool)root.Element("VerifiedBuild")
                };
            }
        }
    }
}
