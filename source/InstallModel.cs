// SPDX-License-Identifier: MIT
// 备用安装器自有模型与路径校验。读取公开启动配置格式，不依赖 PCL 下载程序集。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
namespace PclDownloadSwitch.Backup
{
    internal sealed class GameVersion
    {
        internal string Id, Type, Url, Sha1;
        public override string ToString() { return Id + (Type == "release" ? "  · 正式版" : "  · " + Type); }
    }
    internal sealed class LoaderVersion
    {
        internal string Kind, Version, ProfileUrl, InstallerUrl;
        internal bool Stable = true;
        public override string ToString() { return Version + (Stable ? "" : "  · 预览版"); }
    }
    internal sealed class InstallProgress
    {
        internal string Message;
        internal int Done, Total;
        internal InstallProgress(string message, int done = 0, int total = 0) { Message = message; Done = done; Total = total; }
    }
    internal sealed class DownloadFile
    {
        internal string RelativePath, Url, Sha1;
        internal long Size = -1;
    }
    internal static class Json
    {
        internal static object Parse(string text)
        { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 100 }.DeserializeObject(text); }
        internal static string Serialize(object value)
        { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 100 }.Serialize(value); }
        internal static Dictionary<string, object> Obj(object value)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) throw new InvalidDataException("服务器返回的数据对象格式不符。");
            return result;
        }
        internal static object Get(object value, string key)
        { object found; var obj = value as Dictionary<string, object>; return obj != null && obj.TryGetValue(key, out found) ? found : null; }
        internal static string Str(object value, string key, string fallback = "")
        { object result = Get(value, key); return result == null ? fallback : Convert.ToString(result); }
        internal static object[] Array(object value)
        { return value as object[] ?? new object[0]; }
        internal static bool Bool(object value, string key) { return Get(value, key) is bool && (bool)Get(value, key); }
        internal static long Number(object value, string key, long fallback = -1)
        { object result = Get(value, key); return result == null ? fallback : Convert.ToInt64(result); }
        internal static string Required(object value, string key)
        {
            string result = Str(value, key);
            if (String.IsNullOrWhiteSpace(result)) throw new InvalidDataException("元数据缺少：" + key);
            return result;
        }
    }
    internal static class InstallPaths
    {
        internal static string Under(string root, string relative)
        {
            if (String.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("安装路径不是相对路径：" + relative);
            string[] parts = relative.Replace('\\', '/').Split('/');
            foreach (string part in parts)
            {
                if (part == "" || part == "." || part == ".." || part.TrimEnd('.', ' ') != part ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("不安全的安装路径：" + relative);
            }
            string folder = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string result = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装路径超出了选定目录。");
            // 不通过现有链接目录写到另一个位置；避免混淆用户所选的安装范围。
            string current = Path.GetDirectoryName(result);
            while (current != null && current.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("安装目录包含链接/联接，请选择真实目录：" + current);
                current = Path.GetDirectoryName(current);
            }
            return result;
        }
        internal static string VersionId(string id)
        { Under(Path.GetTempPath(), id); if (id.Contains("/") || id.Contains("\\")) throw new InvalidDataException("版本名称含有目录分隔符。"); return id; }
        internal static string MavenPath(string coordinate)
        {
            var extensionParts = coordinate.Split('@');
            var parts = extensionParts[0].Split(':');
            if (parts.Length < 3 || parts.Length > 4 || extensionParts.Length > 2)
                throw new InvalidDataException("无法识别 Maven 坐标：" + coordinate);
            string extension = extensionParts.Length == 2 ? extensionParts[1] : "jar";
            string path = parts[0].Replace('.', '/') + "/" + parts[1] + "/" + parts[2] + "/" +
                parts[1] + "-" + parts[2] + (parts.Length == 4 ? "-" + parts[3] : "") + "." + extension;
            Under(Path.GetTempPath(), path);
            return path;
        }
        internal static string Hash(string path, bool sha256 = false)
        {
            using (var file = File.OpenRead(path))
            using (HashAlgorithm hash = sha256 ? (HashAlgorithm)SHA256.Create() : SHA1.Create())
                return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        internal static bool Matches(string path, string sha1, long size)
        {
            if (!File.Exists(path) || (size >= 0 && new FileInfo(path).Length != size)) return false;
            return !String.IsNullOrEmpty(sha1) && String.Equals(Hash(path), sha1, StringComparison.OrdinalIgnoreCase);
        }
        internal static void WriteText(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, value, new UTF8Encoding(false));
        }
    }
}
