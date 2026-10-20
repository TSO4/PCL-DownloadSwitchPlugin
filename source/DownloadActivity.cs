// SPDX-License-Identifier: MIT
// 只读观察当前 PCL 的任务列表。任务等待/执行期间不允许改变下载页开关。
using System;
using System.Collections;
using System.Reflection;

namespace PclDownloadSwitch
{
    internal sealed class DownloadActivity
    {
        private readonly FieldInfo taskbar;
        internal bool Locked { get; private set; }
        internal bool HostBusy { get; private set; }
        internal string Reason { get; private set; }
        private string lastFault;

        internal DownloadActivity(Assembly pcl)
        {
            taskbar = Runtime.Field(pcl.GetType("PCL.ModLoader", true), "LoaderTaskbar");
            Locked = true;
            Reason = "正在检查任务状态";
        }

        internal void Refresh()
        {
            bool locked;
            string reason;
            try
            {
                var tasks = taskbar.GetValue(null) as IEnumerable;
                if (tasks == null) throw new InvalidOperationException("PCL 任务列表不可用。");
                int count = CountActive(tasks);
                HostBusy = count != 0;
                bool backup = FallbackState.Busy();
                locked = count != 0 || backup;
                reason = backup ? "备用下载正在安装，开关暂时锁定" :
                    locked ? "下载 / 安装任务进行中（" + count + "）" : "";
                lastFault = null;
            }
            catch (Exception error)
            {
                // 结构不匹配、并发枚举失败时保持原开关状态，暂时禁用切换，下次检查再尝试。
                locked = true;
                HostBusy = true;
                reason = "暂时无法读取任务状态，开关已锁定";
                string fault = error.GetBaseException().GetType().FullName + ": " + error.GetBaseException().Message;
                if (fault != lastFault)
                    DiagnosticsLog.Write("GUARD_READ", "无法读取 PCL 任务状态；保持现有开关状态。", error);
                lastFault = fault;
            }
            if (Locked != locked || Reason != reason)
                DiagnosticsLog.Write(locked ? "GUARD_LOCK" : "GUARD_UNLOCK", locked ? reason : "任务已结束，开关恢复可用。");
            Locked = locked;
            Reason = reason;
        }

        // 使用顶层任务状态覆盖整个下载/安装流程，包括下载之间的校验与解压阶段。
        // 不以网络速度或进度百分比判断：速度为零或进度已到 100% 时任务仍可能未结束。
        internal static int CountActive(IEnumerable tasks)
        {
            int count = 0;
            foreach (object task in tasks)
            {
                if (task == null) throw new InvalidOperationException("发现空的 PCL 任务记录。");
                string state = Convert.ToString(Runtime.Property(task, "State"));
                switch (state)
                {
                    case "Waiting":
                    case "Loading": count++; break;
                    case "Finished":
                    case "Failed":
                    case "Aborted":
                    case "Canceled": break;
                    default: throw new InvalidOperationException("未识别的 PCL 任务状态：" + state);
                }
            }
            return count;
        }
    }
}
