// SPDX-License-Identifier: MIT
// BMCLAPI 路径映射依照其公开接口文档独立实现；不读写 PCL 的下载源设置。
// 自动模式的列表优先官方，文件优先镜像；失败回退。Quilt 按上游文档暂时保留官方源。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
namespace PclDownloadSwitch.Backup
{
    internal enum DownloadSource { Automatic, Official, Bmclapi }
    internal sealed class DownloadSources
    {
        internal DownloadSource Mode = DownloadSource.Automatic;
        private readonly ConcurrentDictionary<string, DateTime> failures = new ConcurrentDictionary<string, DateTime>();
        internal static string Mirror(string value)
        {
            Uri uri = InstallerNetwork.OfficialUri(value);
            string prefix = "https://bmclapi2.bangbang93.com", path = uri.PathAndQuery;
            switch (uri.Host.ToLowerInvariant())
            {
                case "piston-meta.mojang.com": case "piston-data.mojang.com":
                case "launchermeta.mojang.com": case "launcher.mojang.com": return prefix + path;
                case "resources.download.minecraft.net": return prefix + "/assets" + path;
                case "libraries.minecraft.net": case "maven.fabricmc.net":
                case "maven.minecraftforge.net": return prefix + "/maven" + path;
                case "files.minecraftforge.net":
                    return path.StartsWith("/maven/", StringComparison.Ordinal) ? prefix + path : null;
                case "meta.fabricmc.net": return prefix + "/fabric-meta" + path;
                case "maven.neoforged.net":
                    return path.StartsWith("/releases/", StringComparison.Ordinal) ?
                        prefix + "/maven/" + path.Substring("/releases/".Length) : null;
                default: return null;
            }
        }
        internal string[] Candidates(string original, bool metadata)
        {
            original = InstallerNetwork.OfficialUri(original).AbsoluteUri;
            string mirror = Mirror(original);
            var all = new List<string>();
            if (Mode != DownloadSource.Official && mirror != null &&
                (Mode == DownloadSource.Bmclapi || !metadata)) all.Add(mirror);
            all.Add(original);
            if (Mode != DownloadSource.Official && mirror != null && !all.Contains(mirror)) all.Add(mirror);
            var available = all.Where(url => {
                DateTime until;
                return !failures.TryGetValue(new Uri(url).Host, out until) || until < DateTime.UtcNow;
            }).ToArray();
            return available.Length == 0 ? all.ToArray() : available;
        }
        internal void Failed(string url)
        {
            failures[new Uri(url).Host] = DateTime.UtcNow.AddMinutes(5);
        }
        internal static void ValidateResponse(string requested, Uri final)
        {
            // BMCLAPI 会把文件重定向到 OpenBMCLAPI 的分发节点。节点不使用固定域名，
            // 只接受由本次镜像请求返回的 HTTPS 跳转，下载内容仍需通过预期哈希校验。
            if (new Uri(requested).Host.Equals("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase))
            {
                if (final.Scheme != "https" || final.UserInfo.Length != 0 || final.IsLoopback)
                    throw new System.IO.InvalidDataException("镜像返回了不支持的下载跳转。");
            }
            else InstallerNetwork.OfficialUri(final.AbsoluteUri);
        }
    }
}
