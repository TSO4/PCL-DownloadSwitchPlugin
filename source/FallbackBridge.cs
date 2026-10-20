// SPDX-License-Identifier: MIT
// 备用下载模块入口。读取当前 PCL 游戏目录；独立进程安装完成后，仅请求原有版本列表刷新。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
namespace PclDownloadSwitch
{
    internal static class FallbackBridge
    {
        private static DispatcherTimer watch;
        private static DateTime lastResult;
        private static string lastError;
        internal static string GameFolder()
        {
            try { return ReadGameFolder(); }
            catch (Exception error) { DiagnosticsLog.Write("BACKUP_FOLDER", "无法识别当前游戏目录，请手动选择。", error); return ""; }
        }
        private static string ReadGameFolder()
        {
            if (Runtime.MainWindow == null) return "";
            var type = Runtime.MainWindow.GetType().Assembly.GetType("PCL.ModMinecraft");
            if (type == null) return "";
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (string name in new[] { "McFolderSelected", "PathMcFolder" })
            {
                var prop = type.GetProperty(name, flags);
                var field = type.GetField(name, flags);
                object value = prop != null && prop.PropertyType == typeof(string) && prop.GetIndexParameters().Length == 0 ?
                    prop.GetValue(null, null) : field != null && field.FieldType == typeof(string) ? field.GetValue(null) : null;
                string folder = value as string;
                if (!String.IsNullOrWhiteSpace(folder) && Path.IsPathRooted(folder)) return Path.GetFullPath(folder).TrimEnd('\\');
            }
            return "";
        }
        internal static List<BackupGameFolder> GameFolders()
        {
            var folders = new List<BackupGameFolder>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = GameFolder();
            if (current.Length != 0) { known.Add(current); folders.Add(new BackupGameFolder { Name = "当前 PCL 目录", Path = current }); }
            if (Runtime.MainWindow == null) return folders;
            var type = Runtime.MainWindow.GetType().Assembly.GetType("PCL.ModMinecraft");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var field = type == null ? null : type.GetField("McFolderList", flags);
            var list = field == null ? null : field.GetValue(null) as IEnumerable;
            if (list == null) return folders;
            foreach (object item in list)
            {
                string path = FolderText(item, "Location");
                if (String.IsNullOrWhiteSpace(path)) path = FolderText(item, "Path");
                if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.Length > 512) continue;
                path = Path.GetFullPath(path).TrimEnd('\\');
                string name = FolderText(item, "Name");
                if (name.Length > 120) name = name.Substring(0, 120);
                if (known.Add(path)) folders.Add(new BackupGameFolder { Name = name.Length == 0 ? "PCL 游戏目录" : name, Path = path });
                if (folders.Count >= 64) break;
            }
            return folders;
        }
        private static string FolderText(object item, string name)
        {
            if (item == null) return "";
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = item.GetType();
            var field = type.GetField(name, flags);
            if (field != null && field.FieldType == typeof(string)) return (string)field.GetValue(item) ?? "";
            var prop = type.GetProperty(name, flags);
            return prop != null && prop.PropertyType == typeof(string) && prop.GetIndexParameters().Length == 0 ?
                (string)prop.GetValue(item, null) ?? "" : "";
        }
        internal static void Start()
        {
            if (watch != null) return;
            watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            watch.Tick += delegate { Tick(); };
            Runtime.MainWindow.Closed += delegate { watch.Stop(); };
            watch.Start(); Tick();
        }
        private static void Tick()
        {
            try
            {
                using (var self = Process.GetCurrentProcess())
                {
                    SettingsStore.AtomicWrite(FallbackState.HostPath, new XDocument(new XElement("BackupHost",
                        new XElement("Pid", self.Id), new XElement("Started", self.StartTime.ToUniversalTime().Ticks),
                        new XElement("Folder", GameFolder()), new XElement("Busy", Runtime.HostTasksBusy),
                        new XElement("Folders", GameFolders().Select(folder => new XElement("Folder",
                            new XAttribute("name", folder.Name), new XAttribute("path", folder.Path)))),
                        new XElement("Updated", DateTime.UtcNow))));
                    if (!File.Exists(FallbackState.ResultPath) || File.GetLastWriteTimeUtc(FallbackState.ResultPath) <= lastResult) return;
                    lastResult = File.GetLastWriteTimeUtc(FallbackState.ResultPath);
                    var root = SettingsStore.ReadXml(FallbackState.ResultPath).Root;
                    if (root == null || (int)root.Element("Host") != self.Id ||
                        (long)root.Element("Started") != self.StartTime.ToUniversalTime().Ticks) return;
                    string target = ((string)root.Element("Target") ?? "").TrimEnd('\\');
                    if (!String.Equals(target, GameFolder(), StringComparison.OrdinalIgnoreCase))
                    { DiagnosticsLog.Write("BACKUP_OTHER_FOLDER", "已安装到其他目录，请在 PCL 版本选择中添加：" + target); return; }
                    if (!Runtime.VerifiedBuild)
                    { DiagnosticsLog.Write("BACKUP_REFRESH", "此构建未实测刷新接口，请在 PCL 版本选择中点击刷新。"); return; }
                    var type = Runtime.MainWindow.GetType().Assembly.GetType("PCL.ModMinecraft", true);
                    var loader = Runtime.Field(type, "McInstanceListLoader").GetValue(null);
                    Runtime.Method(loader.GetType(), "Start", 2).Invoke(loader, new object[] { target + "\\", true });
                    DiagnosticsLog.Write("BACKUP_REFRESH", "已请求 PCL 刷新当前目录的版本列表。");
                }
                lastError = null;
            }
            catch (Exception error)
            {
                string signature = error.GetBaseException().Message;
                if (lastError != signature) DiagnosticsLog.Write("BACKUP_BRIDGE", "备用模块状态/刷新失败；游戏文件已独立处理。", error);
                lastError = signature;
            }
        }
        internal static FrameworkElement View()
        {
            var stack = new StackPanel { Margin = new Thickness(14, 10, 20, 10) };
            stack.Children.Add(Ui.Text("备用下载", 25, "#EFF5FF"));
            var text = Ui.Text("独立获取原版、Forge、Fabric、NeoForge 和 Quilt 列表并安装。\n" +
                "即使 PCL 下载模块不可用，也可使用随包的“PCL备用下载.exe”。\n\n" +
                "默认选当前 PCL 游戏目录；可从 PCL 已添加的目录中选择，或新建其他目录。", 13, "#B9CBE0");
            text.LineHeight = 25; text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 16, 0, 18);
            stack.Children.Add(text);
            var folder = Ui.Text("", 12, "#83B9FF"); folder.TextWrapping = TextWrapping.Wrap; stack.Children.Add(folder);
            var open = Ui.Button("打开备用下载", "打开独立备用下载模块", 180);
            open.HorizontalAlignment = HorizontalAlignment.Left; open.Margin = new Thickness(0, 24, 0, 20);
            stack.Children.Add(open);
            var note = Ui.Text("下载完成后，当前目录会请求 PCL 刷新版本列表。\n" +
                "安装到其他目录时，请在 PCL 的版本选择中添加该目录。\n" +
                "模块会下载官方文件和运行官方加载器安装器；仅在你点击“开始安装”后执行。", 12, "#AFC1DB");
            note.LineHeight = 24; note.TextWrapping = TextWrapping.Wrap; stack.Children.Add(note);
            stack.Loaded += delegate { folder.Text = "当前游戏目录：\n" + (GameFolder().Length == 0 ? "未能识别，打开模块后请选择。" : GameFolder()); };
            open.Click += delegate
            {
                try
                {
                    Tick();
                    string path = Path.Combine(ReleaseInfo.Folder, "PCL备用下载.exe");
                    if (!File.Exists(path)) throw new FileNotFoundException("缺少备用模块，请完整解压新版插件。", path);
                    using (var self = Process.GetCurrentProcess())
                        Process.Start(new ProcessStartInfo(path, "--host " + self.Id + " --started " +
                            self.StartTime.ToUniversalTime().Ticks + " --directory \"" + GameFolder().TrimEnd('\\') + "\"") {
                                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                }
                catch (Exception error) { DiagnosticsLog.Write("BACKUP_OPEN", "无法打开备用模块。", error); note.Text = error.GetBaseException().Message; }
            };
            return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
    }
}
