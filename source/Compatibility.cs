// SPDX-License-Identifier: MIT
// 接入前只读检查 PE/CLR 元数据和方法签名，不加载、执行或写回待检测的 PCL。
// 本文件只编译进接入程序；运行中的 PCL 不需要加载 Mono.Cecil。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PclDownloadSwitch
{
    internal enum DownloadCapability { Unknown, Restricted, Native }

    internal sealed class HostCheck
    {
        internal string Path, Version, Hash, Mvid;
        internal DownloadCapability Capability;
        internal bool Supported, CanSwitch, VerifiedBuild;
        internal readonly List<string> Problems = new List<string>();
        internal string Summary
        {
            get
            {
                return "PCL " + Version + " · " + Capability + " · " +
                    (Supported ? (CanSwitch ? "下载页接口匹配" : "辅助面板模式") : "停止接入") +
                    (Problems.Count == 0 ? "" : "\n" + String.Join("\n", Problems.ToArray()));
            }
        }
    }

    internal static class Compatibility
    {
        internal const string VerifiedPclHash = "6B6E1260CB5A1D6A303855F1C60CAB3D0BC1AEBBA7E40A964180188B271E7386";
        internal static HostCheck Inspect(string path)
        {
            var check = new HostCheck { Path = System.IO.Path.GetFullPath(path), Version = "未知" };
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                check.Hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                stream.Position = 0;
                using (var module = ModuleDefinition.ReadModule(stream, new ReaderParameters
                    { ReadingMode = ReadingMode.Deferred, InMemory = true, ReadSymbols = false }))
                {
                    if (module.Assembly == null || module.Assembly.Name.Name != "Plain Craft Launcher 2")
                    { check.Problems.Add("不是可识别的 PCL 程序集。"); return check; }
                    check.Version = module.Assembly.Name.Version.ToString();
                    check.Mvid = module.Mvid.ToString("D");
                    check.VerifiedBuild = check.Hash == VerifiedPclHash;
                    bool canRun64 = module.Architecture == TargetArchitecture.AMD64 ||
                        (module.Architecture == TargetArchitecture.I386 &&
                         (module.Attributes & ModuleAttributes.ILOnly) != 0 &&
                         (module.Attributes & (ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit)) == 0);
                    if (!canRun64 ||
                        !module.AssemblyReferences.Any(r => r.Name == "mscorlib"))
                    { check.Problems.Add("当前接入桥只支持 64 位 .NET Framework PCL。"); return check; }
                    var types = AllTypes(module.Types).ToDictionary(t => t.FullName);
                    TypeDefinition main, page, secret;
                    if (!types.TryGetValue("PCL.FormMain", out main) ||
                        !Inherits(types, main, "System.Windows.Window") ||
                        !types.TryGetValue("PCL.PageDownloadInstall", out page))
                    { check.Problems.Add("无法识别 PCL 主窗口或下载页。"); return check; }
                    bool? restrict = null;
                    if (types.TryGetValue("PCL.ModSecret", out secret))
                    {
                        var flag = secret.Fields.SingleOrDefault(f => f.Name == "RestrictMcDownload");
                        if (flag != null && flag.IsLiteral && flag.HasConstant && flag.Constant is bool)
                            restrict = (bool)flag.Constant;
                    }
                    var init = page.Methods.SingleOrDefault(m => m.Name == "LoaderInit" && m.Parameters.Count == 0);
                    if (init == null || !init.HasBody)
                    { check.Problems.Add("下载页初始化方式无法识别。"); return check; }
                    bool bindsNormal = init.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldsfld &&
                        i.Operand is FieldReference && ((FieldReference)i.Operand).Name == "DlClientListLoader" &&
                        ((FieldReference)i.Operand).DeclaringType.FullName == "PCL.ModDownload");
                    bool hasClosedLabel = init.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr &&
                        (string)i.Operand == "关闭下载");
                    bool callsInit = init.Body.Instructions.Any(i => i.Operand is MethodReference &&
                        ((MethodReference)i.Operand).Name == "PageLoaderInit");
                    // 常量和实际编译后的初始化路径必须一致；相互矛盾时不猜测。
                    if (callsInit && bindsNormal && !hasClosedLabel && restrict != true)
                        check.Capability = DownloadCapability.Native;
                    else if (callsInit && hasClosedLabel && !bindsNormal && restrict == true)
                        check.Capability = DownloadCapability.Restricted;
                    else
                    { check.Problems.Add("下载限制标志与初始化路径不明确，不能自动判断。"); return check; }

                    var problems = ValidateSwitchContract(types);
                    if (!Inherits(types, page, "PCL.MyPageRight"))
                        problems.Add("下载页基类不匹配。");
                    check.CanSwitch = problems.Count == 0;
                    if (check.Capability == DownloadCapability.Restricted && !check.CanSwitch)
                    { check.Problems.AddRange(problems); return check; }
                    if (check.Capability == DownloadCapability.Restricted &&
                        module.Assembly.Name.Version < new Version(2, 99, 0, 0))
                    { check.Problems.Add("不对早期版本新增恢复补丁，请使用其原生下载功能或更新启动器。"); return check; }
                    if (!check.CanSwitch) check.Problems.Add("此版本仅允许打开辅助面板，不能改动下载页。");
                    check.Supported = true;
                }
            }
            return check;
        }

        private static List<string> ValidateSwitchContract(Dictionary<string, TypeDefinition> types)
        {
            var problems = new List<string>();
            Action<string, string, string, string[]> method = (type, name, result, args) =>
            {
                TypeDefinition definition;
                if (!types.TryGetValue(type, out definition)) { problems.Add("缺少类型：" + type); return; }
                var matches = definition.Methods.Where(m => m.Name == name && m.Parameters.Count == args.Length).ToArray();
                if (matches.Length != 1 || matches[0].IsStatic || matches[0].ReturnType.FullName != result ||
                    !matches[0].Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(args))
                    problems.Add("方法签名不匹配：" + type + "." + name);
            };
            Action<string, string, bool, string> field = (type, name, isStatic, expected) =>
            {
                TypeDefinition definition;
                if (!types.TryGetValue(type, out definition)) { problems.Add("缺少类型：" + type); return; }
                var found = definition.Fields.SingleOrDefault(f => f.Name == name);
                if (found == null || found.IsStatic != isStatic || found.IsInitOnly ||
                    (expected != null && found.FieldType.FullName != expected))
                    problems.Add("字段不匹配：" + type + "." + name);
            };
            field("PCL.MyPageRight", "PageLoader", false, "PCL.ModLoader/LoaderBase");
            field("PCL.ModDownload", "DlClientListLoader", true,
                "PCL.ModLoader/LoaderTask`2<System.String,PCL.ModDownload/DlClientListResult>");
            field("PCL.ModLoader", "LoaderTaskbar", true, null);
            method("PCL.PageDownloadInstall", "LoadMinecraft_OnFinish", "System.Void", new string[0]);
            method("PCL.PageDownloadInstall", "get_LoadMinecraft", "PCL.MyLoading", new string[0]);
            method("PCL.MyPageRight", "PageOnForceExit", "System.Void", new string[0]);
            method("PCL.MyPageRight", "PageOnEnter", "System.Void", new string[0]);
            method("PCL.MyPageRight", "PageLoaderRestart", "System.Void", new[] { "System.Object", "System.Boolean" });
            method("PCL.MyPageRight", "PageLoaderState", "System.Void", new[] { "System.Object", "PCL.ModBase/LoadState", "PCL.ModBase/LoadState" });
            method("PCL.MyPageRight", "get_PageState", "PCL.MyPageRight/PageStates", new string[0]);
            method("PCL.MyLoading", "get_State", "PCL.ILoadingTrigger", new string[0]);
            method("PCL.MyLoading", "set_State", "System.Void", new[] { "PCL.ILoadingTrigger" });
            method("PCL.ModLoader/LoaderBase", "get_State", "PCL.ModBase/LoadState", new string[0]);
            foreach (var item in new[] {
                new[] { "PCL.ModBase/LoadState", "Waiting", "Loading", "Finished", "Failed", "Canceled" },
                new[] { "PCL.MyPageRight/PageStates", "Empty", "PageExit", "ContentExit", "ContentStay", "LoaderStay" } })
            {
                TypeDefinition enumeration;
                if (!types.TryGetValue(item[0], out enumeration) || !enumeration.IsEnum ||
                    !item.Skip(1).All(n => enumeration.Fields.Any(f => f.Name == n && f.IsLiteral)))
                    problems.Add("任务或页面状态枚举不匹配：" + item[0]);
            }
            foreach (var item in new[] {
                new[] { "PreviewFinish", "PCL.ModLoader/LoaderBase" },
                new[] { "OnStateChangedUi", "PCL.ModLoader/LoaderBase", "PCL.ModBase/LoadState", "PCL.ModBase/LoadState" } })
            {
                TypeDefinition loader, handler;
                if (!types.TryGetValue("PCL.ModLoader/LoaderBase", out loader)) { problems.Add("缺少 LoaderBase"); continue; }
                var evt = loader.Events.SingleOrDefault(e => e.Name == item[0]);
                if (evt == null || !types.TryGetValue(evt.EventType.FullName, out handler) ||
                    !handler.Methods.Any(m => m.Name == "Invoke" && m.ReturnType.FullName == "System.Void" &&
                        m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(item.Skip(1))))
                    problems.Add("加载器事件签名不匹配：" + item[0]);
            }
            return problems;
        }
        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
        {
            foreach (var type in roots)
            {
                yield return type;
                foreach (var child in AllTypes(type.NestedTypes)) yield return child;
            }
        }
        private static bool Inherits(Dictionary<string, TypeDefinition> types, TypeDefinition type, string expected)
        {
            var seen = new HashSet<string>();
            while (type != null && type.BaseType != null && seen.Add(type.FullName))
            {
                if (type.BaseType.FullName == expected) return true;
                if (!types.TryGetValue(type.BaseType.FullName, out type)) break;
            }
            return false;
        }
    }
}
