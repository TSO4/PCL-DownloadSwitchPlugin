// SPDX-License-Identifier: MIT
// 后台助手自己的进度窗。只显示真实阶段，不用虚假的百分比，也不修改 PCL 窗口。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using PclDownloadSwitch;

internal enum AutoAttachStage
{
    Waiting, Checking, Confirmation, Attaching, Attached, Existing, Declined, Stopped, Failed
}

internal sealed class AutoProgress : Form
{
    private readonly Label heading, description, elapsed, footer;
    private readonly Button closeButton, logButton;
    private readonly Timer animation;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Action openLog;
    private readonly Color muted = Color.FromArgb(148, 168, 193);
    private readonly Color blue = Color.FromArgb(83, 172, 255);
    private readonly Color green = Color.FromArgb(81, 211, 166);
    private readonly Color orange = Color.FromArgb(255, 179, 103);
    private int angle;
    private long closeAt = -1;
    private bool terminal, dismissing;
    private AutoAttachStage stage;
    internal AutoAttachStage Stage { get { return stage; } }
    internal bool IsTerminal { get { return terminal; } }
    internal string StatusText { get { return heading.Text; } }
    internal string DetailText { get { return description.Text; } }

    internal AutoProgress(IntPtr hostWindow, Action logAction = null)
    {
        openLog = logAction ?? OpenLog;
        Text = "PCL 下载开关 · 自动接入";
        AccessibleName = Text;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(480, 292);
        BackColor = Color.FromArgb(15, 27, 44);
        ForeColor = Color.FromArgb(238, 245, 255);
        Font = new Font("Microsoft YaHei UI", 9);
        DoubleBuffered = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        var area = (hostWindow == IntPtr.Zero ? Screen.PrimaryScreen : Screen.FromHandle(hostWindow)).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - 24), area.Top + 24);

        var title = new Label { Text = "PCL 下载开关   /   自动接入", AutoSize = true,
            Location = new Point(26, 23), ForeColor = muted };
        heading = new Label { AutoSize = false, Location = new Point(76, 75), Size = new Size(376, 33),
            Font = new Font("Microsoft YaHei UI", 16, FontStyle.Bold) };
        description = new Label { AutoSize = false, Location = new Point(28, 122), Size = new Size(424, 49),
            ForeColor = muted, AutoEllipsis = true };
        elapsed = new Label { AutoSize = false, Location = new Point(28, 220), Size = new Size(278, 22),
            ForeColor = muted };
        footer = new Label { AutoSize = false, Location = new Point(28, 258), Size = new Size(424, 18),
            Text = "Real Knife — The Origin of 404", ForeColor = muted, Font = new Font("Segoe UI", 9) };
        closeButton = new Button { Text = "", AccessibleName = "隐藏加载提示",
            Location = new Point(432, 12), Size = new Size(36, 34),
            FlatStyle = FlatStyle.Flat, BackColor = BackColor, TabIndex = 0 };
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 59, 82);
        closeButton.Paint += delegate(object sender, PaintEventArgs e) {
            using (var pen = new Pen(ForeColor, 1.5f))
            { e.Graphics.DrawLine(pen, 13, 12, 23, 22); e.Graphics.DrawLine(pen, 23, 12, 13, 22); }
        };
        closeButton.Click += delegate { Hide(); };
        logButton = new Button { Text = "查看日志", AccessibleName = "查看接入日志",
            Location = new Point(342, 216), Size = new Size(110, 32), FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(36, 61, 91), ForeColor = ForeColor, Visible = false, TabIndex = 1 };
        logButton.FlatAppearance.BorderColor = Color.FromArgb(64, 93, 126);
        logButton.Click += delegate {
            try { openLog(); }
            catch (Exception error)
            {
                DiagnosticsLog.Write("AUTO_OPEN_LOG", "无法打开日志。", error);
                MessageBox.Show(this, "无法打开日志，请手动查看：\n" + DiagnosticsLog.LogPath,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        Controls.AddRange(new Control[] { title, heading, description, elapsed, footer, closeButton, logButton });
        animation = new Timer { Interval = 80 };
        animation.Tick += delegate {
            angle = (angle + 18) % 360;
            if (!terminal)
            {
                elapsed.Text = "已等待 " + (int)clock.Elapsed.TotalSeconds + " 秒 · 可关闭提示";
                if (stage == AutoAttachStage.Waiting && clock.Elapsed.TotalSeconds >= 30)
                    description.Text = "PCL 启动时间较长，仍在等待主界面响应。您可以关闭此提示，助手会继续等待。";
                Invalidate(new Rectangle(25, 76, 42, 38));
            }
            if (closeAt >= 0 && clock.ElapsedMilliseconds >= closeAt) Dismiss();
        };
        SetStage(AutoAttachStage.Waiting);
        animation.Start();
    }

    // 自动出现时不抢走键盘焦点；用户仍可操作 PCL 或其他窗口。
    protected override bool ShowWithoutActivation { get { return true; } }

    internal void SetStage(AutoAttachStage next, string detail = null)
    {
        // 背景线程排队的阶段通知可能晚于完成通知到达；结束后不允许倒退。
        if (IsDisposed || dismissing || terminal) return;
        stage = next;
        terminal = next >= AutoAttachStage.Attached;
        switch (next)
        {
            case AutoAttachStage.Waiting:
                heading.Text = "正在等待 PCL 就绪";
                description.Text = "已检测到 PCL 启动。主界面准备好后，将检查兼容性并接入插件。";
                break;
            case AutoAttachStage.Checking:
                heading.Text = "正在检查兼容性";
                description.Text = "正在核对 PCL 版本、下载接口和插件文件，请稍候。";
                break;
            case AutoAttachStage.Confirmation:
                heading.Text = "等待您的选择";
                description.Text = "PCL 本身已有下载功能。请在确认窗口选择是否仍要接入插件。";
                break;
            case AutoAttachStage.Attaching:
                heading.Text = "正在接入插件";
                description.Text = "正在连接 PCL 并准备下载开关，请稍候。";
                break;
            case AutoAttachStage.Attached:
                heading.Text = "插件已接入";
                description.Text = "可在 PCL 右上角查看开关状态。若有下载或安装任务，自动启用会延后至任务结束。";
                break;
            case AutoAttachStage.Existing:
                heading.Text = "已有接入操作";
                description.Text = "检测到已加载的插件或正在进行的接入。保留当前开关状态，不重复接入。";
                break;
            case AutoAttachStage.Declined:
                heading.Text = "本次不接入";
                description.Text = "已按您的选择跳过，继续使用 PCL 原有的下载功能即可。";
                break;
            case AutoAttachStage.Stopped:
                heading.Text = "本次接入已停止";
                description.Text = "PCL 已退出或自动设置已改变，助手没有继续接入。";
                break;
            case AutoAttachStage.Failed:
                heading.Text = "本次接入已暂停";
                description.Text = "接入未完成。重新启动同一路径的 PCL 后会自动恢复，也可查看日志。";
                break;
        }
        if (!String.IsNullOrWhiteSpace(detail)) description.Text = detail;
        // 完整错误仍写入日志，长路径/异常不会挤破卡片。
        var tip = description.Tag as ToolTip;
        if (tip == null) { tip = new ToolTip(); description.Tag = tip; }
        tip.SetToolTip(description, description.Text);
        logButton.Visible = next == AutoAttachStage.Failed;
        if (terminal)
        {
            elapsed.Text = next == AutoAttachStage.Failed ? "等待下次 PCL 启动 · 可查看日志" : "提示将自动收起";
            closeAt = next == AutoAttachStage.Failed ? -1 : clock.ElapsedMilliseconds + 1800;
            if (next == AutoAttachStage.Failed && !Visible) Show();
        }
        DiagnosticsLog.Write("AUTO_PROGRESS", next + (detail == null ? "" : ": " + detail));
        Invalidate();
    }

    // Task.Run 回调统一回到助手 UI 线程；旧窗口的通知不影响下一次启动。
    internal void Report(AutoAttachStage next)
    {
        if (IsDisposed || dismissing || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => SetStage(next))); }
            catch (InvalidOperationException) { }
        }
        else SetStage(next);
    }

    internal void Reveal()
    {
        if (IsDisposed || dismissing) return;
        if (!Visible) Show();
    }

    internal void Dismiss()
    {
        if (IsDisposed || dismissing) return;
        dismissing = true;
        animation.Stop();
        Close();
        Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!dismissing && e.CloseReason == CloseReason.UserClosing)
        { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var border = new Pen(Color.FromArgb(55, 76, 103)))
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        Color accent = stage == AutoAttachStage.Failed ? orange :
            stage == AutoAttachStage.Attached || stage == AutoAttachStage.Existing ? green : blue;
        using (var pen = new Pen(accent, 3))
        {
            pen.StartCap = pen.EndCap = LineCap.Round;
            if (!terminal) g.DrawArc(pen, 31, 81, 27, 27, angle, 260);
            else if (stage == AutoAttachStage.Failed)
            { g.DrawEllipse(pen, 31, 81, 27, 27); g.DrawLine(pen, 44, 87, 44, 96); g.DrawLine(pen, 44, 101, 44, 102); }
            else
            { g.DrawLine(pen, 32, 95, 41, 103); g.DrawLine(pen, 41, 103, 59, 84); }
        }
        int current = stage == AutoAttachStage.Waiting ? 0 :
            stage == AutoAttachStage.Checking || stage == AutoAttachStage.Confirmation ? 1 : 2;
        var titles = new[] { "等待就绪", "兼容检查", "接入插件" };
        for (int i = 0; i < titles.Length; i++)
        {
            var color = i <= current ? accent : Color.FromArgb(62, 79, 102);
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, 29 + i * 148, 185, 8, 8);
            TextRenderer.DrawText(g, titles[i], Font, new Point(44 + i * 148, 180),
                i <= current ? ForeColor : muted);
        }
    }

    private static void OpenLog()
    {
        DiagnosticsLog.Write("AUTO_LOG_OPEN", "从加载提示打开日志。");
        if (!File.Exists(DiagnosticsLog.LogPath))
            throw new FileNotFoundException("日志尚未创建。", DiagnosticsLog.LogPath);
        Process.Start(new ProcessStartInfo(DiagnosticsLog.LogPath) { UseShellExecute = true });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animation.Dispose();
            var tip = description.Tag as ToolTip;
            if (tip != null) tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
