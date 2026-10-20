// SPDX-License-Identifier: MIT
// 独立安装流水线：暂存、官方安装器、依赖/资源校验、最后登记版本。
// 只向选定游戏目录发布版本/库/资源，不复制官方启动器个人配置，不改 PCL 程序。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace PclDownloadSwitch.Backup
{
    internal sealed class InstallerService
    {
        private readonly InstallerNetwork network;
        private readonly string existingCache;
        internal InstallerService(InstallerNetwork network, string existingCache = null)
        { this.network = network; this.existingCache = existingCache; }

        internal async Task<string> Install(GameVersion game, LoaderVersion loader, string destination,
            string selectedJava, IProgress<InstallProgress> progress, CancellationToken token, string versionName = null)
        {
            if (String.IsNullOrWhiteSpace(destination) || !Path.IsPathRooted(destination))
                throw new InvalidOperationException("请先选择游戏目录。");
            string target = Path.GetFullPath(destination).TrimEnd('\\', '/');
            if (target.Length <= Path.GetPathRoot(target).Length)
                throw new InvalidOperationException("请选择专门的游戏文件夹，不能直接安装到磁盘根目录。");
            if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请选择游戏目录的真实位置，不使用目录联接。");
            string customName = ValidateName(versionName, target, loader == null ? null : game.Id);
            string jobs = Path.Combine(DiagnosticsLog.Folder, "installer", "jobs");
            string jobId = Guid.NewGuid().ToString("N");
            string job = InstallPaths.Under(jobs, jobId);
            // 暂存根目录保持较短，避免插件位于较深目录时触发旧 Java/Windows 的路径长度限制。
            // 日志仍保存在插件自己的 data 目录；发布成功或失败后仅清理本次 GUID 暂存目录。
            string stages = Path.Combine(Path.GetTempPath(), "RKBackup");
            string stage = InstallPaths.Under(stages, jobId);
            Directory.CreateDirectory(job);
            Directory.CreateDirectory(stage);
            DiagnosticsLog.Write("BACKUP_START", "安装 " + game.Id + " / " +
                (loader == null ? "原版" : loader.Kind + " " + loader.Version) + "；目标：" + target);
            try
            {
                progress.Report(new InstallProgress("读取并校验游戏安装信息…"));
                await network.File(new DownloadFile { RelativePath = "version.json", Url = game.Url, Sha1 = game.Sha1 },
                    job, null, token).ConfigureAwait(false);
                string baseText = File.ReadAllText(Path.Combine(job, "version.json"));
                var profile = Json.Parse(baseText);
                if (Json.Required(profile, "id") != game.Id) throw new InvalidDataException("游戏版本信息与选择不一致。");
                string baseId = InstallPaths.VersionId(game.Id);
                InstallPaths.WriteText(InstallPaths.Under(stage, "versions/" + baseId + "/" + baseId + ".json"), baseText);
                var client = Json.Get(Json.Get(profile, "downloads"), "client");
                if (client == null) throw new InvalidDataException("此历史版本没有可识别的 Java 客户端下载信息。");
                var required = new List<DownloadFile> { FromDownload(client, "versions/" + baseId + "/" + baseId + ".jar") };
                required.AddRange(Libraries(profile));
                await Files(required, stage, target, "准备游戏与运行库", progress, token).ConfigureAwait(false);
                ValidateLibraries(profile, stage);
                string installedId = baseId;
                if (loader != null && (loader.Kind == "Fabric" || loader.Kind == "Quilt"))
                {
                    progress.Report(new InstallProgress("读取 " + loader.Kind + " 安装配置…"));
                    string text = await network.Text(loader.ProfileUrl, token).ConfigureAwait(false);
                    var modProfile = Json.Parse(text);
                    if (Json.Required(modProfile, "inheritsFrom") != baseId)
                        throw new InvalidDataException("加载器配置与所选 MC 版本不一致。");
                    installedId = InstallPaths.VersionId(Json.Required(modProfile, "id"));
                    if (installedId == baseId) throw new InvalidDataException("加载器版本 ID 与原版冲突。");
                    await Files(Libraries(modProfile), stage, target, "安装 " + loader.Kind + " 运行库", progress, token).ConfigureAwait(false);
                    ValidateLibraries(modProfile, stage);
                    InstallPaths.WriteText(InstallPaths.Under(stage, "versions/" + installedId + "/" + installedId + ".json"), text);
                }
                else if (loader != null)
                {
                    progress.Report(new InstallProgress("下载并校验 " + loader.Kind + " 官方安装器…"));
                    await network.File(new DownloadFile { RelativePath = "loader-installer.jar", Url = loader.InstallerUrl },
                        job, null, token).ConfigureAwait(false);
                    string installer = Path.Combine(job, "loader-installer.jar");
                    installedId = ReadInstallerVersion(installer, baseId);
                    int minimum = (int)Json.Number(Json.Get(profile, "javaVersion"), "majorVersion", 8);
                    string java = await InstallerJava.Resolve(selectedJava, minimum, token).ConfigureAwait(false);
                    DiagnosticsLog.Write("BACKUP_JAVA", loader.Kind + " 使用 Java：" + java);
                    // 官方安装器只接触自己的暂存目录；这里的空配置不会覆盖用户的启动器配置。
                    InstallPaths.WriteText(Path.Combine(stage, "launcher_profiles.json"), "{\"profiles\":{},\"selectedProfile\":\"\"}");
                    progress.Report(new InstallProgress("正在运行 " + loader.Kind + " 官方安装器，请等待…"));
                    await InstallerJava.Run(java, installer, stage, Path.Combine(job, "loader-install.log"), progress, token).ConfigureAwait(false);
                    string generated = InstallPaths.Under(stage, "versions/" + installedId + "/" + installedId + ".json");
                    if (!File.Exists(generated)) throw new InvalidDataException("安装器没有生成预期版本，可能不支持此安装器格式。请查看安装日志。");
                    var modProfile = Json.Parse(File.ReadAllText(generated));
                    if (Json.Required(modProfile, "id") != installedId || Json.Required(modProfile, "inheritsFrom") != baseId)
                        throw new InvalidDataException("安装器生成了与选择不一致的版本。");
                    await Files(Libraries(modProfile), stage, target, "校验加载器运行库", progress, token).ConfigureAwait(false);
                    ValidateLibraries(modProfile, stage);
                    PruneInstallerFiles(stage, profile, modProfile);
                }
                await Assets(profile, stage, target, progress, token).ConfigureAwait(false);
                var logging = Json.Get(Json.Get(Json.Get(profile, "logging"), "client"), "file");
                if (logging != null) {
                    progress.Report(new InstallProgress("准备游戏日志配置…"));
                    await network.File(FromDownload(logging, "assets/log_configs/" + Json.Required(logging, "id")), stage, target, token, existingCache).ConfigureAwait(false);
                }
                if (customName != null) installedId = RenameStagedVersion(stage, installedId, customName);
                progress.Report(new InstallProgress("检查目标目录，准备登记安装结果…"));
                await Task.Run(() => Publish(stage, target, progress, token), token).ConfigureAwait(false);
                DiagnosticsLog.Write("BACKUP_DONE", "安装完成：" + installedId + "；目录：" + target);
                progress.Report(new InstallProgress("安装完成：" + installedId, 1, 1));
                return installedId;
            }
            catch (OperationCanceledException) { DiagnosticsLog.Write("BACKUP_CANCELED", "用户取消安装；原有版本未被覆盖。"); throw; }
            catch (Exception error) { DiagnosticsLog.Write("BACKUP_ERROR", "备用安装失败；日志目录：" + job, error); throw; }
            finally
            {
                // 清理仅限本次 GUID 工作目录里的 game 暂存目录，保留安装器/日志用于排错。
                string expected = Path.GetFullPath(stages).TrimEnd('\\') + "\\" + jobId;
                if (String.Equals(Path.GetFullPath(stage), expected, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFullPath(stage).StartsWith(Path.GetFullPath(stages).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
                    catch (Exception cleanup) { DiagnosticsLog.Write("BACKUP_CLEANUP", "暂存目录清理失败：" + stage, cleanup); }
            }
        }

        // 自定义名称仅作用于本次新安装。已有版本、原版继承目标及共享库保持原样。
        internal static string ValidateName(string name, string target, string inheritedBase)
        {
            if (String.IsNullOrWhiteSpace(name)) return null;
            if (name != name.Trim() || name.Length > 80)
                throw new InvalidDataException("版本名称不能以空白开头或结尾，且最多 80 个字符。");
            InstallPaths.VersionId(name);
            if (String.Equals(name, inheritedBase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("加载器版本名称不能与原版版本号相同，请换个名称。");
            string folder = InstallPaths.Under(target, "versions/" + name);
            if (Directory.Exists(folder) || File.Exists(folder))
                throw new IOException("这个版本名称已存在：" + name + "。请换个名称；不会覆盖原有版本。");
            return name;
        }

        internal static string RenameStagedVersion(string stage, string oldId, string newId)
        {
            InstallPaths.VersionId(oldId); InstallPaths.VersionId(newId);
            if (String.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase)) return oldId;
            string original = InstallPaths.Under(stage, "versions/" + oldId);
            string destination = InstallPaths.Under(stage, "versions/" + newId);
            if (!Directory.Exists(original) || (File.GetAttributes(original) & FileAttributes.ReparsePoint) != 0 ||
                Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("无法为本次安装设置版本名称：目录缺失、包含链接或已存在。");
            string jsonPath = InstallPaths.Under(stage, "versions/" + oldId + "/" + oldId + ".json");
            var profile = Json.Obj(Json.Parse(File.ReadAllText(jsonPath)));
            if (Json.Required(profile, "id") != oldId) throw new InvalidDataException("待重命名的版本信息不一致。");
            if (String.Equals(Json.Str(profile, "inheritsFrom"), newId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("版本名称不能与它继承的原版名称相同。");
            // PCL 用 clientVersion 区分显示名称与实际 MC 版本；避免自定义名称被当成 MC 版本号。
            if (String.IsNullOrEmpty(Json.Str(profile, "clientVersion")))
            {
                string inherited = Json.Str(profile, "inheritsFrom");
                profile["clientVersion"] = inherited.Length == 0 ? oldId : inherited;
            }
            profile["id"] = newId;
            if (Json.Str(profile, "jar") == oldId) profile["jar"] = newId;
            // 移动范围严格限定在本次暂存目录内，发布时仍统一进行冲突检查。
            Directory.Move(original, destination);
            string oldJson = InstallPaths.Under(stage, "versions/" + newId + "/" + oldId + ".json");
            string newJson = InstallPaths.Under(stage, "versions/" + newId + "/" + newId + ".json");
            File.Move(oldJson, newJson);
            InstallPaths.WriteText(newJson, Json.Serialize(profile));
            string oldJar = InstallPaths.Under(stage, "versions/" + newId + "/" + oldId + ".jar");
            if (File.Exists(oldJar))
                File.Move(oldJar, InstallPaths.Under(stage, "versions/" + newId + "/" + newId + ".jar"));
            DiagnosticsLog.Write("BACKUP_RENAME", "本次安装：" + oldId + " → " + newId);
            return newId;
        }
        private static string ReadInstallerVersion(string installer, string minecraft)
        {
            using (var archive = ZipFile.OpenRead(installer))
            {
                var entry = archive.GetEntry("install_profile.json");
                if (entry == null || entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("官方安装器格式无法识别。");
                object install;
                using (var reader = new StreamReader(entry.Open())) install = Json.Parse(reader.ReadToEnd());
                string game = Json.Str(install, "minecraft");
                if (game.Length == 0) game = Json.Str(Json.Get(install, "versionInfo"), "inheritsFrom");
                if (game.Length != 0 && game != minecraft) throw new InvalidDataException("安装器对应的 MC 版本与选择不一致。");
                string id = Json.Str(install, "version");
                if (id.Length == 0) id = Json.Str(Json.Get(install, "versionInfo"), "id");
                if (id.Length == 0 || id == minecraft) throw new InvalidDataException("无法确定加载器版本名称。");
                return InstallPaths.VersionId(id);
            }
        }
        internal static DownloadFile FromDownload(object value, string path)
        {
            return new DownloadFile { RelativePath = path, Url = Json.Required(value, "url"),
                Sha1 = Json.Str(value, "sha1"), Size = Json.Number(value, "size") };
        }
        internal static List<DownloadFile> Libraries(object profile)
        {
            var result = new List<DownloadFile>();
            foreach (object lib in Json.Array(Json.Get(profile, "libraries")))
            {
                if (!Allowed(Json.Get(lib, "rules"))) continue;
                object downloads = Json.Get(lib, "downloads"), artifact = Json.Get(downloads, "artifact");
                string coordinate = Json.Required(lib, "name");
                if (artifact != null)
                {
                    string url = Json.Str(artifact, "url");
                    // 处理器生成的文件没有下载 URL，必须已由官方安装器生成；稍后单独核对。
                    if (url.Length != 0) result.Add(FromDownload(artifact, "libraries/" + Json.Required(artifact, "path")));
                }
                else if (downloads == null)
                {
                    string path = InstallPaths.MavenPath(coordinate);
                    string repository = Json.Str(lib, "url", "https://libraries.minecraft.net/");
                    result.Add(new DownloadFile { RelativePath = "libraries/" + path,
                        Url = repository.TrimEnd('/') + "/" + path, Sha1 = Json.Str(lib, "sha1"), Size = Json.Number(lib, "size") });
                }
                string native = Json.Str(Json.Get(lib, "natives"), "windows");
                if (native.Length != 0)
                {
                    native = native.Replace("${arch}", "64");
                    object item = Json.Get(Json.Get(downloads, "classifiers"), native);
                    if (item == null) throw new InvalidDataException("缺少 Windows 本地运行库：" + coordinate);
                    result.Add(FromDownload(item, "libraries/" + Json.Required(item, "path")));
                }
            }
            return result;
        }
        internal static bool Allowed(object rules)
        {
            if (rules == null) return true;
            bool allowed = false;
            foreach (object rule in Json.Array(rules))
            {
                object os = Json.Get(rule, "os");
                string name = Json.Str(os, "name"), arch = Json.Str(os, "arch"), version = Json.Str(os, "version");
                if (name.Length != 0 && name != "windows") continue;
                if (arch.Length != 0 && arch != "x86_64" && arch != "amd64") continue;
                if (version.Length != 0 && !Regex.IsMatch(Environment.OSVersion.Version.ToString(), version, RegexOptions.None, TimeSpan.FromSeconds(1))) continue;
                var features = Json.Get(rule, "features") as Dictionary<string, object>;
                if (features != null && features.Values.Any(v => v is bool && (bool)v)) continue;
                allowed = Json.Str(rule, "action") == "allow";
            }
            return allowed;
        }
        private static void ValidateLibraries(object profile, string stage)
        {
            foreach (object lib in Json.Array(Json.Get(profile, "libraries")))
            {
                if (!Allowed(Json.Get(lib, "rules"))) continue;
                object downloads = Json.Get(lib, "downloads"), artifact = Json.Get(downloads, "artifact");
                string relative = artifact != null ? Json.Required(artifact, "path") :
                    downloads == null ? InstallPaths.MavenPath(Json.Required(lib, "name")) : null;
                if (relative == null) continue;
                string path = InstallPaths.Under(stage, "libraries/" + relative);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    throw new InvalidDataException("安装器缺少必要运行库：" + relative);
                string hash = Json.Str(artifact, "sha1");
                if (hash.Length != 0 && !InstallPaths.Matches(path, hash, Json.Number(artifact, "size")))
                    throw new InvalidDataException("生成的运行库校验失败：" + relative);
            }
        }
        private async Task Files(IEnumerable<DownloadFile> entries, string stage, string target, string label,
            IProgress<InstallProgress> progress, CancellationToken token)
        {
            var files = entries.GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).Select(group => {
                if (group.Select(f => f.Sha1).Where(h => !String.IsNullOrEmpty(h)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    throw new InvalidDataException("依赖列表包含内容冲突：" + group.Key);
                return group.First();
            }).ToList();
            var queue = new ConcurrentQueue<DownloadFile>(files);
            int done = 0;
            progress.Report(new InstallProgress(label, 0, files.Count));
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Exception failure = null;
                var workers = Enumerable.Range(0, Math.Min(16, files.Count)).Select(async worker => {
                    DownloadFile file;
                    while (queue.TryDequeue(out file))
                    {
                        try { await network.File(file, stage, target, cancel.Token, existingCache).ConfigureAwait(false); }
                        catch (Exception error) {
                            if (!(error is OperationCanceledException)) Interlocked.CompareExchange(ref failure, error, null);
                            cancel.Cancel(); throw;
                        }
                        int complete = Interlocked.Increment(ref done);
                        if (complete == files.Count || complete % 10 == 0 || files.Count < 30)
                            progress.Report(new InstallProgress(label + " · " + complete + " / " + files.Count, complete, files.Count));
                    }
                }).ToArray();
                try { await Task.WhenAll(workers).ConfigureAwait(false); }
                catch {
                    token.ThrowIfCancellationRequested();
                    if (failure != null) throw new IOException("下载依赖失败：" + failure.Message, failure);
                    throw;
                }
            }
        }
        private async Task Assets(object profile, string stage, string target, IProgress<InstallProgress> progress, CancellationToken token)
        {
            object index = Json.Get(profile, "assetIndex");
            if (index == null) throw new InvalidDataException("此历史版本的资源索引格式暂不支持。");
            string id = InstallPaths.VersionId(Json.Required(index, "id"));
            string relative = "assets/indexes/" + id + ".json";
            progress.Report(new InstallProgress("读取游戏资源索引…"));
            await network.File(FromDownload(index, relative), stage, target, token, existingCache).ConfigureAwait(false);
            id = PreserveAssetIndex(profile, stage, target);
            relative = "assets/indexes/" + id + ".json";
            object data = Json.Parse(File.ReadAllText(InstallPaths.Under(stage, relative)));
            var objects = Json.Obj(Json.Get(data, "objects"));
            var files = new List<DownloadFile>();
            foreach (var asset in objects)
            {
                string hash = Json.Required(asset.Value, "hash");
                if (!Regex.IsMatch(hash, "^[0-9a-f]{40}$")) throw new InvalidDataException("资源哈希格式错误。");
                files.Add(new DownloadFile { RelativePath = "assets/objects/" + hash.Substring(0, 2) + "/" + hash,
                    Url = "https://resources.download.minecraft.net/" + hash.Substring(0, 2) + "/" + hash,
                    Sha1 = hash, Size = Json.Number(asset.Value, "size") });
            }
            await Files(files, stage, target, "下载与校验游戏资源", progress, token).ConfigureAwait(false);
            foreach (var asset in objects)
            {
                token.ThrowIfCancellationRequested();
                string hash = Json.Required(asset.Value, "hash");
                string source = InstallPaths.Under(stage, "assets/objects/" + hash.Substring(0, 2) + "/" + hash);
                foreach (string folder in new[] {
                    Json.Bool(data, "virtual") ? "assets/virtual/" + id + "/" : null,
                    Json.Bool(data, "map_to_resources") ? "resources/" : null }.Where(p => p != null))
                {
                    string output = InstallPaths.Under(stage, folder + asset.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    File.Copy(source, output, true);
                }
            }
        }
        // 同一个官方索引 ID 可能存在不同内容快照。仅为新版本另存一份，不覆盖旧版本的索引。
        internal static string PreserveAssetIndex(object profile, string stage, string target)
        {
            var index = Json.Obj(Json.Get(profile, "assetIndex"));
            string id = InstallPaths.VersionId(Json.Required(index, "id"));
            string relative = "assets/indexes/" + id + ".json";
            string incoming = InstallPaths.Under(stage, relative), existing = InstallPaths.Under(target, relative);
            if (!File.Exists(existing) || InstallPaths.Hash(existing, true) == InstallPaths.Hash(incoming, true)) return id;
            if (!InstallPaths.Matches(incoming, Json.Required(index, "sha1"), Json.Number(index, "size")))
                throw new InvalidDataException("新资源索引未通过官方元数据校验。");
            string snapshot = InstallPaths.VersionId("rk-" + id + "-" + InstallPaths.Hash(incoming));
            File.Move(incoming, InstallPaths.Under(stage, "assets/indexes/" + snapshot + ".json"));
            index["id"] = snapshot;
            Json.Obj(profile)["assets"] = snapshot;
            string version = InstallPaths.VersionId(Json.Required(profile, "id"));
            InstallPaths.WriteText(InstallPaths.Under(stage, "versions/" + version + "/" + version + ".json"), Json.Serialize(profile));
            DiagnosticsLog.Write("BACKUP_INDEX_SNAPSHOT", "已有资源索引内容不同，保留原文件，新版本使用独立索引：" + snapshot);
            return snapshot;
        }
        // Forge 的 mappings/处理器缓存只供安装阶段使用；只登记版本配置实际引用的运行库。
        internal static void PruneInstallerFiles(string stage, params object[] profiles)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in profiles)
                foreach (object lib in Json.Array(Json.Get(profile, "libraries")))
                {
                    if (!Allowed(Json.Get(lib, "rules"))) continue;
                    object downloads = Json.Get(lib, "downloads"), artifact = Json.Get(downloads, "artifact");
                    string path = artifact != null ? Json.Required(artifact, "path") :
                        downloads == null ? InstallPaths.MavenPath(Json.Required(lib, "name")) : null;
                    if (path != null) keep.Add(InstallPaths.Under(stage, "libraries/" + path));
                    string native = Json.Str(Json.Get(lib, "natives"), "windows").Replace("${arch}", "64");
                    if (native.Length != 0)
                        keep.Add(InstallPaths.Under(stage, "libraries/" +
                            Json.Required(Json.Get(Json.Get(downloads, "classifiers"), native), "path")));
                }
            string root = InstallPaths.Under(stage, "libraries");
            int removed = 0;
            if (Directory.Exists(root))
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string verified = InstallPaths.Under(stage, file.Substring(stage.TrimEnd('\\').Length + 1));
                    if (!keep.Contains(verified)) { File.Delete(verified); removed++; }
                }
            DiagnosticsLog.Write("BACKUP_INSTALLER_CACHE", "安装完成，省略未被运行配置引用的安装中间文件：" + removed);
        }
        internal static void Publish(string stage, string target, IProgress<InstallProgress> progress, CancellationToken token)
        {
            var files = new List<Tuple<string, string>>();
            foreach (string category in new[] { "libraries", "assets", "resources", "versions" })
            {
                string root = Path.Combine(stage, category);
                if (!Directory.Exists(root)) continue;
                foreach (string source in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    token.ThrowIfCancellationRequested();
                    string relative = source.Substring(stage.TrimEnd('\\').Length + 1);
                    string destination = InstallPaths.Under(target, relative);
                    if (File.Exists(destination))
                    {
                        if (new FileInfo(destination).Length != new FileInfo(source).Length || InstallPaths.Hash(destination, true) != InstallPaths.Hash(source, true))
                            throw new IOException("目标目录已有不同内容的文件，已停止，未覆盖它：\n" + destination + "\n可选择新建一个游戏目录后再安装。");
                    }
                    else files.Add(Tuple.Create(source, destination));
                }
            }
            // 版本 JSON 最后发布：资源/运行库未就绪时，不让 PCL 看到半安装的版本入口。
            var ordered = files.OrderBy(f => f.Item1.StartsWith(Path.Combine(stage, "versions") + "\\", StringComparison.OrdinalIgnoreCase) &&
                f.Item1.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
            int done = 0;
            foreach (var file in ordered)
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(file.Item2));
                string temp = Path.Combine(Path.GetDirectoryName(file.Item2), ".rk-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.Copy(file.Item1, temp, false);
                    try { File.Move(temp, file.Item2); }
                    catch (IOException)
                    {
                        if (!File.Exists(file.Item2) || InstallPaths.Hash(file.Item1, true) != InstallPaths.Hash(file.Item2, true)) throw;
                    }
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                done++;
                if (done % 30 == 0 || done == ordered.Count)
                    progress.Report(new InstallProgress("登记到所选游戏目录 · " + done + " / " + ordered.Count, done, ordered.Count));
            }
        }
    }
}
