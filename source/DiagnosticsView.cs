// SPDX-License-Identifier: MIT
// 日志查看页面：手动刷新、复制，不上传或自动发送诊断信息。
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PclDownloadSwitch
{
    internal sealed class DiagnosticsView : Grid
    {
        private readonly TextBox log;
        private readonly TextBlock state;

        internal DiagnosticsView()
        {
            Margin = new Thickness(2, 5, 2, 2);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = Ui.Text("诊断与日志", 22, "#EFF5FF");
            title.Margin = new Thickness(0, 0, 0, 14);
            Children.Add(title);
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            SetRow(toolbar, 1);
            Children.Add(toolbar);
            var refresh = Ui.Button("刷新", "刷新插件日志", 76);
            var copy = Ui.Button("复制日志", "复制当前显示的诊断信息", 100);
            copy.Margin = new Thickness(10, 0, 0, 0);
            toolbar.Children.Add(refresh);
            toolbar.Children.Add(copy);
            state = Ui.Text("", 11, "#AFC1DB");
            state.TextWrapping = TextWrapping.Wrap;
            state.Margin = new Thickness(0, 0, 0, 10);
            SetRow(state, 2);
            Children.Add(state);
            log = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 11,
                Background = Ui.Brush("#0B1320"), Foreground = Ui.Brush("#D9E8FA"),
                BorderBrush = Ui.Brush("#354B65"), Padding = new Thickness(12),
                TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            AutomationProperties.SetName(log, "插件日志，可选择复制");
            SetRow(log, 3);
            Children.Add(log);
            var note = Ui.Text("仅在本地记录插件行为与异常；可能包含本机路径。复制后请先查看内容。\n接入失败时也可直接打开插件目录下的 data\\plugin.log。", 10, "#849BBA");
            note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 19;
            note.Margin = new Thickness(0, 10, 0, 0);
            SetRow(note, 4);
            Children.Add(note);
            refresh.Click += delegate { Refresh(); copy.Content = "复制日志"; };
            copy.Click += delegate
            {
                try { Clipboard.SetText(state.Text + "\n\n" + log.Text); copy.Content = "已复制 ✓"; }
                catch (Exception error)
                {
                    DiagnosticsLog.Write("LOG_COPY", "复制日志失败。", error);
                    copy.Content = "复制失败";
                    state.Text = "复制失败：" + error.Message + "\n可手动选中下方文本，按 Ctrl+C。";
                }
            };
            Loaded += delegate { Refresh(); };
            Refresh();
        }

        internal void Refresh()
        {
            state.Text = "插件 " + ReleaseInfo.Version + " · PCL " + Runtime.HostVersion + " · " +
                (Runtime.NativeAvailable ? "原生下载可用" : "开关" + (Runtime.Enabled ? "已启用" : "已关闭")) +
                " · " + (Runtime.SwitchLocked ? Runtime.LockReason : "当前允许切换") +
                "\n日志：" + DiagnosticsLog.LogPath;
            if (DiagnosticsLog.LastWriteFailure.Length != 0)
                state.Text += "\n日志写入失败：" + DiagnosticsLog.LastWriteFailure;
            log.Text = DiagnosticsLog.Read();
            log.ScrollToEnd();
        }
    }
}
