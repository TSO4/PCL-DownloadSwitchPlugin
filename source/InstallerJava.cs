// SPDX-License-Identifier: MIT
// 只运行用户选择/系统已安装的 Java 和官方安装器；取消仅结束本次创建的安装器进程。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace PclDownloadSwitch.Backup
{
    internal sealed class JavaRuntime
    {
        internal string Path;
        internal int Major;
    }

    internal static class InstallerJava
    {
        // 会话内复用版本检查；可执行文件大小/修改时间改变后重新检查。
        private static readonly object cacheGate = new object();
        private static readonly Dictionary<string, Tuple<long, long, int>> cache =
            new Dictionary<string, Tuple<long, long, int>>(StringComparer.OrdinalIgnoreCase);
        internal static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); }
                else { result.Append('\\', slashes); result.Append(c); }
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        internal static async Task<string> Resolve(string selected, int minimum, CancellationToken token)
        { return (await ResolveRuntime(selected, minimum, token).ConfigureAwait(false)).Path; }

        internal static async Task<JavaRuntime> ResolveRuntime(string selected, int minimum, CancellationToken token)
        {
            var candidates = new List<string>();
            if (!String.IsNullOrWhiteSpace(selected)) candidates.Add(Path.GetFullPath(selected));
            else
            {
                string envJava = Environment.GetEnvironmentVariable("JAVA_HOME");
                if (!String.IsNullOrWhiteSpace(envJava)) candidates.Add(Path.Combine(envJava, "bin", "java.exe"));
                foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                    if (!String.IsNullOrWhiteSpace(path))
                        try { candidates.Add(Path.Combine(path.Trim('"'), "java.exe")); } catch (ArgumentException) { }
                foreach (string vendor in new[] { "Java", "Microsoft", "Eclipse Adoptium", "Eclipse Foundation", "Zulu", "Amazon Corretto" })
                {
                    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), vendor);
                    if (!Directory.Exists(root)) continue;
                    try
                    {
                        foreach (string folder in Directory.GetDirectories(root))
                            candidates.Add(Path.Combine(folder, "bin", "java.exe"));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                // 官方启动器和 PCL 常用的运行时目录，只限定层数查找，不扫描整块磁盘。
                AddRuntimeFolder(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"), 4);
                AddRuntimeFolder(candidates, Path.Combine(Path.GetDirectoryName(ReleaseInfo.Folder), "Java"), 3);
                AddRuntimeFolder(candidates, Path.Combine(Path.GetDirectoryName(ReleaseInfo.Folder), "runtime"), 4);
            }
            var supported = new List<JavaRuntime>();
            using (var throttle = new SemaphoreSlim(4))
            {
                var checks = candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).Select(async path => {
                    await throttle.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        int major = await Task.Run(() => Probe(path, token), token).ConfigureAwait(false);
                        lock (supported) supported.Add(new JavaRuntime { Major = major, Path = path });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { DiagnosticsLog.Write("JAVA_PROBE", "未选用 Java：" + path, error); }
                    finally { throttle.Release(); }
                }).ToArray();
                await Task.WhenAll(checks).ConfigureAwait(false);
            }
            var result = Choose(supported, minimum);
            if (result == null) throw new InvalidOperationException("未找到可用的 Java " + minimum +
                " 或更高版本。请在“Java”一栏选择已经安装的 java.exe，再重试。");
            return result;
        }
        internal static JavaRuntime Choose(IEnumerable<JavaRuntime> candidates, int minimum)
        { return candidates.Where(x => x.Major >= minimum).OrderBy(x => x.Major).FirstOrDefault(); }

        private static void AddRuntimeFolder(List<string> candidates, string root, int depth)
        {
            if (depth < 0 || !Directory.Exists(root)) return;
            try
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
                string java = Path.Combine(root, "bin", "java.exe");
                if (File.Exists(java)) candidates.Add(java);
                foreach (string child in Directory.GetDirectories(root)) AddRuntimeFolder(candidates, child, depth - 1);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static int Probe(string java, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(java);
            long size = file.Length, modified = file.LastWriteTimeUtc.Ticks;
            lock (cacheGate)
            {
                Tuple<long, long, int> known;
                if (cache.TryGetValue(java, out known) && known.Item1 == size && known.Item2 == modified) return known.Item3;
            }
            using (var process = new Process { StartInfo = new ProcessStartInfo(java, "-version") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true } })
            {
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using (token.Register(delegate { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
                    if (!process.WaitForExit(5000)) { process.Kill(); throw new TimeoutException("Java 版本检查超时。"); }
                token.ThrowIfCancellationRequested();
                string text = stdout.Result + stderr.Result;
                var match = Regex.Match(text, "version\\s+\"(?<major>[0-9]+)(\\.(?<minor>[0-9]+))?");
                if (!match.Success) throw new InvalidDataException("无法识别 Java 版本。");
                int major = Int32.Parse(match.Groups["major"].Value);
                major = major == 1 ? Int32.Parse(match.Groups["minor"].Value) : major;
                lock (cacheGate) cache[java] = Tuple.Create(size, modified, major);
                return major;
            }
        }
        internal static async Task Run(string java, string installer, string gameFolder, string log,
            IProgress<InstallProgress> progress, CancellationToken token)
        {
            using (var process = new Process { StartInfo = new ProcessStartInfo(java,
                "-Djava.awt.headless=true -jar " + Quote(installer) + " --installClient " + Quote(gameFolder)) {
                    WorkingDirectory = Path.GetDirectoryName(installer), UseShellExecute = false,
                    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true } })
            using (var writer = new StreamWriter(log, false, new UTF8Encoding(false)))
            {
                object gate = new object();
                DataReceivedEventHandler line = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (gate) { writer.WriteLine(e.Data); writer.Flush(); }
                };
                process.OutputDataReceived += line; process.ErrorDataReceived += line;
                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                using (token.Register(delegate { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new InvalidOperationException("官方加载器安装器失败（退出码 " +
                    process.ExitCode + "）。请查看安装日志：" + log);
            }
        }
    }
}
