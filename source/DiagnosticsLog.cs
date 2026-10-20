// SPDX-License-Identifier: MIT
// 入口程序与托管插件共用的本地日志。只记录插件行为，不读取 PCL 账号、配置或游戏日志。
using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace PclDownloadSwitch
{
    internal static class DiagnosticsLog
    {
        private static readonly object Gate = new object();
        internal static readonly string Folder = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "data");
        internal static readonly string LogPath = Path.Combine(Folder, "plugin.log");
        internal static string LastWriteFailure = "";
        private const int Limit = 524288;

        // 日志最多保留当前文件及上一份；写入失败只报告原因，不向其他位置偷偷写入。
        internal static void Write(string code, string message, Exception error = null)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= Limit)
                    {
                        string previous = Path.Combine(Folder, "plugin.previous.log");
                        File.Copy(LogPath, previous, true);
                        File.WriteAllText(LogPath, "", new UTF8Encoding(false));
                    }
                    string detail = error == null ? "" : Environment.NewLine + error;
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                        " [" + code + "] " + message + detail + Environment.NewLine;
                    // 限制单条日志长度，防止异常链意外变得过大。
                    if (line.Length > 32000) line = line.Substring(0, 32000) + "\n[truncated]\n";
                    using (var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        writer.Write(line);
                    LastWriteFailure = "";
                }
                catch (Exception failure) { LastWriteFailure = failure.Message; }
            }
        }

        internal static string Read()
        {
            try
            {
                if (!File.Exists(LogPath)) return "还没有插件日志。";
                using (var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    return reader.ReadToEnd();
            }
            catch (Exception error) { return "读取日志失败：" + error.Message; }
        }
    }
}
