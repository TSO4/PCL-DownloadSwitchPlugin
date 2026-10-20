// SPDX-License-Identifier: MIT
// 自动接入、登录启动均须用户在这里主动确认；设置只写到插件自己的 data 目录。
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PclDownloadSwitch
{
    internal sealed class SettingsView : ScrollViewer
    {
        private readonly CheckBox automatic, login;
        private readonly TextBlock state;
        private bool updating;
        internal SettingsView()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            var stack = new StackPanel { Margin = new Thickness(12, 8, 16, 12) };
            Content = stack;
            stack.Children.Add(Ui.Text("设置", 24, "#EFF5FF"));
            AddText(stack, "当前 PCL " + Runtime.HostVersion + "\n" +
                (Runtime.NativeAvailable ? "检测结果：本身可下载，无需恢复。" :
                Runtime.VerifiedBuild ? "检测结果：已核对的构建，下载页接口匹配。" :
                "检测结果：结构匹配；此构建尚未实机验证。") +
                "\n未发布的未来版本无法保证兼容，结构不符时会停止接入。");
            automatic = Option("启动 PCL 后自动接入并启用下载");
            login = Option("登录 Windows 时启动后台助手");
            stack.Children.Add(automatic);
            AddText(stack, "默认关闭。检测到当前目录的 PCL 后显示加载进度，主界面就绪后检查并接入，完成自动收起；失败可查看日志。原本可下载时仍会询问是否继续。");
            AddText(stack, "快速关闭 PCL 或接入出错时只暂停本次，重新启动同一路径的 PCL 会自动恢复。托盘“关闭自动接入”或取消本页勾选会保持关闭。");
            stack.Children.Add(login);
            AddText(stack, "单独开启后才会创建当前用户的登录启动快捷方式。关闭自动模式会一并取消它。未开启时，助手只在本次 Windows 会话内运行。");
            AddText(stack, "自动接入目标（仅此目录）：\n" + Runtime.HostPath);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 12) };
            var start = Ui.Button("启动后台助手", "启动已启用的后台助手", 140);
            var refresh = Ui.Button("刷新状态", "刷新自动模式设置", 100);
            refresh.Margin = new Thickness(10, 0, 0, 0);
            actions.Children.Add(start); actions.Children.Add(refresh); stack.Children.Add(actions);
            state = Ui.Text("", 12, "#AFC1DB");
            state.TextWrapping = TextWrapping.Wrap; stack.Children.Add(state);
            automatic.Click += delegate { ChangeAutomatic(); };
            login.Click += delegate { ChangeLogin(); };
            refresh.Click += delegate { Refresh(); };
            start.Click += delegate {
                try
                {
                    if (!SettingsStore.Load().AutoAttach) { state.Text = "请先开启并确认上面的自动模式。"; return; }
                    AutoController.StartWatcher();
                    state.Text = "已请求启动助手。请查看系统托盘图标；右键可暂停或退出。";
                }
                catch (Exception error) { Error(error); }
            };
            Loaded += delegate { Refresh(); };
            Refresh();
        }
        private static CheckBox Option(string label)
        {
            var box = new CheckBox { Content = Ui.Text(label, 14, "#EFF5FF"),
                Margin = new Thickness(0, 12, 0, 4), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, label);
            return box;
        }
        private static void AddText(Panel parent, string text)
        {
            var note = Ui.Text(text, 12, "#AFC1DB");
            note.LineHeight = 22; note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 8, 0, 10);
            parent.Children.Add(note);
        }
        internal void Refresh()
        {
            updating = true;
            var settings = SettingsStore.Load();
            automatic.IsChecked = settings.AutoAttach;
            login.IsChecked = settings.StartWithWindows;
            login.IsEnabled = settings.AutoAttach;
            state.Text = "自动模式：" + (settings.AutoAttach ? "已开启" : "已关闭") +
                "  /  登录启动：" + (settings.StartWithWindows ? "已开启" : "已关闭") +
                "\n后台助手可从托盘退出。此处显示保存的设置，不代表助手当前一定在运行。";
            updating = false;
        }
        private void ChangeAutomatic()
        {
            if (updating) return;
            try
            {
                if (automatic.IsChecked != true) { AutoController.Disable("用户在设置中关闭自动模式。"); Refresh(); return; }
                if (MessageBox.Show(Window.GetWindow(this), ReleaseInfo.AutomaticWarning, "启用自动接入",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                { Refresh(); return; }
                var settings = SettingsStore.Load();
                settings.AutoAttach = true; settings.TargetPath = Runtime.HostPath;
                SettingsStore.Save(settings);
                AutoController.StartWatcher();
                DiagnosticsLog.Write("AUTO_ENABLED", "用户确认自动接入，目标：" + settings.TargetPath);
                Refresh();
                state.Text += "\n当前已经接入的 PCL 保持手动开关状态；下次启动 PCL 时自动生效。";
            }
            catch (Exception error) { Error(error); }
        }
        private void ChangeLogin()
        {
            if (updating) return;
            try
            {
                var settings = SettingsStore.Load();
                bool enable = login.IsChecked == true;
                if (enable && (!settings.AutoAttach || MessageBox.Show(Window.GetWindow(this),
                    "开启后，会在当前 Windows 用户的启动文件夹创建本插件的快捷方式，登录时运行可见的托盘助手。" +
                    "\n\n移动或删除插件前请先关闭此选项。是否开启？", "登录启动",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes))
                { Refresh(); return; }
                // 先保存关闭状态，创建失败不会留下“已启用”的假状态。
                settings.StartWithWindows = false; SettingsStore.Save(settings);
                AutoController.SetLoginStartup(enable);
                settings.StartWithWindows = enable;
                try { SettingsStore.Save(settings); }
                catch { AutoController.SetLoginStartup(false); throw; }
                Refresh();
            }
            catch (Exception error) { Error(error); }
        }
        private void Error(Exception error)
        {
            DiagnosticsLog.Write("SETTINGS_ERROR", "设置操作失败。", error);
            Refresh();
            state.Text = "设置失败：" + error.GetBaseException().Message + "\n请到“诊断与日志”查看 SETTINGS_ERROR。";
        }
    }
}
