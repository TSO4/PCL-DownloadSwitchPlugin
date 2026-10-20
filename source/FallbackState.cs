// SPDX-License-Identifier: MIT
// 自有进程之间的少量状态交换；不读写 PCL 配置。PID 与启动时间防止复用过期状态。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
namespace PclDownloadSwitch
{
    internal sealed class BackupGameFolder
    {
        internal string Name, Path;
        public override string ToString() { return Name + "  ·  " + Path; }
    }
    internal static class FallbackState
    {
        internal static string BusyPath { get { return Path.Combine(DiagnosticsLog.Folder, "backup-busy.xml"); } }
        internal static string HostPath { get { return Path.Combine(DiagnosticsLog.Folder, "backup-host.xml"); } }
        internal static string ResultPath { get { return Path.Combine(DiagnosticsLog.Folder, "backup-result.xml"); } }
        internal static bool Alive(int pid, long started)
        {
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        internal static bool Busy()
        {
            if (!File.Exists(BusyPath)) return false;
            try
            {
                var root = SettingsStore.ReadXml(BusyPath).Root;
                return root != null && Alive((int)root.Element("Pid"), (long)root.Element("Started"));
            }
            catch { return true; } // 格式损坏时保持保护，详情日志可排错。
        }
        internal static void Begin(string target)
        {
            using (var self = Process.GetCurrentProcess())
                SettingsStore.AtomicWrite(BusyPath, new XDocument(new XElement("BackupBusy",
                    new XElement("Pid", self.Id), new XElement("Started", self.StartTime.ToUniversalTime().Ticks),
                    new XElement("Target", target))));
        }
        internal static void End()
        {
            if (!File.Exists(BusyPath)) return;
            var root = SettingsStore.ReadXml(BusyPath).Root;
            using (var self = Process.GetCurrentProcess())
                if ((int)root.Element("Pid") == self.Id && (long)root.Element("Started") == self.StartTime.ToUniversalTime().Ticks)
                    File.Delete(BusyPath);
        }
        internal static bool HostBusy(int pid, long started)
        {
            if (pid <= 0 || !Alive(pid, started)) return false;
            if (!File.Exists(HostPath)) return true;
            var root = SettingsStore.ReadXml(HostPath).Root;
            return root == null || (int)root.Element("Pid") != pid || (long)root.Element("Started") != started ||
                (bool)root.Element("Busy") || DateTime.UtcNow - (DateTime)root.Element("Updated") > TimeSpan.FromSeconds(6);
        }
        internal static List<BackupGameFolder> GameFolders(int pid, long started)
        {
            var folders = new List<BackupGameFolder>();
            if (pid <= 0 || !Alive(pid, started) || !File.Exists(HostPath)) return folders;
            var root = SettingsStore.ReadXml(HostPath).Root;
            if (root == null || (int)root.Element("Pid") != pid || (long)root.Element("Started") != started ||
                DateTime.UtcNow - (DateTime)root.Element("Updated") > TimeSpan.FromSeconds(6)) return folders;
            var list = root.Element("Folders");
            if (list == null) return folders;
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in list.Elements("Folder"))
            {
                string path = (string)item.Attribute("path") ?? "";
                if (!System.IO.Path.IsPathRooted(path)) continue;
                path = System.IO.Path.GetFullPath(path).TrimEnd('\\');
                if (known.Add(path)) folders.Add(new BackupGameFolder { Name = (string)item.Attribute("name") ?? "游戏目录", Path = path });
                if (folders.Count >= 64) break;
            }
            return folders;
        }
        internal static void Complete(int host, long started, string target, string version)
        {
            SettingsStore.AtomicWrite(ResultPath, new XDocument(new XElement("BackupResult",
                new XElement("Host", host), new XElement("Started", started), new XElement("Target", target),
                new XElement("Version", version), new XElement("Completed", DateTime.UtcNow))));
        }
    }
}
