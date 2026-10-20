// SPDX-License-Identifier: MIT
// Mojang、Fabric、Quilt、Forge、NeoForge 官方版本列表。选择具体版本，不追随未知路径执行。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
namespace PclDownloadSwitch.Backup
{
    internal sealed class InstallerCatalog
    {
        internal const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        private readonly InstallerNetwork network;
        internal InstallerCatalog(InstallerNetwork network) { this.network = network; }
        internal async Task<List<GameVersion>> Games(CancellationToken token)
        {
            var root = Json.Parse(await network.Text(ManifestUrl, token).ConfigureAwait(false));
            return Json.Array(Json.Get(root, "versions")).Select(item => new GameVersion {
                Id = InstallPaths.VersionId(Json.Required(item, "id")), Type = Json.Required(item, "type"),
                Url = Json.Required(item, "url"), Sha1 = Json.Required(item, "sha1")
            }).ToList();
        }
        internal async Task<List<LoaderVersion>> Loaders(string kind, string game, CancellationToken token)
        {
            if (kind == "原版") return new List<LoaderVersion>();
            if (kind == "Fabric" || kind == "Quilt")
            {
                string prefix = kind == "Fabric" ? "https://meta.fabricmc.net/v2" : "https://meta.quiltmc.org/v3";
                var items = Json.Array(Json.Parse(await network.Text(prefix + "/versions/loader/" + Uri.EscapeDataString(game), token).ConfigureAwait(false)));
                return items.Select(item => {
                    var loader = Json.Get(item, "loader");
                    string version = Json.Required(loader, "version");
                    return new LoaderVersion { Kind = kind, Version = version,
                        Stable = Json.Get(loader, "stable") is bool ? Json.Bool(loader, "stable") :
                            !Regex.IsMatch(version, "alpha|beta|snapshot|rc", RegexOptions.IgnoreCase),
                        ProfileUrl = prefix + "/versions/loader/" + Uri.EscapeDataString(game) + "/" + Uri.EscapeDataString(version) + "/profile/json" };
                }).OrderByDescending(x => x.Stable).ThenByDescending(x => x.Version, NaturalComparer.Instance).ToList();
            }
            string rootUrl, prefixVersion;
            if (kind == "Forge")
            {
                rootUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
                prefixVersion = game + "-";
            }
            else if (kind == "NeoForge")
            {
                if (game == "1.20.1")
                { rootUrl = "https://maven.neoforged.net/releases/net/neoforged/forge/"; prefixVersion = game + "-"; }
                else
                { rootUrl = "https://maven.neoforged.net/releases/net/neoforged/neoforge/"; prefixVersion = NeoPrefix(game); }
            }
            else throw new InvalidDataException("未知加载器：" + kind);
            if (prefixVersion == null) return new List<LoaderVersion>();
            string xml = await network.Text(rootUrl + "maven-metadata.xml", token).ConfigureAwait(false);
            XDocument metadata;
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                metadata = XDocument.Load(reader);
            string artifact = rootUrl.TrimEnd('/').Split('/').Last();
            return metadata.Descendants("version").Select(e => e.Value).Where(v => v.StartsWith(prefixVersion, StringComparison.Ordinal))
                .Select(v => new LoaderVersion { Kind = kind, Version = v,
                    Stable = !Regex.IsMatch(v, "alpha|beta|snapshot|rc", RegexOptions.IgnoreCase),
                    InstallerUrl = rootUrl + Uri.EscapeDataString(v) + "/" + artifact + "-" + Uri.EscapeDataString(v) + "-installer.jar" })
                .OrderByDescending(v => v.Stable).ThenByDescending(v => v.Version, NaturalComparer.Instance).ToList();
        }
        internal static string NeoPrefix(string game)
        {
            if (!Regex.IsMatch(game, @"^\d+\.\d+(\.\d+)?$")) return null;
            var parts = game.Split('.');
            if (parts[0] == "1")
                return parts[1] + "." + (parts.Length > 2 ? parts[2] : "0") + ".";
            return parts[0] + "." + parts[1] + ".";
        }
    }
    internal sealed class NaturalComparer : IComparer<string>
    {
        internal static readonly NaturalComparer Instance = new NaturalComparer();
        public int Compare(string a, string b)
        {
            string[] aa = Regex.Split(a, @"(\d+)"), bb = Regex.Split(b, @"(\d+)");
            for (int i = 0; i < Math.Min(aa.Length, bb.Length); i++)
            {
                long an, bn;
                int result = Int64.TryParse(aa[i], out an) && Int64.TryParse(bb[i], out bn) ?
                    an.CompareTo(bn) : StringComparer.OrdinalIgnoreCase.Compare(aa[i], bb[i]);
                if (result != 0) return result;
            }
            return aa.Length.CompareTo(bb.Length);
        }
    }
}
