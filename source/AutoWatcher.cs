// SPDX-License-Identifier: MIT
// 用户明确开启后的后台助手。只观察设置里指定的 PCL 路径，托盘可随时暂停或退出。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using PclDownloadSwitch;
using CancellationTokenSource = System.Threading.CancellationTokenSource;

internal sealed class AutoWatcher : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly Timer timer;
    private readonly HashSet<string> attempted = new HashSet<string>();
    private readonly Dictionary<string, DateTime> readySince = new Dictionary<string, DateTime>();
    private readonly List<RecentAttach> recent = new List<RecentAttach>();
    private readonly HashSet<int> observationWarnings = new HashSet<int>();
    private AutoProgress progress;
    private string progressKey;
    private CancellationTokenSource attachCancellation;
    private bool busy, exiting, waitingForRestart;
    private sealed class RecentAttach { internal int Pid; internal long Started; internal DateTime At; }

    internal AutoWatcher()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("暂停本次接入（下次 PCL 启动恢复）", null, delegate {
            PauseTemporary(progressKey, "已暂停本次接入。已经开始的接入操作可能仍在完成。");
        });
        menu.Items.Add("关闭自动接入（保持关闭）", null, delegate { DisableAutomatic(); });
        menu.Items.Add("退出后台助手（本次会话）", null, delegate { ExitThread(); });
        tray = new NotifyIcon { Icon = SystemIcons.Information, Visible = true,
            Text = "PCL 下载开关 · 等待指定的 PCL", ContextMenuStrip = menu };
        tray.DoubleClick += delegate {
            if (progress != null && !progress.IsDisposed) { progress.Reveal(); return; }
            MessageBox.Show("后台助手会等待设置中指定的 PCL，在主界面就绪后自动接入并启用下载。" +
                "\n\n在 PCL 的插件详情 → 设置中更改选项；托盘右键可暂停或退出。" +
                "\n临时暂停会在同一路径的 PCL 重新启动时恢复；主动关闭自动模式则保持关闭。" +
                "\n\n当前目标：" + SettingsStore.Load().TargetPath,
                "PCL 下载开关 · 后台助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        timer = new Timer { Interval = 500 };
        timer.Tick += Tick;
        timer.Start();
        DiagnosticsLog.Write("AUTO_WATCH", "后台助手已启动，托盘可见；仅观察设置中的 PCL。");
    }

    private async void Tick(object sender, EventArgs args)
    {
        if (busy || exiting) return;
        busy = true;
        try
        {
            var settings = SettingsStore.Load();
            if (!settings.AutoAttach)
            {
                tray.Text = "PCL 下载开关 · 自动接入已关闭";
                if (waitingForRestart && progress != null && progress.IsTerminal) progress.Dismiss();
                waitingForRestart = false;
                StopProgress("自动接入已关闭，助手停止等待。");
                return;
            }
            foreach (var record in recent.ToArray())
            {
                bool alive = false;
                try { using (var p = Process.GetProcessById(record.Pid)) alive = !p.HasExited && p.StartTime.ToUniversalTime().Ticks == record.Started; }
                catch (ArgumentException) { }
                if (!alive)
                {
                    recent.Remove(record);
                    if (ShortExitShouldPause(record.At, DateTime.UtcNow))
                    {
                        PauseTemporary(InstanceKey(record.Pid, record.Started),
                            "PCL 在接入后较快退出，本次接入已暂停。退出原因未确定。");
                    }
                }
                else if (!ShortExitShouldPause(record.At, DateTime.UtcNow)) recent.Remove(record);
            }
            tray.Text = waitingForRestart ? "PCL 下载开关 · 等待下次 PCL 启动" : "PCL 下载开关 · 等待指定的 PCL";
            int session;
            using (var self = Process.GetCurrentProcess()) session = self.SessionId;
            var live = new HashSet<string>();
            var seenPids = new HashSet<int>();
            foreach (var target in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(settings.TargetPath)))
            {
                using (target)
                {
                    seenPids.Add(target.Id);
                    string key;
                    long started;
                    try
                    {
                        if (target.SessionId != session ||
                            !String.Equals(target.MainModule.FileName, settings.TargetPath, StringComparison.OrdinalIgnoreCase)) continue;
                        started = target.StartTime.ToUniversalTime().Ticks;
                        key = InstanceKey(target.Id, started);
                        live.Add(key);
                        if (attempted.Contains(key)) continue;
                        observationWarnings.Remove(target.Id);
                        BeginProgress(key, target.MainWindowHandle);
                        if (target.MainWindowHandle == IntPtr.Zero || !target.Responding) { readySince.Remove(key); continue; }
                    }
                    catch (InvalidOperationException) { continue; }
                    catch (System.ComponentModel.Win32Exception error)
                    {
                        // 无法核实路径的同名进程不接入，也不反复弹窗或关闭用户的自动设置。
                        if (observationWarnings.Add(target.Id))
                            DiagnosticsLog.Write("AUTO_OBSERVE", "无法观察同名进程，请让目标 PCL 和插件使用相同的普通用户权限。", error);
                        continue;
                    }
                    DateTime ready;
                    if (!readySince.TryGetValue(key, out ready)) { readySince[key] = DateTime.UtcNow; continue; }
                    // 两次观察到窗口响应后立即检查，不再固定等待三秒。
                    if (DateTime.UtcNow - ready < TimeSpan.FromMilliseconds(350)) continue;
                    // 再读设置，用户刚暂停/改目标时不使用旧快照发起接入。
                    var latest = SettingsStore.Load();
                    if (!latest.AutoAttach || !String.Equals(latest.TargetPath, settings.TargetPath, StringComparison.OrdinalIgnoreCase))
                    { StopProgress("自动设置已改变，本次未继续接入。"); return; }
                    attempted.Add(key);
                    tray.Text = "PCL 下载开关 · 正在检查并接入";
                    var currentProgress = progress;
                    attachCancellation = new CancellationTokenSource();
                    var token = attachCancellation.Token;
                    Launcher.AttachOutcome outcome;
                    try { outcome = await Task.Run(() => Launcher.AttachTarget(target, true, currentProgress.Report, token)); }
                    finally { attachCancellation.Dispose(); attachCancellation = null; }
                    if (exiting) return;
                    if (outcome == Launcher.AttachOutcome.Attached)
                        recent.Add(new RecentAttach { Pid = target.Id, Started = started, At = DateTime.UtcNow });
                    currentProgress.SetStage(outcome == Launcher.AttachOutcome.Attached ? AutoAttachStage.Attached :
                        outcome == Launcher.AttachOutcome.Existing ? AutoAttachStage.Existing :
                        outcome == Launcher.AttachOutcome.Declined ? AutoAttachStage.Declined : AutoAttachStage.Stopped);
                }
            }
            if (progressKey != null && !live.Contains(progressKey)) StopProgress("PCL 已退出，本次未继续接入。");
            foreach (var key in new List<string>(readySince.Keys)) if (!live.Contains(key)) readySince.Remove(key);
            attempted.RemoveWhere(k => !live.Contains(k));
            observationWarnings.RemoveWhere(pid => !seenPids.Contains(pid));
        }
        catch (Exception error)
        {
            DiagnosticsLog.Write("AUTO_ERROR", "自动接入失败，不循环重试。", error);
            PauseTemporary(progressKey, "自动接入失败：" + error.GetBaseException().Message);
        }
        finally { busy = false; }
    }

    internal static bool ShortExitShouldPause(DateTime attached, DateTime now)
    { return now >= attached && now - attached < TimeSpan.FromSeconds(20); }
    internal static string InstanceKey(int pid, long started) { return pid + ":" + started; }

    private void BeginProgress(string key, IntPtr hostWindow)
    {
        if (progressKey == key && progress != null && !progress.IsDisposed && !progress.IsTerminal) return;
        if (progress != null) progress.Dismiss();
        if (waitingForRestart)
        {
            waitingForRestart = false;
            DiagnosticsLog.Write("AUTO_RESUME", "检测到同一路径的新 PCL 启动实例，恢复自动检查与接入；实例=" + key);
        }
        progressKey = key;
        progress = new AutoProgress(hostWindow);
        progress.Show();
    }

    private void StopProgress(string reason)
    {
        if (progress != null && !progress.IsDisposed && !progress.IsTerminal)
            progress.SetStage(AutoAttachStage.Stopped, reason);
    }

    private void PauseTemporary(string key, string reason)
    {
        if (attachCancellation != null) attachCancellation.Cancel();
        if (key != null) attempted.Add(key);
        waitingForRestart = true;
        // 保留用户开启的自动/登录选项；同一启动实例不循环重试，新实例重新通过兼容检查。
        DiagnosticsLog.Write("AUTO_TEMP_PAUSED", reason + " 重新启动对应的 PCL 后自动恢复；实例=" + (key ?? "无"));
        if (!exiting)
        {
            tray.Text = "PCL 下载开关 · 等待下次 PCL 启动";
            if (progress == null || progress.IsDisposed || progress.IsTerminal)
            {
                if (progress != null) progress.Dismiss();
                progress = new AutoProgress(IntPtr.Zero);
            }
            progress.SetStage(AutoAttachStage.Failed, reason + "\n重新启动同一路径的 PCL 后自动恢复。");
        }
    }
    private void DisableAutomatic()
    {
        try
        {
            if (attachCancellation != null) attachCancellation.Cancel();
            AutoController.Disable("用户从托盘关闭自动模式；不会在新窗口出现时自行开启。");
            waitingForRestart = false;
            if (progress != null) progress.Dismiss();
            tray.Text = "PCL 下载开关 · 自动接入已关闭";
            tray.ShowBalloonTip(5000, "PCL 下载开关 · 已关闭自动接入", "之后重新打开 PCL 也不会自动接入。可在插件设置中重新开启。", ToolTipIcon.Info);
        }
        catch (Exception error) { DiagnosticsLog.Write("AUTO_DISABLE_ERROR", "关闭自动模式失败。", error); timer.Stop(); }
    }
    protected override void ExitThreadCore()
    {
        exiting = true;
        timer.Stop();
        if (attachCancellation != null) attachCancellation.Cancel();
        if (progress != null) progress.Dismiss();
        tray.Visible = false;
        DiagnosticsLog.Write("AUTO_EXIT", "用户退出后台助手；当前 PCL 的开关状态保持不变。");
        base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { if (progress != null) progress.Dismiss(); timer.Dispose(); tray.Dispose(); }
        base.Dispose(disposing);
    }
}
