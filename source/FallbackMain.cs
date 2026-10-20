// SPDX-License-Identifier: MIT
// 备用下载的独立窗口进程；可直接双击，也可从插件详情启动。不要求 PCL 下载模块存在。
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using PclDownloadSwitch;
using PclDownloadSwitch.Backup;
[assembly: AssemblyTitle("PCL 第三方下载开关 · 备用下载")]
[assembly: AssemblyVersion("1.3.2.0")]
internal static class FallbackMain
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!Environment.Is64BitProcess) throw new InvalidOperationException("备用安装器需要 64 位 Windows。");
            int host = 0; long started = 0; string folder = "";
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length) throw new ArgumentException("备用模块启动参数不完整。");
                switch (args[i])
                {
                    case "--host": host = Int32.Parse(args[i + 1]); break;
                    case "--started": started = Int64.Parse(args[i + 1]); break;
                    case "--directory": folder = args[i + 1]; break;
                    default: throw new ArgumentException("备用模块不支持此参数。");
                }
            }
            // 独立打开时，只有仍然存活的已接入 PCL 记录才可作为默认目录。
            if (host == 0 && File.Exists(FallbackState.HostPath))
            {
              try {
                var root = SettingsStore.ReadXml(FallbackState.HostPath).Root;
                if (root != null && DateTime.UtcNow - (DateTime)root.Element("Updated") < TimeSpan.FromSeconds(6) &&
                    FallbackState.Alive((int)root.Element("Pid"), (long)root.Element("Started")))
                {
                    host = (int)root.Element("Pid"); started = (long)root.Element("Started");
                    folder = (string)root.Element("Folder") ?? "";
                }
              } catch (Exception error) {
                  DiagnosticsLog.Write("BACKUP_HOST", "无法读取当前 PCL 目录，请在窗口中选择游戏目录。", error);
              }
            }
            var app = new Application();
            app.Run(new FallbackWindow(folder, host, started));
            return 0;
        }
        catch (Exception error)
        {
            DiagnosticsLog.Write("BACKUP_UI", "备用下载窗口未能启动。", error);
            MessageBox.Show(error.GetBaseException().Message + "\n\n日志：" + DiagnosticsLog.LogPath,
                "备用下载 · 启动失败", MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }
    }
}
