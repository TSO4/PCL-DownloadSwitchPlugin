// SPDX-License-Identifier: MIT
// 可选官方/BMCLAPI 下载源、有限回退、取消与哈希校验。未涉及账号或登录令牌。
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace PclDownloadSwitch.Backup
{
    internal sealed class InstallerNetwork : IDisposable
    {
        private readonly HttpClient client;
        internal readonly DownloadSources Sources = new DownloadSources();
        internal InstallerNetwork()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = Math.Max(32, ServicePointManager.DefaultConnectionLimit);
            client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RealKnife-BackupInstaller/1.2");
        }
        internal static Uri OfficialUri(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.UserInfo.Length != 0 ||
                !uri.IsDefaultPort || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new InvalidDataException("无效下载地址。");
            string host = uri.Host.ToLowerInvariant();
            bool allowed = host == "piston-meta.mojang.com" || host == "piston-data.mojang.com" ||
                host == "launchermeta.mojang.com" || host == "launcher.mojang.com" ||
                host == "libraries.minecraft.net" || host == "resources.download.minecraft.net" ||
                host == "meta.fabricmc.net" || host == "maven.fabricmc.net" ||
                host == "meta.quiltmc.org" || host == "maven.quiltmc.org" ||
                host == "maven.minecraftforge.net" || host == "files.minecraftforge.net" ||
                host == "maven.neoforged.net" || host == "bmclapi2.bangbang93.com";
            if (!allowed) throw new InvalidDataException("元数据引用了尚未支持的下载来源：" + host);
            return uri.Scheme == "https" ? uri : new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        }
        internal async Task<string> Text(string url, CancellationToken token)
        {
            Exception last = null;
            foreach (string candidate in Sources.Candidates(url, true))
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(25));
                    try
                    {
                        using (var response = await client.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                        using (timeout.Token.Register(response.Dispose))
                        {
                            response.EnsureSuccessStatusCode();
                            DownloadSources.ValidateResponse(candidate, response.RequestMessage.RequestUri);
                            if (response.Content.Headers.ContentLength > 32 * 1024 * 1024)
                                throw new InvalidDataException("服务器返回的数据过大。");
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var memory = new MemoryStream())
                            {
                                var buffer = new byte[32768]; int read;
                                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                                {
                                    if (memory.Length + read > 32 * 1024 * 1024) throw new InvalidDataException("服务器返回的数据过大。");
                                    memory.Write(buffer, 0, read);
                                }
                                return System.Text.Encoding.UTF8.GetString(memory.ToArray());
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        token.ThrowIfCancellationRequested();
                        last = timeout.IsCancellationRequested ? new TimeoutException("读取版本数据超时。", error) : error;
                        Sources.Failed(candidate);
                        DiagnosticsLog.Write("BACKUP_SOURCE", "版本数据请求失败，将尝试可用来源：" + new Uri(candidate).Host + "；" + last.Message);
                    }
                }
            }
            throw new IOException("无法读取安装数据：" + last.Message, last);
        }
        internal async Task<string> MavenSha1(string url, CancellationToken token)
        {
            string value = (await Text(url + ".sha1", token).ConfigureAwait(false)).Trim().Split(' ', '\r', '\n', '\t')[0];
            if (!Regex.IsMatch(value, "^[0-9a-fA-F]{40}$")) throw new InvalidDataException("Maven 校验文件格式错误。");
            return value.ToLowerInvariant();
        }
        internal async Task File(DownloadFile file, string stageRoot, string reuseRoot, CancellationToken token, string additionalCache = null)
        {
            token.ThrowIfCancellationRequested();
            string destination = InstallPaths.Under(stageRoot, file.RelativePath);
            string expected = file.Sha1;
            if (!String.IsNullOrEmpty(expected) && !Regex.IsMatch(expected, "^[0-9a-fA-F]{40}$"))
                throw new InvalidDataException("元数据 SHA-1 格式无效。");
            if (String.IsNullOrEmpty(expected)) expected = await MavenSha1(file.Url, token).ConfigureAwait(false);
            if (InstallPaths.Matches(destination, expected, file.Size)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            foreach (string cache in new[] { reuseRoot, additionalCache })
            {
                if (String.IsNullOrWhiteSpace(cache)) continue;
                string reuse = InstallPaths.Under(cache, file.RelativePath);
                if (InstallPaths.Matches(reuse, expected, file.Size))
                { System.IO.File.Copy(reuse, destination, true); return; }
            }
            Exception last = null;
            var tried = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var candidates = Sources.Candidates(file.Url, false);
                string candidate = System.Linq.Enumerable.FirstOrDefault(candidates, url => !tried.Contains(url)) ?? candidates[0];
                tried.Add(candidate);
                string temporary = Path.Combine(Path.GetDirectoryName(destination), ".rk-" + Guid.NewGuid().ToString("N") + ".part");
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    try
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(candidates.Length > 1 ? 10 : 25));
                        using (var response = await client.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                        using (timeout.Token.Register(response.Dispose))
                        {
                            response.EnsureSuccessStatusCode();
                            DownloadSources.ValidateResponse(candidate, response.RequestMessage.RequestUri);
                            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                            {
                                var buffer = new byte[65536]; int count; long received = 0;
                                for (;;)
                                {
                                    // 持续有数据时允许大文件慢速下载；45 秒无进展则主动关闭响应。
                                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                                    count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                                    if (count == 0) break;
                                    received += count;
                                    if (file.Size >= 0 && received > file.Size) throw new InvalidDataException("下载内容超过预期大小。");
                                    await output.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
                                }
                            }
                        }
                        if (!InstallPaths.Matches(temporary, expected, file.Size))
                            throw new InvalidDataException("文件校验失败：" + file.RelativePath);
                        if (System.IO.File.Exists(destination)) System.IO.File.Delete(destination);
                        System.IO.File.Move(temporary, destination);
                        return;
                    }
                    catch (Exception error)
                    {
                        token.ThrowIfCancellationRequested();
                        last = timeout.IsCancellationRequested ? new TimeoutException("下载超时：" + file.RelativePath, error) : error;
                        Sources.Failed(candidate);
                        DiagnosticsLog.Write("BACKUP_SOURCE", "文件请求失败（第 " + (attempt + 1) + " 次）：" + new Uri(candidate).Host +
                            " / " + file.RelativePath + "；" + last.Message);
                    }
                    finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
                }
                if (attempt < 2 && Sources.Candidates(file.Url, false)[0] == candidate)
                    await Task.Delay(800 * (attempt + 1), token).ConfigureAwait(false);
            }
            throw new IOException("下载失败：" + file.RelativePath + "\n" + last.Message, last);
        }
        public void Dispose() { client.Dispose(); }
    }
}
